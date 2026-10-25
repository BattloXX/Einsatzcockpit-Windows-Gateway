using System.Net;
using System.Net.Sockets;
using System.Text;
using Einsatzcockpit.PrintAgent.Rendering;
using Microsoft.Extensions.Logging.Abstractions;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class HtmlRendererTests
{
    // Opt-in: vorhandenen Edge/Chromium verwenden, niemals einen Browser herunterladen.
    public sealed class BrowserTheoryAttribute : TheoryAttribute
    {
        public BrowserTheoryAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("ECPA_TEST_BROWSER")))
                Skip = "ECPA_TEST_BROWSER muss auf einen vorhandenen Edge/Chromium zeigen.";
        }
    }

    [BrowserTheory]
    [InlineData(200, "text/html; charset=utf-8", true)]
    [InlineData(403, "text/html", false)]
    [InlineData(422, "application/json", false)]
    [InlineData(200, "application/json", false)]
    public async Task OnlySuccessfulHtmlArtifactCanBecomePdf(int status, string contentType, bool printable)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var body = Encoding.UTF8.GetBytes("<html><body>Testkarte<script>window.__ecpgReady=true</script></body></html>");
        var serving = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var socket = await server.AcceptTcpClientAsync(lifetime.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                // Chromium kann spekulative TCP-Verbindungen ohne HTTP-Request schließen.
                if (await reader.ReadLineAsync(lifetime.Token) is null) continue;
                while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 }) { }
                var headers = $"HTTP/1.1 {status} Test\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), lifetime.Token);
                await stream.WriteAsync(body, lifetime.Token);
            }
        }, lifetime.Token);
        var target = Path.Combine(Path.GetTempPath(), $"ecpa-html-{Guid.NewGuid()}.pdf");
        var renderer = new EdgeHtmlRenderer(Environment.GetEnvironmentVariable("ECPA_TEST_BROWSER"), 2,
            NullLogger<EdgeHtmlRenderer>.Instance);
        try
        {
            var render = renderer.RenderAsync($"http://127.0.0.1:{port}/render?sig=test", target, lifetime.Token);
            if (printable)
            {
                await render;
                var pdf = await File.ReadAllBytesAsync(target, lifetime.Token);
                Assert.True(pdf.Length > 1000);
                Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => render);
                Assert.False(File.Exists(target));
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await serving; } catch (OperationCanceledException) { }
            File.Delete(target);
        }
    }
}
