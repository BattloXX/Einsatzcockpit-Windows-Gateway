using System.Net;
using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Cloud;
using Einsatzcockpit.PrintAgent.Core.Configuration;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Core.Runtime;
using Einsatzcockpit.PrintAgent.Fetching;
using Einsatzcockpit.PrintAgent.Inventory;
using Einsatzcockpit.PrintAgent.Printing;
using Einsatzcockpit.PrintAgent.Rendering;
using Einsatzcockpit.PrintAgent.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Worker;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentWorker> _logger;
    private AgentRuntime? _runtime;

    public AgentWorker(AgentPaths paths, ILoggerFactory loggerFactory, ILogger<AgentWorker> logger)
    {
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _paths.EnsureDirectories();
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = Configuration.AgentOptions.Load(_paths);
            var tokenStore = new DpapiTokenStore(_paths.TokenPath,
                _loggerFactory.CreateLogger<DpapiTokenStore>());
            var token = ReadToken(tokenStore);
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(options.CloudUrl))
            {
                _logger.LogError("Nicht gekoppelt - EinsatzcockpitPrintAgent.exe pair --url ... --code ...");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                _runtime = CreateRuntime(options, token);
                await _runtime.StartAsync(stoppingToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Druckagent konnte nicht gestartet werden.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            finally
            {
                if (_runtime is not null)
                {
                    await _runtime.DisposeAsync();
                    _runtime = null;
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private AgentRuntime CreateRuntime(Einsatzcockpit.PrintAgent.Core.Configuration.AgentOptions options,
        string token)
    {
        var store = new JobStore(_paths.DatabasePath);
        var windowsSpooler = new WindowsSpooler();
        var ipp = new IppDirectSubmitter();
        var spooler = new CompositeSpooler(windowsSpooler, ipp);
        var submitters = new SubmitterRegistry([
            new DriverPrintSubmitter(windowsSpooler),
            new RawPdfSubmitter(),
            ipp,
            new CommandSubmitter(options.EffectiveAllowedCommands),
            new FolderSubmitter()
        ]);
        var renderer = new EdgeHtmlRenderer(options.EdgePath, options.HtmlReadyTimeoutS,
            _loggerFactory.CreateLogger<EdgeHtmlRenderer>());
        var fetcher = new HttpArtifactFetcher(_paths.SpoolDirectory, options.Proxy, renderer);
        var proxy = string.IsNullOrWhiteSpace(options.Proxy) ? null : new WebProxy(options.Proxy);
        var transport = new ClientWebSocketTransport(new WebSocketTransportOptions(proxy));
        var cloud = new CloudConnection(transport, AgentConfiguration.WebSocketUri(options.CloudUrl!), token, store);
        var processor = new JobProcessor(store, cloud, fetcher, submitters, spooler, new SystemClock(),
            _loggerFactory.CreateLogger<JobProcessor>());
        var recovery = new RecoveryService(store, spooler, new SystemClock(),
            _loggerFactory.CreateLogger<RecoveryService>());
        var readiness = new ReadinessProvider(options.EffectiveAllowedCommands);
        var inventory = new PrinterInventory();
        var assembly = typeof(AgentWorker).Assembly.GetName();
        var version = assembly.Version?.ToString() ?? "unbekannt";
        var hostInfo = new HostInfo(Environment.MachineName, Environment.OSVersion.VersionString,
            Environment.MachineName);
        return new AgentRuntime(store, recovery, processor, cloud, readiness, inventory, version, hostInfo);
    }

    private string? ReadToken(ITokenStore store)
    {
        try
        {
            return store.Read();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gespeichertes Device-Token konnte nicht gelesen werden.");
            return null;
        }
    }
}
