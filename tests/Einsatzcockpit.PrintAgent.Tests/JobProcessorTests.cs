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
        store.Update(Job(JobStates.Spooled) with { SpoolRef = "7" }); spooler.State = SpoolJobState.Deleted; await p.MonitorAsync(); Assert.Equal(JobStates.Uncertain, store.Get("j1")!.State);
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

    [Fact]
    public void TerminalStateAndOutboxCommitTogetherOrNeither()
    {
        var store = new JobStore(_path);
        store.Insert(Job(JobStates.Spooled));
        using var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}");
        sql.Open();
        using var command = sql.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_outbox BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT,'simulated crash'); END";
        command.ExecuteNonQuery();
        var done = Job(JobStates.Done);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.UpdateWithOutbox(done, "job_status", "{}", DateTimeOffset.UtcNow));
        Assert.Equal(JobStates.Spooled, store.Get("j1")!.State);
        Assert.Empty(store.PendingOutbox());
        command.CommandText = "DROP TRIGGER fail_outbox";
        command.ExecuteNonQuery();
        Assert.True(store.UpdateWithOutbox(done, "job_status", "{}", DateTimeOffset.UtcNow));
        Assert.Equal(JobStates.Done, new JobStore(_path).Get("j1")!.State);
        Assert.Single(new JobStore(_path).PendingOutbox());
        Assert.False(store.UpdateWithOutbox(done with { State = JobStates.Spooled }, "job_status", "{}", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(JobStates.Done)]
    [InlineData(JobStates.Failed)]
    [InlineData(JobStates.Abandoned)]
    [InlineData(JobStates.Uncertain)]
    public void CleanupRetainsTerminalJobsFor90DaysAndReturnsPdfPaths(string state)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new JobStore(_path);
        foreach (var age in new[] { 1, 10, 90, 91 })
            store.Insert(Job(state) with { JobId = $"j{age}", UpdatedAt = now.AddDays(-age) });
        var cleaned = store.Cleanup(now.AddDays(-7), now);
        Assert.Equal(3, cleaned.Count);
        Assert.All(cleaned, job => Assert.Equal("x.pdf", job.PdfPath));
        Assert.Equal("x.pdf", store.Get("j1")!.PdfPath);
        foreach (var id in new[] { "j10", "j90" })
        {
            var retained = store.Get(id)!;
            Assert.Equal(state, retained.State);
            Assert.Equal("t", retained.LeaseToken);
            Assert.Null(retained.PdfPath);
            Assert.Equal("", retained.ArtifactUrl);
            Assert.Null(retained.OptionsJson);
        }
        Assert.Null(store.Get("j91"));
    }

    [Theory]
    [InlineData(JobStates.Granted)]
    [InlineData(JobStates.Fetching)]
    [InlineData(JobStates.IntentPending)]
    [InlineData(JobStates.Submitting)]
    [InlineData(JobStates.Spooled)]
    public void CleanupNeverDeletesOrClearsNonterminalJobs(string state)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new JobStore(_path);
        var job = Job(state) with { UpdatedAt = now.AddDays(-100) };
        store.Insert(job);
        Assert.Empty(store.Cleanup(now.AddDays(-7), now));
        Assert.Equal(job, store.Get(job.JobId));
    }

    [Fact]
    public void CleanupPreservesUnacknowledgedOutboxForDeletedJobs()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new JobStore(_path);
        store.Insert(Job(JobStates.Done) with { UpdatedAt = now.AddDays(-100) });
        var payload = JsonSerializer.Serialize(new JobStatusMessage("j1", "t", "done", "spooled"), ProtocolJson.Options);
        var pendingId = store.AddOutbox("job_status", payload, now.AddDays(-100));
        var ackedId = store.AddOutbox("job_status", payload, now.AddDays(-100));
        var pump = new Einsatzcockpit.PrintAgent.Core.Cloud.OutboxPump(store, (_, _) => Task.CompletedTask);
        pump.Ack(ackedId);
        store.IncrementOutboxAttempts(pendingId);
        store.Cleanup(now.AddDays(-7), now);
        Assert.Null(store.Get("j1"));
        var pending = Assert.Single(new JobStore(_path).PendingOutbox());
        Assert.Equal(pendingId, pending.Id);
        Assert.Equal(payload, pending.PayloadJson);
        pump.Ack(pendingId);
        Assert.Empty(store.PendingOutbox());
    }

    [Fact]
    public async Task DeletingObservationSurvivesRestartAndGoneBecomesUncertain()
    {
        var store = new JobStore(_path);
        store.Insert(Job(JobStates.Spooled) with { SpoolRef = "7" });
        var cloud = new FakeCloud();
        await Create(cloud, store, spooler: new FakeSpooler { State = SpoolJobState.Deleting }).MonitorAsync();
        Assert.True(store.Get("j1")!.SpoolDeleting);
        Assert.Equal(JobStates.Spooled, store.Get("j1")!.State);
        Assert.Empty(cloud.Statuses);
        // Auch zwischenzeitliche Fehlermeldungen duerfen die Beobachtung nicht zuruecksetzen.
        await Create(cloud, store, spooler: new FakeSpooler { State = SpoolJobState.Error(2, "Fehler") }).MonitorAsync();
        var reopened = new JobStore(_path);
        Assert.True(reopened.Get("j1")!.SpoolDeleting);
        var restartedCloud = new FakeCloud();
        await Create(restartedCloud, reopened, spooler: new FakeSpooler { State = SpoolJobState.Gone }).MonitorAsync();
        Assert.Equal(JobStates.Uncertain, reopened.Get("j1")!.State);
        Assert.Equal("uncertain", Assert.Single(restartedCloud.Statuses).Status);
        Assert.Contains(reopened.PendingOutbox(), item => item.PayloadJson.Contains("uncertain"));
    }

    [Fact]
    public void ExistingSchemaMigratesSpoolDeletingWithFalseDefault()
    {
        var store = new JobStore(_path);
        store.Insert(Job(JobStates.Spooled));
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            sql.Open();
            using var command = sql.CreateCommand();
            command.CommandText = "ALTER TABLE jobs DROP COLUMN spool_deleting";
            command.ExecuteNonQuery();
        }
        var reopened = new JobStore(_path);
        var job = reopened.Get("j1")!;
        Assert.False(job.SpoolDeleting);
        reopened.Update(job with { SpoolDeleting = true });
        Assert.True(new JobStore(_path).Get("j1")!.SpoolDeleting);
    }

    [Fact]
    public async Task RejectionReportsNotSubmittedAndPersistsProtocolFlag()
    {
        var cloud = new FakeCloud();
        await Create(cloud, submitter: new FakeSubmitter { BeforeSubmit = () => throw new PrintRejectedException("Queue fehlt") })
            .HandleGrantAsync(Grant("123"));
        var status = Assert.Single(cloud.Statuses);
        Assert.Equal("failed", status.Status);
        Assert.Equal("submitting", status.Phase);
        Assert.True(status.NotSubmitted);
        var payload = JsonDocument.Parse(Assert.Single(new JobStore(_path).PendingOutbox()).PayloadJson);
        Assert.True(payload.RootElement.GetProperty("not_submitted").GetBoolean());
    }

    [Fact]
    public async Task FetchFailureReportsNotSubmittedButInterruptedSubmissionDoesNot()
    {
        var cloud = new FakeCloud();
        await Create(cloud, fetcher: new FakeFetcher { Error = new IOException("Download") }).HandleGrantAsync(Grant("123"));
        Assert.True(Assert.Single(cloud.Statuses).NotSubmitted);
        cloud.Statuses.Clear();
        await Create(cloud, submitter: new FakeSubmitter { BeforeSubmit = () => throw new IOException("Verbindung unterbrochen") })
            .HandleGrantAsync(Grant("456"));
        var uncertain = Assert.Single(cloud.Statuses);
        Assert.Equal("uncertain", uncertain.Status);
        Assert.False(uncertain.NotSubmitted);
    }

    [Fact]
    public async Task MonitorDeduplicatesErrorsAndReportsRecoveryAndRecurringError()
    {
        var store = new JobStore(_path);
        store.Insert(Job(JobStates.Spooled) with { SpoolRef = "7" });
        var cloud = new FakeCloud();
        var spooler = new FakeSpooler { State = SpoolJobState.Error(3, "Papier leer") };
        var processor = Create(cloud, store, spooler: spooler);
        await processor.MonitorAsync();
        await processor.MonitorAsync();
        Assert.Single(cloud.Statuses);
        Assert.Single(store.PendingOutbox());
        spooler.State = SpoolJobState.Error(4, "Papierstau");
        await processor.MonitorAsync();
        Assert.Equal(2, cloud.Statuses.Count);
        spooler.State = SpoolJobState.Printing;
        await processor.MonitorAsync();
        await processor.MonitorAsync();
        Assert.Null(cloud.Statuses.Last().Error);
        Assert.Null(store.Get("j1")!.Error);
        Assert.Equal(3, cloud.Statuses.Count);
        spooler.State = SpoolJobState.Error(3, "Papier leer");
        await processor.MonitorAsync();
        Assert.Equal(4, cloud.Statuses.Count);
        spooler.State = SpoolJobState.Gone;
        await processor.MonitorAsync();
        await processor.MonitorAsync();
        Assert.Equal("done", cloud.Statuses.Last().Status);
        Assert.Equal(5, store.PendingOutbox().Count);
    }

    [Theory]
    [InlineData(SpoolJobStateKind.Gone)]
    [InlineData(SpoolJobStateKind.Deleted)]
    public async Task RecoveryDoesNotTreatMissingSpoolJobAsPrinted(SpoolJobStateKind kind)
    {
        var store = new JobStore(_path);
        store.Insert(Job(JobStates.Spooled) with { SpoolRef = "7" });
        var spooler = new FakeSpooler { State = new(kind) };
        await new RecoveryService(store, spooler, new FakeClock()).RecoverAsync();
        await Create(new FakeCloud(), store, spooler: spooler).MonitorAsync();
        Assert.Equal(JobStates.Uncertain, store.Get("j1")!.State);
        var status = JsonSerializer.Deserialize<JobStatusMessage>(Assert.Single(store.PendingOutbox()).PayloadJson, ProtocolJson.Options)!;
        Assert.Equal("uncertain", status.Status);
        Assert.Equal("7", status.SpoolRef);
        Assert.False(status.NotSubmitted);
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
