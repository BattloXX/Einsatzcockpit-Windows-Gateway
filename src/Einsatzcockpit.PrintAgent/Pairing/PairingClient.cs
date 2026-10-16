using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Configuration;
using Einsatzcockpit.PrintAgent.Core.Configuration;
using Einsatzcockpit.PrintAgent.Security;

namespace Einsatzcockpit.PrintAgent.Pairing;

public sealed record PairingResult(string DeviceToken, string GatewayId, string Name);

public sealed class PairingClient
{
    private readonly ITokenStore _tokens;

    public PairingClient(ITokenStore tokens)
    {
        _tokens = tokens;
    }

    public async Task<PairingResult> PairAsync(string cloudUrl, string code, string? proxy,
        CancellationToken cancellationToken)
    {
        var cloud = new Uri(cloudUrl, UriKind.Absolute);
        using var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var endpoint = new Uri(cloud, "/api/v1/gateway/pair");
        try
        {
            using var response = await client.PostAsJsonAsync(endpoint,
                new { code = code.Trim().ToUpperInvariant() }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Pairing-Code ist ungültig oder abgelaufen.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Pairing wurde abgelehnt (HTTP {(int)response.StatusCode}).");
            }

            var result = await response.Content.ReadFromJsonAsync<PairingResult>(cancellationToken: cancellationToken);
            if (result is null || string.IsNullOrWhiteSpace(result.DeviceToken))
            {
                throw new InvalidOperationException("Pairing-Antwort enthält kein Device-Token.");
            }

            _tokens.Save(result.DeviceToken);
            return result;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Cloud beim Pairing nicht erreichbar.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Pairing zur Cloud hat das Zeitlimit überschritten.", ex);
        }
    }

    public static Uri WebSocketUri(string cloudUrl) => AgentConfiguration.WebSocketUri(cloudUrl);
}
