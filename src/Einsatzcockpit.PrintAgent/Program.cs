using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Logging;
using Einsatzcockpit.PrintAgent.Pairing;
using Einsatzcockpit.PrintAgent.Security;
using Einsatzcockpit.PrintAgent.Tray;
using Einsatzcockpit.PrintAgent.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var paths = new AgentPaths();
paths.EnsureDirectories();
if (args.Length > 0)
{
    return await RunCommandAsync(args, paths);
}

var options = AgentOptions.Load(paths);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(service =>
{
    service.ServiceName = "EinsatzcockpitPrintAgent";
});
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(ParseLogLevel(options.LogLevel));
builder.Logging.AddEventLog(eventLog =>
{
    eventLog.SourceName = "Einsatzcockpit Druckagent";
});
builder.Logging.Services.AddSingleton<ILoggerProvider>(new FileLoggerProvider(paths.LogDirectory));
builder.Services.AddSingleton(paths);
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
return 0;

static async Task<int> RunCommandAsync(string[] args, AgentPaths paths)
{
    switch (args[0].ToLowerInvariant())
    {
        case "tray":
            return RunTray(paths);
        case "pair":
            return await PairAsync(args, paths);
        case "status":
            PrintStatus(paths);
            return 0;
        case "unpair":
            using (var factory = LoggerFactory.Create(logging =>
                logging.AddProvider(new FileLoggerProvider(paths.LogDirectory))))
            {
                new DpapiTokenStore(paths.TokenPath, factory.CreateLogger<DpapiTokenStore>()).Delete();
            }
            Console.WriteLine("Device-Token wurde gelöscht.");
            return 0;
        case "--help":
        case "-h":
        case "help":
            Console.WriteLine("EinsatzcockpitPrintAgent.exe pair --url <cloud> --code <CODE>");
            Console.WriteLine("EinsatzcockpitPrintAgent.exe status");
            Console.WriteLine("EinsatzcockpitPrintAgent.exe unpair");
            Console.WriteLine("EinsatzcockpitPrintAgent.exe tray");
            return 0;
        default:
            Console.Error.WriteLine("Unbekannter Befehl. --help zeigt die verfügbaren Befehle.");
            return 1;
    }
}

static int RunTray(AgentPaths paths)
{
    using var mutex = new Mutex(true, @"Local\EinsatzcockpitPrintAgentTray", out var createdNew);
    if (!createdNew)
    {
        return 0;
    }

    var exitCode = 0;
    var thread = new Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext(paths));
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return exitCode;
}

static async Task<int> PairAsync(string[] args, AgentPaths paths)
{
    var url = ValueAfter(args, "--url");
    var code = ValueAfter(args, "--code");
    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(code))
    {
        Console.Error.WriteLine("Verwendung: pair --url <cloud> --code <CODE>");
        return 1;
    }

    try
    {
        var options = AgentOptions.Load(paths);
        using var factory = LoggerFactory.Create(logging =>
            logging.AddProvider(new FileLoggerProvider(paths.LogDirectory)));
        var tokens = new DpapiTokenStore(paths.TokenPath, factory.CreateLogger<DpapiTokenStore>());
        var client = new PairingClient(tokens);
        var result = await client.PairAsync(url, code, options.Proxy, CancellationToken.None);
        AgentOptions.Save(paths, options with { CloudUrl = url });
        Console.WriteLine($"Gekoppelt als {result.Name} ({result.GatewayId}).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Pairing fehlgeschlagen: " + ex.Message);
        return 1;
    }
}

static void PrintStatus(AgentPaths paths)
{
    var snapshot = new Einsatzcockpit.PrintAgent.Core.Status.StatusFile(paths.StatusPath).Read();
    if (snapshot is not null)
    {
        Console.WriteLine($"Version: {snapshot.Version}");
        Console.WriteLine("Verbunden: " + (snapshot.Connected ? "ja" : "nein"));
    }
    var options = AgentOptions.Load(paths);
    Console.WriteLine("Datenverzeichnis: " + paths.DataDirectory);
    Console.WriteLine("Cloud-URL: " + (options.CloudUrl ?? "nicht konfiguriert"));
    Console.WriteLine("Gekoppelt: " + (File.Exists(paths.TokenPath) ? "ja" : "nein"));
    var store = new JobStore(paths.DatabasePath);
    foreach (var count in store.StateCounts())
    {
        Console.WriteLine($"Jobs {count.State}: {count.Count}");
    }
    foreach (var job in store.Recent(10))
    {
        Console.WriteLine($"{job.JobId} {job.State} {job.UpdatedAt:O}");
    }
}

static string? ValueAfter(IReadOnlyList<string> args, string name)
{
    for (var index = 0; index < args.Count; index++)
    {
        if (string.Equals(args[index], name, StringComparison.Ordinal))
        {
            return index + 1 < args.Count ? args[index + 1] : null;
        }
    }
    return null;
}

static LogLevel ParseLogLevel(string value) =>
    Enum.TryParse<LogLevel>(value, true, out var level) ? level : LogLevel.Information;
