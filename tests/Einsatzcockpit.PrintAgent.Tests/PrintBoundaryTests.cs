using System.Net;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Printing;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Printing;

namespace Einsatzcockpit.PrintAgent.Tests;

public class PrintBoundaryTests
{
    [Fact]
    public async Task NonzeroCommandExitCannotProveThatNothingWasPrinted()
    {
        var program = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh";
        var args = OperatingSystem.IsWindows() ? new[] { program, "/c", "exit /b 1" } : new[] { program, "-c", "exit 1" };
        var options = JsonSerializer.SerializeToElement(new { command = args });
        var route = new RouteConfig(1, 1, "Test", "command", options);
        var request = new PrintRequest("unused.pdf", route, new(), "phase4");
        await Assert.ThrowsAsync<IOException>(() => new CommandSubmitter([program]).SubmitAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x0500)]
    public async Task MissingIppJobIdOrErrorAfterPostIsAmbiguous(int status)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "%PDF-test");
            using var client = new HttpClient(new ResponseHandler(IppMessage.Encode((ushort)status, 1, [])));
            var submitter = new IppDirectSubmitter(client);
            var request = new PrintRequest(path, new RouteConfig(1, 1, "http://printer.test/ipp", "ipp_direct"), new(), "phase4");
            await Assert.ThrowsAsync<IOException>(() => submitter.SubmitAsync(request, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CompletedCommandAndFolderModesRemainUsable()
    {
        var program = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh";
        var args = OperatingSystem.IsWindows() ? new[] { program, "/c", "exit /b 0" } : new[] { program, "-c", "exit 0" };
        var route = new RouteConfig(1, 1, "Test", "command", JsonSerializer.SerializeToElement(new { command = args }));
        var submitted = await new CommandSubmitter([program]).SubmitAsync(new("unused", route, new(), "test"), CancellationToken.None);
        var spooler = new CompositeSpooler(new WindowsSpooler(), new IppDirectSubmitter());
        Assert.Equal(SpoolJobStateKind.Gone, (await spooler.GetJobStateAsync("Test", submitted.SpoolRef, CancellationToken.None)).Kind);
        var directory = Path.Combine(Path.GetTempPath(), "phase4-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var pdf = Path.Combine(directory, "source.pdf");
        try
        {
            await File.WriteAllTextAsync(pdf, "%PDF-test");
            var folder = new RouteConfig(1, 1, "Test", "folder", JsonSerializer.SerializeToElement(new { target_dir = directory }));
            var copied = await new FolderSubmitter().SubmitAsync(new(pdf, folder, new(), "test"), CancellationToken.None);
            Assert.True(File.Exists(copied.SpoolRef[5..]));
            Assert.Equal(SpoolJobStateKind.Gone, (await spooler.GetJobStateAsync("Test", copied.SpoolRef, CancellationToken.None)).Kind);
            Assert.Equal(SpoolJobStateKind.Error, (await spooler.GetJobStateAsync("Test", "unknown:ref", CancellationToken.None)).Kind);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private class ResponseHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
