using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Einsatzcockpit.PrintAgent.Core.Configuration;
using Einsatzcockpit.PrintAgent.Security;

namespace Einsatzcockpit.PrintAgent.Pairing;

public sealed record PairingResult(
    [property: JsonPropertyName("device_token")] string DeviceToken,
    [property: JsonPropertyName("gateway_id")] long GatewayId,
    [property: JsonPropertyName("name")] string Name);

public sealed class PairingClient
{
    private readonly ITokenStore _tokens;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public PairingClient(ITokenStore tokens, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _tokens = tokens;
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task<PairingResult> PairAsync(string cloudUrl, string code, string? proxy,
        CancellationToken cancellationToken)
    {
        var cloud = new Uri(cloudUrl, UriKind.Absolute);
        if (cloud.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(cloud.UserInfo)
            || !string.IsNullOrEmpty(cloud.Query) || !string.IsNullOrEmpty(cloud.Fragment))
        {
            throw new InvalidOperationException("Cloud-URL muss eine HTTPS-Adresse ohne Zugangsdaten, Query oder Fragment sein.");
        }
        code = code.Trim().ToUpperInvariant();
        if (code.Length != 8 || code.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new InvalidOperationException("Pairing-Code muss aus 8 Buchstaben oder Ziffern bestehen.");
        }
        using var handler = _handlerFactory();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (handler is HttpClientHandler httpHandler)
            {
                httpHandler.Proxy = new WebProxy(proxy);
                httpHandler.UseProxy = true;
            }
        }

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var endpoint = new Uri(cloud, "/api/v1/gateway/pair");
        try
        {
            using var response = await client.PostAsJsonAsync(endpoint,
                new { code }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Pairing-Code ist ungültig, abgelaufen oder bereits verwendet. Bitte in Einsatzcockpit einen neuen Code erzeugen.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Pairing wurde abgelehnt (HTTP {(int)response.StatusCode}).");
            }

            var result = await response.Content.ReadFromJsonAsync<PairingResult>(cancellationToken: cancellationToken);
            if (result is null || string.IsNullOrWhiteSpace(result.DeviceToken) || result.GatewayId <= 0)
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
        catch (JsonException)
        {
            throw new InvalidOperationException("Die Cloud hat eine ungültige Pairing-Antwort geliefert.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Pairing zur Cloud hat das Zeitlimit überschritten.", ex);
        }
    }

    public static Uri WebSocketUri(string cloudUrl) => AgentConfiguration.WebSocketUri(cloudUrl);
}
