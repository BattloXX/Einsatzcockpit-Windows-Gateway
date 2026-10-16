using System.Drawing.Printing;
using System.Management;
using System.Net.Sockets;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Inventory;

public sealed class PrinterInventory : IInventoryProvider
{
    public async Task<IReadOnlyList<QueueInfo>> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var ports = new Dictionary<string, (string? Host, int Port)>();
        using (var search = new ManagementObjectSearcher("SELECT Name,HostAddress,PortNumber FROM Win32_TCPIPPrinterPort")) foreach (ManagementObject item in search.Get()) ports[item["Name"]?.ToString() ?? ""] = (item["HostAddress"]?.ToString(), Convert.ToInt32(item["PortNumber"] ?? 0));
        var result = new List<QueueInfo>();
        using var printers = new ManagementObjectSearcher("SELECT Name,DriverName,PortName,WorkOffline,PrinterStatus,ExtendedPrinterStatus,Shared,Network FROM Win32_Printer");
        foreach (ManagementObject printer in printers.Get())
        {
            var queue = printer["Name"]?.ToString() ?? ""; var port = printer["PortName"]?.ToString(); ports.TryGetValue(port ?? "", out var endpoint);
            JsonElement? identity = endpoint.Host is null ? null : JsonSerializer.SerializeToElement(new { ip = endpoint.Host, host = endpoint.Host });
            var capabilities = Capabilities(queue); var status = Convert.ToBoolean(printer["WorkOffline"] ?? false) ? "offline" : printer["PrinterStatus"]?.ToString();
            result.Add(new QueueInfo(queue, printer["DriverName"]?.ToString(), port, endpoint.Host, identity, capabilities, status));
        }
        return result;
    }
    private static JsonElement? Capabilities(string queue)
    {
        try { var settings = new PrinterSettings { PrinterName = queue }; var sizes = settings.PaperSizes.Cast<PaperSize>().ToArray(); var media = new[] { (PaperKind.A4, "A4"), (PaperKind.A3, "A3") }.Where(k => sizes.Any(x => x.Kind == k.Item1 || x.PaperName.StartsWith(k.Item2, StringComparison.OrdinalIgnoreCase))).Select(k => k.Item2).ToArray(); return JsonSerializer.SerializeToElement(new { media, media_all = sizes.Select(x => x.PaperName).ToArray(), duplex = settings.CanDuplex, color = settings.SupportsColor, trays = settings.PaperSources.Cast<PaperSource>().Select(x => x.SourceName).ToArray() }); } catch { return null; }
    }
}
