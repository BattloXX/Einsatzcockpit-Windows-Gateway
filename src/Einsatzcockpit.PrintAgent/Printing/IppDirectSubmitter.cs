using System.Net.Http.Headers;
using System.Text;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Printing;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class IppDirectSubmitter(HttpClient? client = null) : IPrintSubmitter
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private static int _requestId;
    public string Name => "ipp_direct";
    public async Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var uri = TargetUri(request.Route); var options = request.Options; var attributes = Basic(uri, request.DocumentName).Concat(new[]
        {
            new IppAttribute(0x21, "copies", options?.Copies ?? 1), new IppAttribute(0x44, "sides", PrintValueMappings.Duplex(options?.Duplex) switch { "long-edge" => "two-sided-long-edge", "short-edge" => "two-sided-short-edge", _ => "one-sided" }),
            new IppAttribute(0x44, "media", PrintValueMappings.Media(options?.Media) == "A3" ? "iso_a3_297x420mm" : "iso_a4_210x297mm"), new IppAttribute(0x44, "print-color-mode", PrintValueMappings.IsColor(options?.Color) ? "color" : "monochrome")
        });
        var bytes = IppMessage.Encode(IppMessage.PrintJob, NextId(), attributes, await File.ReadAllBytesAsync(request.PdfPath, cancellationToken));
        var response = await Send(uri, bytes, cancellationToken); if (response.StatusCode >= 0x0400) throw new PrintRejectedException($"IPP-Drucker lehnt Auftrag ab (0x{response.StatusCode:X4}).");
        if (!response.Attributes.TryGetValue("job-id", out var job) || job is not int id) throw new PrintRejectedException("IPP-Drucker lieferte keine Job-ID.");
        return new SubmitResult($"ipp:{uri}|{id}");
    }
    public async Task<SpoolJobState> GetJobStateAsync(string spoolRef, CancellationToken cancellationToken)
    {
        var separator = spoolRef.LastIndexOf('|'); if (separator <= 4 || !int.TryParse(spoolRef[(separator + 1)..], out var job)) return SpoolJobState.Gone;
        var uri = new Uri(spoolRef[4..separator]);
        try
        {
            var bytes = IppMessage.Encode(IppMessage.GetJobAttributes, NextId(), Basic(uri, "").Concat(new[] { new IppAttribute(0x21, "job-id", job), new IppAttribute(0x44, "requested-attributes", "job-state") }));
            var response = await Send(uri, bytes, cancellationToken); if (!response.Attributes.TryGetValue("job-state", out var state) || state is not int number) return SpoolJobState.InQueue;
            return number switch { 9 => SpoolJobState.Gone, 7 or 8 => SpoolJobState.Deleted, 6 => SpoolJobState.Error(number, "IPP-Druckauftrag angehalten"), 5 => SpoolJobState.Printing, _ => SpoolJobState.InQueue };
        }
        catch (HttpRequestException) { return SpoolJobState.InQueue; }
    }
    public async Task<string?> GetPrinterUuidAsync(Uri uri, CancellationToken cancellationToken)
    {
        var request = IppMessage.Encode(IppMessage.GetPrinterAttributes, NextId(), Basic(uri, "").Concat(new[] { new IppAttribute(0x44, "requested-attributes", "printer-uuid") }));
        var response = await Send(uri, request, cancellationToken); return response.Attributes.TryGetValue("printer-uuid", out var uuid) ? uuid as string : null;
    }
    private async Task<IppResponse> Send(Uri uri, byte[] bytes, CancellationToken cancellationToken) { using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(bytes) }; message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp"); using var response = await _client.SendAsync(message, cancellationToken); return IppMessage.Decode(await response.Content.ReadAsByteArrayAsync(cancellationToken)); }
    private static IEnumerable<IppAttribute> Basic(Uri uri, string name) => new[] { new IppAttribute(0x47, "attributes-charset", "utf-8"), new IppAttribute(0x48, "attributes-natural-language", "de"), new IppAttribute(0x45, "printer-uri", uri.ToString()), new IppAttribute(0x42, "requesting-user-name", "Einsatzcockpit"), new IppAttribute(0x42, "job-name", name) };
    private static Uri TargetUri(RouteConfig route) { var value = CommandSubmitter.Option(route, "target_uri").GetString() ?? route.LocalQueue; return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : throw new PrintRejectedException("Ungültige IPP-Ziel-URI."); }
    private static int NextId() => Interlocked.Increment(ref _requestId);
}
