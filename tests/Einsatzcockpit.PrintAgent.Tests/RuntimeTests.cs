using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Cloud;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Core.Runtime;
using Einsatzcockpit.PrintAgent.Core.Update;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecpg-runtime-{Guid.NewGuid()}.db");
    private readonly DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hello_nach_start_enthaelt_nur_aktuelle_offene_jobs_und_uncertain_ist_in_outbox()
    {
        var store = new JobStore(_path);
        store.Insert(Job("submitting"));
        var cloud = new TestCloud();
        await using var runtime = Runtime(store, cloud);

        await runtime.StartAsync();

        var hello = cloud.Messages.Single(message => message.Type == "hello");
        var payload = (HelloMessage)hello.Payload;
        Assert.Empty(payload.OpenJobs);
        Assert.Equal(JobStates.Uncertain, store.Get("j1")!.State);
        Assert.Contains(store.PendingOutbox(), item => item.PayloadJson.Contains("uncertain"));
    }

    [Theory]
    [InlineData("driver")]
    [InlineData("pdf_direct")]
    public async Task CloudDocumentPackageGrantReachesSubmitterAfterNumericIntent(string renderMode)
    {
        var transport = new QueueTransport();
        var store = new JobStore(_path);
        var connection = new CloudConnection(transport, new Uri("wss://example.test/ws"), "token", store, new FixedClock(_now));
        var submitter = new GoodSubmitter();
        await using var runtime = Runtime(store, connection, submitter: submitter);
        await runtime.StartAsync();
        await WaitUntilAsync(() => transport.Sent.Any(IsHello));

        await transport.EnqueueAsync(JsonSerializer.Deserialize<Envelope>("""{"type":"job_available","payload":{"job_ids":[123]}}""", ProtocolJson.Options)!);
        await WaitUntilAsync(() => transport.Sent.Any(message => TypeOf(message) == "lease_request"));
        var request = transport.Sent.Last(message => TypeOf(message) == "lease_request");
        var rid = JsonSerializer.Deserialize<Envelope>(request, ProtocolJson.Options)!.Rid;
        // Direkt aus Cloud _grant (Stand 2026-10-25); nur Test-Token/URL ersetzt.
        var fixture = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cloud-2026-10-25-lease-grant.json")), ProtocolJson.Options)!;
        var original = fixture.Payload.Deserialize<LeaseGrantMessage>(ProtocolJson.Options)!.Jobs.Single();
        var grant = original with { Route = original.Route with { RenderMode = renderMode } };
        Assert.Equal("123", grant.JobId);
        await transport.EnqueueAsync(new Envelope("lease_grant", Rid: rid,
            Payload: ProtocolJson.Element(new LeaseGrantMessage([grant]))));
        await WaitUntilAsync(() => transport.Sent.Any(message => TypeOf(message) == "print_intent"));
        var intent = transport.Sent.Last(message => TypeOf(message) == "print_intent");
        var intentEnvelope = JsonSerializer.Deserialize<Envelope>(intent, ProtocolJson.Options)!;
        Assert.Equal(123, intentEnvelope.Payload.GetProperty("job_id").GetInt64());
        var intentRid = intentEnvelope.Rid;
        await transport.EnqueueAsync(new Envelope("intent_result", Rid: intentRid,
            Payload: JsonSerializer.Deserialize<JsonElement>("""{"job_id":123,"ok":true,"reason":null}""")));

        await WaitUntilAsync(() => store.Get("123")?.State == JobStates.Spooled);
        Assert.Equal(new PrintOptions(2, "long", "monochrome", "A3"), submitter.LastRequest!.Options);
        var options = submitter.LastRequest.Route.Options!.Value;
        Assert.Equal("auto", options.GetProperty("orientation").GetString());
        Assert.Equal("fit", options.GetProperty("scaling").GetString());
        Assert.True(options.GetProperty("collate").GetBoolean());
        await WaitUntilAsync(() => transport.Sent.Any(raw => TypeOf(raw) == "job_status"));
        var status = transport.Sent.Select(raw => JsonSerializer.Deserialize<Envelope>(raw, ProtocolJson.Options)!)
            .First(message => message.Type == "job_status");
        Assert.Equal(123, status.Payload.GetProperty("job_id").GetInt64());
        Assert.Equal("spooled", status.Payload.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task QueuedGrantsArePersistedAndRenewedWhilePreviousPdfIsStillFetching()
    {
        var store = new JobStore(_path);
        var cloud = new TestCloud { Grants = [Grant("1"), Grant("2"), Grant("3")] };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new TestFetcher { WaitFor = release.Task };
        await using var runtime = Runtime(store, cloud, fetcher: fetcher);
        await runtime.ApplyConfigAsync(new ConfigSync("2", "PC", [], new AgentIntervals(30, 30, 15, 1)));
        await runtime.StartAsync();
        try
        {
            await runtime.PollLeasesAsync(default);
            Assert.Equal(JobStates.Fetching, store.Get("1")!.State);
            Assert.Equal(JobStates.Granted, store.Get("2")!.State);
            Assert.Equal(JobStates.Granted, store.Get("3")!.State);
            await WaitUntilAsync(() => cloud.Renewals.Any(leases => leases.Count == 3));
        }
        finally
        {
            release.TrySetResult();
            await WaitUntilAsync(() => store.GetByState(JobStates.Spooled).Count == 3);
        }
    }

    [Fact]
    public async Task Hoeherer_grant_nach_fetch_fehler_wird_angenommen_nach_spooled_ignoriert()
    {
        var store = new JobStore(_path);
        var fetcher = new TestFetcher { Error = new InvalidOperationException("download") };
        var processor = Processor(store, new TestCloud(), fetcher);
        await processor.HandleGrantAsync(Grant("j1", "one", 1));
        Assert.Equal(JobStates.Failed, store.Get("j1")!.State);

        fetcher.Error = null;
        await processor.HandleGrantAsync(Grant("j1", "two", 2));
        Assert.Equal(JobStates.Spooled, store.Get("j1")!.State);
        await processor.HandleGrantAsync(Grant("j1", "three", 3));
        Assert.Equal("two", store.Get("j1")!.LeaseToken);
    }

    [Fact]
    public async Task Submit_fehler_setzen_failed_oder_uncertain_und_wiederholen_nicht()
    {
        var rejectedStore = new JobStore(_path + "-rejected");
        var rejectedCloud = new TestCloud();
        var rejected = Processor(rejectedStore, rejectedCloud, submitter: new ThrowingSubmitter(new PrintRejectedException("queue")));
        await rejected.HandleGrantAsync(Grant("rejected"));
        Assert.Equal(JobStates.Failed, rejectedStore.Get("rejected")!.State);
        Assert.Contains(rejectedStore.PendingOutbox(), item => item.PayloadJson.Contains("\"phase\":\"submitting\""));

        var uncertainStore = new JobStore(_path + "-uncertain");
        var submitter = new ThrowingSubmitter(new InvalidOperationException("lost"));
        var uncertain = Processor(uncertainStore, new TestCloud(), submitter: submitter);
        await uncertain.HandleGrantAsync(Grant("uncertain", "one", 1));
        await uncertain.HandleGrantAsync(Grant("uncertain", "two", 2));
        Assert.Equal(JobStates.Uncertain, uncertainStore.Get("uncertain")!.State);
        Assert.Equal(1, submitter.Calls);
    }

    [Fact]
    public async Task Cancel_waehrend_submitting_bricht_nach_spoolen_ab_und_meldet_canceled()
    {
        var store = new JobStore(_path);
        var cloud = new TestCloud();
        var submitter = new BlockingSubmitter();
        var spooler = new TestSpooler();
        var processor = Processor(store, cloud, submitter: submitter, spooler: spooler);
        var processing = processor.HandleGrantAsync(Grant("j1"));
        await submitter.Started.Task;
        await processor.CancelAsync("j1");
        submitter.Release.SetResult(new SubmitResult("77"));
        await processing;

        Assert.Equal(1, spooler.CancelCalls);
        Assert.Equal(JobStates.Abandoned, store.Get("j1")!.State);
        Assert.Contains(store.PendingOutbox(), item => item.PayloadJson.Contains("canceled"));
    }

    [Fact]
    public async Task Config_cache_ueberlebt_neue_runtime_und_jobstore_instanz()
    {
        var config = new ConfigSync("agent", "Name", [new RouteConfig(1, 2, "Q", "driver")], new AgentIntervals(30, 30, 15, 20));
        var first = Runtime(new JobStore(_path), new TestCloud());
        await first.ApplyConfigAsync(config);
        await first.DisposeAsync();

        await using var second = Runtime(new JobStore(_path), new TestCloud());
        Assert.Equal("agent", second.CachedConfig!.AgentId);
        Assert.Single(second.CachedConfig.Routes);
    }

    [Fact]
    public async Task Cleanup_loescht_alte_jobs_und_pdf_dateien_und_behaelt_junge()
    {
        var store = new JobStore(_path);
        var oldPdf = Path.Combine(Path.GetTempPath(), $"ecpg-old-{Guid.NewGuid()}.pdf");
        await File.WriteAllTextAsync(oldPdf, "pdf");
        store.Insert(Job(JobStates.Done, "old", _now.AddDays(-8), oldPdf));
        store.Insert(Job(JobStates.Done, "new", _now.AddDays(-6), null));
        await using var runtime = Runtime(store, new TestCloud());

        await runtime.StartAsync();

        Assert.Null(store.Get("old"));
        Assert.False(File.Exists(oldPdf));
        Assert.NotNull(store.Get("new"));
    }

    [Fact]
    public async Task Lease_poll_wird_bei_ausstehendem_update_uebersprungen()
    {
        var store = new JobStore(_path);
        var cloud = new TestCloud();
        await using var runtime = Runtime(store, cloud, new TestUpdateGate(true));
        await runtime.PollLeasesAsync(CancellationToken.None);
        Assert.Equal(0, cloud.LeaseRequests);
    }

    private AgentRuntime Runtime(JobStore store, ICloudChannel cloud, IUpdateGate? updateGate = null,
        GoodSubmitter? submitter = null, TestFetcher? fetcher = null) => new(store,
        new RecoveryService(store, new TestSpooler(), new FixedClock(_now)),
        Processor(store, cloud, fetcher: fetcher, submitter: submitter), cloud, new TestReadiness(), new TestInventory(), "1.0", new HostInfo("h", "os", "m"),
        new FixedClock(_now), updateGate: updateGate);
    private JobProcessor Processor(JobStore store, ICloudChannel cloud, TestFetcher? fetcher = null, IPrintSubmitter? submitter = null, TestSpooler? spooler = null) =>
        new(store, cloud, fetcher ?? new TestFetcher(), submitter ?? new GoodSubmitter(), spooler ?? new TestSpooler(), new FixedClock(_now));
    private static LeaseGrant Grant(string id, string token = "t", int generation = 1) => new(id, token, generation, 60, "alarm", "url", "pdf", new(), new RouteConfig(1, 2, "Q", "driver"));
    private static StoredJob Job(string state, string id = "j1", DateTimeOffset? updated = null, string? pdf = null) => new(id, "t", 1, state, "alarm", "url", "pdf", "{}", JsonSerializer.Serialize(new RouteConfig(1, 2, "Q", "driver"), ProtocolJson.Options), pdf, null, "name", 0, null, updated ?? DateTimeOffset.UtcNow, updated ?? DateTimeOffset.UtcNow);
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
    private static string TypeOf(string json) => JsonSerializer.Deserialize<Envelope>(json, ProtocolJson.Options)!.Type;
    private static bool IsHello(string json) => TypeOf(json) == "hello";

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + "-rejected", _path + "-uncertain" })
            TestFiles.DeleteDatabase(path);
    }

    private sealed record Sent(string Type, object Payload);
    private sealed class TestCloud : ICloudChannel
    {
        public int LeaseRequests { get; private set; }
        public LeaseGrant[] Grants { get; set; } = [];
        public ConcurrentQueue<IReadOnlyList<LeaseReference>> Renewals { get; } = new();
        public List<Sent> Messages { get; } = [];
        public Task<LeaseGrant[]> RequestLeasesAsync(int max)
        {
            LeaseRequests++;
            var grants = Grants;
            Grants = [];
            return Task.FromResult(grants);
        }
        public Task<IntentResult> SendIntentAsync(string jobId, string token) => Task.FromResult(new IntentResult(jobId, true));
        public Task EnqueueStatusAsync(JobStatusMessage status) => Task.CompletedTask;
        public Task RenewAsync(IReadOnlyList<LeaseReference> leases) { Renewals.Enqueue(leases); return Task.CompletedTask; }
        public Task SendMessageAsync(string type, object payload, CancellationToken cancellationToken = default) { Messages.Add(new(type, payload)); return Task.CompletedTask; }
    }
    private sealed class TestUpdateGate(bool isUpdatePending) : IUpdateGate
    {
        public bool IsUpdatePending => isUpdatePending;
    }
    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class TestFetcher : IArtifactFetcher
    {
        public Exception? Error { get; set; }
        public Task? WaitFor { get; init; }
        public async Task<string> FetchAsync(LeaseGrant grant, CancellationToken cancellationToken)
        {
            if (WaitFor is not null) await WaitFor.WaitAsync(cancellationToken);
            if (Error is not null) throw Error;
            return "x.pdf";
        }
    }
    private sealed class GoodSubmitter : IPrintSubmitter
    {
        public string Name => "test";
        public PrintRequest? LastRequest { get; private set; }
        public Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new SubmitResult("42"));
        }
    }
    private sealed class ThrowingSubmitter(Exception error) : IPrintSubmitter { public int Calls { get; private set; } public string Name => "test"; public Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken) { Calls++; return Task.FromException<SubmitResult>(error); } }
    private sealed class BlockingSubmitter : IPrintSubmitter { public TaskCompletionSource<bool> Started { get; } = new(); public TaskCompletionSource<SubmitResult> Release { get; } = new(); public string Name => "test"; public Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken) { Started.SetResult(true); return Release.Task; } }
    private sealed class TestSpooler : IPrintSpooler { public int CancelCalls { get; private set; } public Task<SpoolJobInfo?> FindJobByDocumentNameAsync(string queue, string documentName, CancellationToken cancellationToken) => Task.FromResult<SpoolJobInfo?>(null); public Task<SpoolJobState> GetJobStateAsync(string queue, string spoolRef, CancellationToken cancellationToken) => Task.FromResult(SpoolJobState.InQueue); public Task CancelJobAsync(string queue, string spoolRef, CancellationToken cancellationToken) { CancelCalls++; return Task.CompletedTask; } }
    private sealed class TestReadiness : IReadinessProvider { public Task<IReadOnlyList<RouteReadiness>> GetReadinessAsync(IReadOnlyList<RouteConfig> routes, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RouteReadiness>>([]); }
    private sealed class TestInventory : IInventoryProvider { public Task<IReadOnlyList<QueueInfo>> GetInventoryAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QueueInfo>>([]); }
    private sealed class QueueTransport : IWebSocketTransport
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        public ConcurrentQueue<string> Sent { get; } = [];
        public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken cancellationToken = default) { Sent.Enqueue(message); return Task.CompletedTask; }
        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default) => await _incoming.Reader.ReadAsync(cancellationToken);
        public Task CloseAsync(CancellationToken cancellationToken = default) { _incoming.Writer.TryWrite(null); return Task.CompletedTask; }
        public Task EnqueueAsync(Envelope message) => _incoming.Writer.WriteAsync(JsonSerializer.Serialize(message, ProtocolJson.Options)).AsTask();
    }
}

