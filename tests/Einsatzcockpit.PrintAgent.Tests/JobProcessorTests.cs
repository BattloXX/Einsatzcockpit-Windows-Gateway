using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class JobProcessorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecpg-{Guid.NewGuid()}.db");
    public void Dispose() => TestFiles.DeleteDatabase(_path);

    [Fact]
    public async Task Submit_wird_nur_nach_erfolgreichem_intent_aufgerufen()
    {
        var cloud = new FakeCloud { Intent = new("j1", false, "canceled") }; var submitter = new FakeSubmitter();
        var processor = Create(cloud, submitter: submitter);
        await processor.HandleGrantAsync(Grant("j1"));
        Assert.Equal(0, submitter.Calls); Assert.Equal(JobStates.Abandoned, new JobStore(_path).Get("j1")!.State);
    }

    [Fact]
    public async Task Submitting_ist_vor_spooleraufruf_persistiert()
    {
        var submitter = new FakeSubmitter { BeforeSubmit = () => Assert.Equal(JobStates.Submitting, new JobStore(_path).Get("j1")!.State) };
        await Create(new FakeCloud(), submitter: submitter).HandleGrantAsync(Grant("j1"));
        Assert.Equal(JobStates.Spooled, new JobStore(_path).Get("j1")!.State);
    }

    [Theory]
    [InlineData(JobStates.Granted, JobStates.Abandoned)]
    [InlineData(JobStates.Fetching, JobStates.Abandoned)]
    [InlineData(JobStates.IntentPending, JobStates.Abandoned)]
    [InlineData(JobStates.Spooled, JobStates.Spooled)]
    public async Task Recovery_behandelt_vordruck_phasen_und_spooled(string initial, string expected)
    {
        var store = new JobStore(_path); store.Insert(Job(initial));
        var recovery = new RecoveryService(store, new FakeSpooler(), new FakeClock());
        await recovery.RecoverAsync();
        Assert.Equal(expected, store.Get("j1")!.State);
    }

    [Fact]
    public async Task Recovery_submitting_fund_und_unklar_werden_nicht_neu_gedruckt()
    {
        var store = new JobStore(_path); store.Insert(Job(JobStates.Submitting));
        await new RecoveryService(store, new FakeSpooler { Found = new("42") }, new FakeClock()).RecoverAsync();
        Assert.Equal(JobStates.Spooled, store.Get("j1")!.State);
        store.Update(Job(JobStates.Submitting));
        await new RecoveryService(store, new FakeSpooler(), new FakeClock()).RecoverAsync();
        Assert.Equal(JobStates.Uncertain, store.Get("j1")!.State);
    }

    [Fact]
    public async Task Doppelgrant_ist_idempotent_und_neuer_grant_nach_submitting_wird_ignoriert()
    {
        var submitter = new FakeSubmitter(); var processor = Create(new FakeCloud(), submitter: submitter);
        await processor.HandleGrantAsync(Grant("j1")); await processor.HandleGrantAsync(Grant("j1"));
        await processor.HandleGrantAsync(Grant("j1", token: "neu", generation: 2));
        Assert.Equal(1, submitter.Calls); Assert.Equal("t", new JobStore(_path).Get("j1")!.LeaseToken);
    }

    [Fact]
    public async Task Cancel_und_spooler_monitoring_setzen_erwartete_zustaende()
    {
        var store = new JobStore(_path); store.Insert(Job(JobStates.Granted));
        var p = Create(new FakeCloud(), store: store); await p.CancelAsync("j1"); Assert.Equal(JobStates.Abandoned, store.Get("j1")!.State);
        store.Update(Job(JobStates.Spooled) with { SpoolRef = "7" });
        var spooler = new FakeSpooler { State = SpoolJobState.Error(3, "Papier leer") }; var cloud = new FakeCloud(); p = Create(cloud, store, spooler: spooler);
        await p.MonitorAsync(); Assert.Contains(cloud.Statuses, x => x.Status == "printing" && x.Error == "Papier leer");
        Assert.Equal(JobStates.Spooled, store.Get("j1")!.State);
        spooler.State = SpoolJobState.Gone; await p.MonitorAsync(); Assert.Equal(JobStates.Done, store.Get("j1")!.State);
        store.Update(Job(JobStates.Spooled) with { SpoolRef = "7" }); spooler.State = SpoolJobState.Deleted; await p.MonitorAsync(); Assert.Equal(JobStates.Failed, store.Get("j1")!.State);
    }

    [Fact]
    public async Task Fetchfehler_und_outbox_ueberleben_neustart()
    {
        var p = Create(new FakeCloud(), fetcher: new FakeFetcher { Error = new InvalidOperationException("kaputt") });
        await p.HandleGrantAsync(Grant("j1"));
        var reopened = new JobStore(_path); Assert.Equal(JobStates.Failed, reopened.Get("j1")!.State);
        var pending = reopened.PendingOutbox(); Assert.Contains(pending, x => x.PayloadJson.Contains("fetching"));
        reopened.RemoveOutbox(pending[0].Id); Assert.Equal(pending.Count - 1, reopened.PendingOutbox().Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletingOldDownloadCannotReviveAbandonedJobOrOverwriteNewLease(bool regrant, bool failOldFetch)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new FakeCloud();
        var submitter = new FakeSubmitter();
        var fetcher = new FakeFetcher { Fetch = async grant =>
        {
            if (grant.LeaseGeneration == 1)
            {
                await release.Task;
                if (failOldFetch) throw new IOException("alter Download fehlgeschlagen");
            }
            return "x.pdf";
        }};
        var processor = Create(cloud, fetcher: fetcher, submitter: submitter);
        var old = processor.HandleGrantAsync(Grant("123"));
        processor.Abandon("123", "invalid_lease");
        var next = regrant ? processor.HandleGrantAsync(Grant("123", "new", 2)) : Task.CompletedTask;
        try
        {
            var stored = new JobStore(_path).Get("123")!;
            Assert.Equal(regrant ? "new" : "t", stored.LeaseToken);
            Assert.Equal(regrant ? JobStates.Granted : JobStates.Abandoned, stored.State);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(old, next);
        }
        Assert.Equal(regrant ? 1 : 0, submitter.Calls);
        Assert.Equal(regrant ? JobStates.Spooled : JobStates.Abandoned, new JobStore(_path).Get("123")!.State);
        Assert.DoesNotContain(cloud.Statuses, status => status.LeaseToken == "t");
    }

    private JobProcessor Create(FakeCloud cloud, JobStore? store = null, FakeFetcher? fetcher = null, FakeSubmitter? submitter = null, FakeSpooler? spooler = null) =>
        new(store ?? new JobStore(_path), cloud, fetcher ?? new FakeFetcher(), submitter ?? new FakeSubmitter(), spooler ?? new FakeSpooler(), new FakeClock());
    private static LeaseGrant Grant(string id, string token = "t", int generation = 1) => new(id, token, generation, 60, "alarm", "https://example.test/a.pdf", "pdf", new(), new(1, 2, "Q", "driver"));
    private static StoredJob Job(string state) => new("j1", "t", 1, state, "alarm", "url", "pdf", "{}", JsonSerializer.Serialize(new RouteConfig(1, 2, "Q", "driver"), ProtocolJson.Options), "x.pdf", null, "ECPG-j1-g1", 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class FakeCloud : ICloudChannel
    {
        public IntentResult Intent { get; set; } = new("j1", true); public List<JobStatusMessage> Statuses { get; } = [];
        public Task<LeaseGrant[]> RequestLeasesAsync(int max) => Task.FromResult(Array.Empty<LeaseGrant>());
        public Task<IntentResult> SendIntentAsync(string jobId, string token) => Task.FromResult(Intent with { JobId = jobId });
        public Task EnqueueStatusAsync(JobStatusMessage status) { Statuses.Add(status); return Task.CompletedTask; }
        public Task RenewAsync(IReadOnlyList<LeaseReference> leases) => Task.CompletedTask;
    }
    private sealed class FakeFetcher : IArtifactFetcher
    {
        public Exception? Error { get; init; }
        public Func<LeaseGrant, Task<string>>? Fetch { get; init; }
        public Task<string> FetchAsync(LeaseGrant g, CancellationToken c) =>
            Fetch is not null ? Fetch(g) : Error is null ? Task.FromResult("x.pdf") : Task.FromException<string>(Error);
    }
    private sealed class FakeSubmitter : IPrintSubmitter { public string Name => "driver"; public int Calls { get; private set; } public Action? BeforeSubmit { get; init; } public Task<SubmitResult> SubmitAsync(PrintRequest r, CancellationToken c) { Calls++; BeforeSubmit?.Invoke(); return Task.FromResult(new SubmitResult("7")); } }
    private sealed class FakeSpooler : IPrintSpooler { public SpoolJobInfo? Found { get; init; } public SpoolJobState State { get; set; } = SpoolJobState.InQueue; public Task<SpoolJobInfo?> FindJobByDocumentNameAsync(string q, string n, CancellationToken c) => Task.FromResult(Found); public Task<SpoolJobState> GetJobStateAsync(string q, string s, CancellationToken c) => Task.FromResult(State); public Task CancelJobAsync(string q, string s, CancellationToken c) => Task.CompletedTask; }
}
