using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class RecoveryFailureTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecpg-recovery-{Guid.NewGuid()}.db");
    public void Dispose() => TestFiles.DeleteDatabase(_path);

    [Fact]
    public async Task OfflineQueueDoesNotBlockOtherJobsAndPersistsUncertainOutbox()
    {
        var store = new JobStore(_path);
        store.Insert(Job("broken", "offline"));
        store.Insert(Job("working", "ready"));
        var open = await new RecoveryService(store, new SelectiveSpooler(), new SystemClock()).RecoverAsync();
        var reopened = new JobStore(_path);
        Assert.Equal(JobStates.Uncertain, reopened.Get("broken")!.State);
        Assert.Equal(JobStates.Spooled, reopened.Get("working")!.State);
        Assert.Equal("win:42", reopened.Get("working")!.SpoolRef);
        Assert.Contains(open, job => job.JobId == "broken" && job.LocalState == JobStates.Uncertain);
        var message = Assert.Single(reopened.PendingOutbox());
        var status = JsonSerializer.Deserialize<JobStatusMessage>(message.PayloadJson, ProtocolJson.Options)!;
        Assert.Equal("uncertain", status.Status);
        Assert.DoesNotContain("SECRET_TOKEN", message.PayloadJson);
        // Ein weiterer Neustart erzeugt weder zweiten Druck noch zweite Statusmeldung.
        await new RecoveryService(reopened, new SelectiveSpooler(), new SystemClock()).RecoverAsync();
        Assert.Single(reopened.PendingOutbox());
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoUncertainOrSwallowed()
    {
        var store = new JobStore(_path);
        store.Insert(Job("cancel", "ready"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecoveryService(store, new SelectiveSpooler(), new SystemClock()).RecoverAsync(cancellation.Token));
        Assert.Equal(JobStates.Submitting, store.Get("cancel")!.State);
        Assert.Empty(store.PendingOutbox());
    }

    [Fact]
    public async Task InvalidRouteIsIsolatedAndDurablyReported()
    {
        var store = new JobStore(_path);
        store.Insert(Job("bad-json", "ready") with { RouteJson = "not-json" });
        await new RecoveryService(store, new SelectiveSpooler(), new SystemClock()).RecoverAsync();
        Assert.Equal(JobStates.Uncertain, store.Get("bad-json")!.State);
        Assert.Single(store.PendingOutbox());
    }

    private static StoredJob Job(string id, string queue) => new(id, "lease", 1, JobStates.Submitting,
        "alarm", "url", "pdf", null,
        JsonSerializer.Serialize(new RouteConfig(1, 1, queue, "driver"), ProtocolJson.Options),
        null, null, id, 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class SelectiveSpooler : IPrintSpooler
    {
        public Task<SpoolJobInfo?> FindJobByDocumentNameAsync(string queue, string name, CancellationToken token) =>
            queue == "offline" ? throw new InvalidOperationException("SECRET_TOKEN") : Task.FromResult<SpoolJobInfo?>(new("win:42"));
        public Task<SpoolJobState> GetJobStateAsync(string queue, string reference, CancellationToken token) => Task.FromResult(SpoolJobState.InQueue);
        public Task CancelJobAsync(string queue, string reference, CancellationToken token) => Task.CompletedTask;
    }
}
