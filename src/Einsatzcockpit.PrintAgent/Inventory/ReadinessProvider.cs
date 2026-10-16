using System.Management;
using System.Net.Sockets;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Printing;

namespace Einsatzcockpit.PrintAgent.Inventory;

/// <summary>
/// Prüft je Route, ob sie jetzt drucken kann. Nur bereite Routen bekommen von der Cloud Jobs;
/// eine nicht bereite Route lässt den Job an den nächsten Agent (Priorität) weitergehen.
/// </summary>
public sealed class ReadinessProvider(IReadOnlyCollection<string> allowedCommands) : IReadinessProvider
{
    private const uint PrinterPaused = 0x00000001;
    private const uint PrinterError = 0x00000002;
    private const uint PrinterPaperOut = 0x00000010;
    private const uint PrinterOffline = 0x00000080;
    private const uint PrinterNotAvailable = 0x00001000;

    private readonly HashSet<string> _allowed = allowedCommands
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<RouteReadiness>> GetReadinessAsync(IReadOnlyList<RouteConfig> routes,
        CancellationToken cancellationToken)
    {
        var ports = routes.Any(r => r.RenderMode is "driver" or "pdf_direct") ? TcpPorts() : [];
        var result = new List<RouteReadiness>();
        foreach (var route in routes)
        {
            try
            {
                result.Add(await CheckAsync(route, ports, cancellationToken));
            }
            catch (Exception ex)
            {
                result.Add(State(route, false, "error", ex.Message));
            }
        }
        return result;
    }

    private async Task<RouteReadiness> CheckAsync(RouteConfig route,
        IReadOnlyDictionary<string, (string Host, int Port)> ports, CancellationToken token)
    {
        switch (route.RenderMode)
        {
            case "folder":
                return await CheckFolderAsync(route, token);
            case "command":
                return CheckCommand(route);
            case "ipp_direct":
                var ziel = CommandSubmitter.Option(route, "target_uri").ValueKind == JsonValueKind.String
                    ? CommandSubmitter.Option(route, "target_uri").GetString()
                    : route.LocalQueue;
                if (!Uri.TryCreate(ziel, UriKind.Absolute, out var uri))
                {
                    return State(route, false, "unreachable", "Ungültige Ziel-URI.");
                }
                var ippPort = uri.Port > 0 ? uri.Port : 631;
                return await TcpAsync(route, uri.Host, ippPort, Identity(uri.Host), token);
        }

        // driver / pdf_direct: Windows-Warteschlange
        Printing.Native.Winspool.PRINTER_INFO_2 printer;
        try
        {
            WindowsSpooler.TryGetPrinter(route.LocalQueue, out printer);
        }
        catch
        {
            return State(route, false, "queue_missing", $"Warteschlange \"{route.LocalQueue}\" nicht gefunden.");
        }
        if ((printer.Status & (PrinterOffline | PrinterNotAvailable)) != 0)
        {
            return State(route, false, "offline", "Drucker ist offline.");
        }
        if ((printer.Status & PrinterPaused) != 0)
        {
            return State(route, false, "paused", "Warteschlange ist angehalten.");
        }
        if ((printer.Status & PrinterPaperOut) != 0)
        {
            return State(route, false, "error", "Kein Papier.");
        }
        if ((printer.Status & PrinterError) != 0)
        {
            return State(route, false, "error", "Drucker meldet einen Fehler.");
        }
        if (printer.pPortName is { } portName && ports.TryGetValue(portName, out var endpoint))
        {
            // Netzwerkdrucker: wirklich erreichbar? Sonst würde der Job im Spooler hängen,
            // statt dass der redundante Agent übernimmt.
            return await TcpAsync(route, endpoint.Host, endpoint.Port, Identity(endpoint.Host), token);
        }
        return State(route, true, "ready", null);
    }

    private static async Task<RouteReadiness> CheckFolderAsync(RouteConfig route, CancellationToken token)
    {
        var option = CommandSubmitter.Option(route, "target_dir");
        var pfad = option.ValueKind == JsonValueKind.String ? option.GetString() : null;
        if (string.IsNullOrWhiteSpace(pfad) || !Directory.Exists(pfad))
        {
            return State(route, false, "not_writable", "Zielverzeichnis fehlt.");
        }
        try
        {
            var probe = Path.Combine(pfad, $".printagent-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "", token);
            File.Delete(probe);
            return State(route, true, "ready", null);
        }
        catch
        {
            return State(route, false, "not_writable", "Zielverzeichnis nicht beschreibbar.");
        }
    }

    private RouteReadiness CheckCommand(RouteConfig route)
    {
        var command = CommandSubmitter.Option(route, "command");
        var programm = command.ValueKind == JsonValueKind.Array && command.GetArrayLength() > 0
            ? Path.GetFullPath(command[0].GetString() ?? "")
            : "";
        if (!_allowed.Contains(programm))
        {
            return State(route, false, "not_allowed", "Programm steht nicht in der lokalen Allowlist (agent.json).");
        }
        return File.Exists(programm)
            ? State(route, true, "ready", null)
            : State(route, false, "not_allowed", "Programm nicht gefunden.");
    }

    /// <summary>Win32_TCPIPPrinterPort: Portname → (Host, Port) je nach Protokoll (RAW/LPR).</summary>
    internal static Dictionary<string, (string Host, int Port)> TcpPorts()
    {
        var result = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name,HostAddress,PortNumber,Protocol FROM Win32_TCPIPPrinterPort");
            foreach (ManagementObject item in search.Get())
            {
                var name = item["Name"]?.ToString();
                var host = item["HostAddress"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
                {
                    continue;
                }
                var protocol = Convert.ToInt32(item["Protocol"] ?? 1); // 1 = RAW, 2 = LPR
                var port = Convert.ToInt32(item["PortNumber"] ?? 0);
                result[name] = (host, port > 0 ? port : protocol == 2 ? 515 : 9100);
            }
        }
        catch
        {
            // WMI nicht verfügbar → nur Spoolerstatus prüfen.
        }
        return result;
    }

    internal static JsonElement Identity(string host) => JsonSerializer.SerializeToElement(new { ip = host, host });

    private static async Task<RouteReadiness> TcpAsync(RouteConfig route, string host, int port,
        JsonElement identity, CancellationToken token)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return State(route, true, "ready", null, identity);
        }
        catch
        {
            return State(route, false, "unreachable", $"{host}:{port} nicht erreichbar.", identity);
        }
    }

    private static RouteReadiness State(RouteConfig route, bool ready, string state, string? detail,
        JsonElement? identity = null) => new(route.RouteId, ready, state, detail, identity);
}
