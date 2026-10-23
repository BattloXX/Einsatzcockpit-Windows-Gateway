using System.Net;
using System.Text;
using Einsatzcockpit.PrintAgent.Pairing;
using Einsatzcockpit.PrintAgent.Security;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class PairingTests
{
    [Fact]
    public async Task CloudContractUsesSnakeCaseAndNumericGatewayId()
    {
        var tokens = new MemoryTokens();
        var handler = new ReplyHandler(HttpStatusCode.OK,
            """{"device_token":"test-device-token","gateway_id":42,"name":"PC A"}""");
        var client = new PairingClient(tokens, () => handler);
        var result = await client.PairAsync("https://cloud.example", " abcd2345 ", null, default);
        Assert.Equal(42, result.GatewayId);
        Assert.Equal("PC A", result.Name);
        Assert.Equal("test-device-token", tokens.Value);
        Assert.Equal("https://cloud.example/api/v1/gateway/pair", handler.Url);
        Assert.Equal("""{"code":"ABCD2345"}""", handler.Body);
    }

    [Theory]
    [InlineData(401, "{}", "bereits verwendet")]
    [InlineData(500, "{}", "HTTP 500")]
    [InlineData(302, "{}", "HTTP 302")]
    [InlineData(200, "{}", "kein Device-Token")]
    [InlineData(200, "{\"device_token\":\"sensitive\",\"gateway_id\":\"bad\"}", "ungültige Pairing-Antwort")]
    public async Task FailedPairingPreservesExistingTokenAndNeverEchoesResponse(int status, string body, string expected)
    {
        var tokens = new MemoryTokens { Value = "existing" };
        var client = new PairingClient(tokens, () => new ReplyHandler((HttpStatusCode)status, body));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PairAsync("https://cloud.example", "ABCD2345", null, default));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
        Assert.DoesNotContain("ABCD2345", error.Message);
        Assert.Equal("existing", tokens.Value);
    }

    [Theory]
    [InlineData("https://cloud.example", "")]
    [InlineData("https://cloud.example", "--PairingCode")]
    [InlineData("https://cloud.example", "SHORT")]
    [InlineData("http://cloud.example", "ABCD2345")]
    [InlineData("https://user:pass@cloud.example", "ABCD2345")]
    [InlineData("https://cloud.example?token=secret", "ABCD2345")]
    public async Task InvalidInputDoesNotSendRequest(string url, string code)
    {
        var client = new PairingClient(new MemoryTokens(), () => throw new Exception("Must not send"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PairAsync(url, code, null, default));
    }

    [Fact]
    public async Task UnreachableCloudHasActionableError()
    {
        var client = new PairingClient(new MemoryTokens(), () => new UnreachableHandler());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PairAsync("https://cloud.example", "ABCD2345", null, default));
        Assert.Equal("Cloud beim Pairing nicht erreichbar.", error.Message);
    }

    private sealed class MemoryTokens : ITokenStore
    {
        public string? Value { get; set; }
        public string? Read() => Value;
        public void Save(string token) => Value = token;
        public void Delete() => Value = null;
    }

    private sealed class ReplyHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Url { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Network failure");
    }
}
