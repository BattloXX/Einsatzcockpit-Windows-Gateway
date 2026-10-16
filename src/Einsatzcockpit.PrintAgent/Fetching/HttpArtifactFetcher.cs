using System.Net;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Configuration;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Rendering;

namespace Einsatzcockpit.PrintAgent.Fetching;

public sealed class HttpArtifactFetcher : IArtifactFetcher
{
    private readonly string _spoolDirectory;
    private readonly HttpClient _client;
    private readonly EdgeHtmlRenderer _htmlRenderer;

    public HttpArtifactFetcher(string spoolDirectory, string? proxy, EdgeHtmlRenderer htmlRenderer)
    {
        _spoolDirectory = spoolDirectory;
        _htmlRenderer = htmlRenderer;
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public async Task<string> FetchAsync(LeaseGrant grant, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_spoolDirectory);
        var target = Path.Combine(_spoolDirectory,
            AgentConfiguration.SpoolFileName(grant.JobId, grant.LeaseGeneration));
        var temporary = target + ".tmp";
        try
        {
            if (string.Equals(grant.RenderKind, "html", StringComparison.OrdinalIgnoreCase))
            {
                await _htmlRenderer.RenderAsync(grant.ArtifactUrl, temporary, cancellationToken);
            }
            else if (string.Equals(grant.RenderKind, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var response = await _client.GetAsync(grant.ArtifactUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Artefakt ist kein PDF (Content-Type).");
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(temporary);
                await input.CopyToAsync(output, cancellationToken);
            }
            else
            {
                throw new InvalidDataException("Unbekannte Render-Art: " + grant.RenderKind);
            }

            var header = new byte[4];
            await using (var file = File.OpenRead(temporary))
            {
                var read = await file.ReadAsync(header, cancellationToken);
                if (read != header.Length || !AgentConfiguration.HasPdfSignature(header))
                {
                    throw new InvalidDataException("Artefakt besitzt keine gültige PDF-Signatur.");
                }
            }

            File.Move(temporary, target, true);
            return target;
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }
}
