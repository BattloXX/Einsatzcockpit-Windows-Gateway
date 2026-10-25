using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Fetching;
using Einsatzcockpit.PrintAgent.Rendering;
using Microsoft.Extensions.Logging.Abstractions;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class ArtifactFetcherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ecpa-artifact-{Guid.NewGuid()}");
    private static EdgeHtmlRenderer Renderer() => new(null, 9, NullLogger<EdgeHtmlRenderer>.Instance);
    private static byte[] Pdf() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mixed-pages.pdf"));
    private static LeaseGrant Grant() => JsonSerializer.Deserialize<Envelope>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "cloud-2026-10-25-lease-grant.json")), ProtocolJson.Options)!
        .Payload.Deserialize<LeaseGrantMessage>(ProtocolJson.Options)!.Jobs.Single();

    [Theory]
    [InlineData("https://cloud.example/api/v1/print/artifacts/123?sig=test")]
    [InlineData("/api/v1/print/artifacts/123?sig=test")]
    public async Task SignedPdfDownloadPreservesAllBytesOfMixedPageDocument(string url)
    {
        var bytes = Pdf();
        var handler = new ReplyHandler(() => Reply(bytes, "application/pdf; charset=binary"));
        using var client = new HttpClient(handler);
        var fetcher = new HttpArtifactFetcher(_directory, client, Renderer(), new Uri("https://cloud.example"));
        var path = await fetcher.FetchAsync(Grant() with { ArtifactUrl = url }, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("123-g2.pdf", Path.GetFileName(path));
        Assert.Equal("https://cloud.example/api/v1/print/artifacts/123?sig=test", handler.Url);
        Assert.False(handler.HasAuthorization); // Authentifizierung ausschließlich über sig.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(403, "application/json", "%PDF-error")]
    [InlineData(200, "text/html", "<html>Login</html>")]
    [InlineData(200, "application/pdf", "truncated")]
    public async Task HttpErrorsWrongMimeAndMissingPdfSignatureNeverBecomePrintArtifacts(int status, string mime, string body)
    {
        using var client = new HttpClient(new ReplyHandler(() => Reply(Encoding.ASCII.GetBytes(body), mime, status)));
        var fetcher = new HttpArtifactFetcher(_directory, client, Renderer());
        await Assert.ThrowsAnyAsync<Exception>(() => fetcher.FetchAsync(Grant(), default));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task IncompleteBodyIsRejectedAndTemporaryFileRemoved()
    {
        using var client = new HttpClient(new ReplyHandler(() =>
        {
            var response = Reply(Pdf(), "application/pdf");
            response.Content.Headers.ContentLength = Pdf().Length + 10;
            return response;
        }));
        var fetcher = new HttpArtifactFetcher(_directory, client, Renderer());
        await Assert.ThrowsAsync<InvalidDataException>(() => fetcher.FetchAsync(Grant(), default));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationAfterHeadersInterruptsBodyAndRemovesTemporaryFile()
    {
        var content = new BlockingContent();
        using var client = new HttpClient(new ReplyHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Content.Headers.ContentType = new("application/pdf");
            return response;
        }));
        var fetcher = new HttpArtifactFetcher(_directory, client, Renderer());
        using var cancellation = new CancellationTokenSource();
        var download = fetcher.FetchAsync(Grant(), cancellation.Token);
        await content.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ProductionHttpClientFollowsRedirectToSignedPdfWithoutDeviceToken()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var requests = new List<string>();
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var socket = await server.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                requests.Add((await reader.ReadLineAsync(cancellation.Token))!);
                while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 } header)
                    Assert.False(header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                var bytes = i == 0 ? [] : Pdf();
                var headers = i == 0
                    ? "HTTP/1.1 302 Found\r\nLocation: /pdf?sig=redirect-test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Type: application/pdf\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellation.Token);
                await stream.WriteAsync(bytes, cancellation.Token);
            }
        }, cancellation.Token);
        var fetcher = new HttpArtifactFetcher(_directory, (string?)null, Renderer());
        var path = await fetcher.FetchAsync(Grant() with { ArtifactUrl = $"http://127.0.0.1:{port}/start?sig=test" }, cancellation.Token);
        await serving;
        Assert.Equal(Pdf(), await File.ReadAllBytesAsync(path, cancellation.Token));
        Assert.Equal(["GET /start?sig=test HTTP/1.1", "GET /pdf?sig=redirect-test HTTP/1.1"], requests);
    }

    private static HttpResponseMessage Reply(byte[] bytes, string mime, int status = 200)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mime);
        return response;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class ReplyHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public bool HasAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            HasAuthorization = request.Headers.Authorization is not null;
            return Task.FromResult(reply());
        }
    }

    private sealed class BlockingContent : HttpContent
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockingStream(Reading));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class BlockingStream(TaskCompletionSource reading) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
