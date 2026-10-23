using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Einsatzcockpit.PrintAgent.Core.Cloud;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class ProtocolConnectionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecpa-presence-{Guid.NewGuid()}.db");
    private const string Config = """{"type":"config_sync","payload":{"agent_id":2,"name":"KDOF1","routes":[],"intervals":{"heartbeat_s":1,"readiness_s":30,"lease_poll_s":15,"renew_s":20}}}""";

    [Fact]
    public void ActualCloudNumericIdsAreReadAndWrittenWithoutChangingTokens()
    {
        var config = JsonSerializer.Deserialize<Envelope>(Config, ProtocolJson.Options)!;
        Assert.Equal("2", config.Payload.Deserialize<ConfigSync>(ProtocolJson.Options)!.AgentId);
        var grant = """{"job_id":123,"lease_token":"000123","lease_generation":1,"lease_expires_in":60,"document_type":"einsatzinfo","artifact_url":"https://example/pdf","render_kind":"pdf","options":{},"route":{"route_id":4,"printer_id":5,"local_queue":"HP","render_mode":"driver"}}""";
        Assert.Equal("123", JsonSerializer.Deserialize<LeaseGrant>(grant, ProtocolJson.Options)!.JobId);
        var status = ProtocolJson.Element(new JobStatusMessage("123", "000123", "done", "spooled", "001"));
        Assert.Equal(JsonValueKind.Number, status.GetProperty("job_id").ValueKind);
        Assert.Equal("000123", status.GetProperty("lease_token").GetString());
        Assert.Equal("001", status.GetProperty("spool_ref").GetString());
        Assert.Equal("123", JsonSerializer.Deserialize<IntentResult>("""{"job_id":123,"ok":true}""", ProtocolJson.Options)!.JobId);
        Assert.Equal("123", JsonSerializer.Deserialize<JobAvailableMessage>("""{"job_ids":[123]}""", ProtocolJson.Options)!.JobIds.Single());
        Assert.Equal(JsonValueKind.Number, ProtocolJson.Element(new LeaseReference("123", "t")).GetProperty("job_id").ValueKind);
        Assert.Equal(JsonValueKind.Number, ProtocolJson.Element(new OpenJob("123", "t", "spooled")).GetProperty("job_id").ValueKind);
    }

    [Fact]
    public async Task NumericConfigEnablesOnlineAndHeartbeatsContinue()
    {
        var transport = new Transport();
        var cloud = Connection(transport);
        await cloud.StartAsync();
        try
        {
            await Wait(() => cloud.IsConnected);
            await Wait(() => transport.Sent.Count(s => s.Contains("heartbeat")) >= 2);
            Assert.Equal("KDOF1", transport.ConfiguredName);
            await Wait(() => transport.Sent.Any(s => s.Contains("ping")));
        }
        finally { await cloud.StopAsync(); }
    }

    [Fact]
    public async Task NetworkDisconnectReconnectsPromptlyAndServiceRestartSendsHelloAgain()
    {
        var transport = new Transport();
        var cloud = Connection(transport);
        await cloud.StartAsync();
        try
        {
            await Wait(() => cloud.IsConnected);
            transport.Input.Writer.TryWrite("CLOSE");
            await Wait(() => transport.Connections >= 2 && cloud.IsConnected);
            Assert.True(transport.Sent.Count(s => s.Contains("hello")) >= 2);
            await cloud.StopAsync();
            Assert.False(cloud.IsConnected);
            await cloud.StartAsync();
            await Wait(() => transport.Connections >= 3 && cloud.IsConnected);
        }
        finally { await cloud.StopAsync(); }
    }

    [Fact]
    public async Task MalformedConfigIsNotOnlineAndReceiveFailureTriggersReconnect()
    {
        var transport = new Transport { FailFirstConfig = true };
        var cloud = Connection(transport);
        await cloud.StartAsync();
        try { await Wait(() => transport.Connections >= 2 && cloud.IsConnected); }
        finally { await cloud.StopAsync(); }
    }

    private CloudConnection Connection(Transport transport)
    {
        var cloud = new CloudConnection(transport, new Uri("wss://example.test/ws/print-agent"), "test-token", new JobStore(_path));
        cloud.Connected += () => cloud.SendMessageAsync("hello", new HelloMessage("test", new HostInfo("KDOF1", "Windows", "test"), []));
        cloud.ConfigSyncReceived += config => { transport.ConfiguredName = config.Name; return Task.CompletedTask; };
        return cloud;
    }

    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    public void Dispose() => TestFiles.DeleteDatabase(_path);

    private sealed class Transport : IWebSocketTransport
    {
        public Channel<string> Input { get; } = Channel.CreateUnbounded<string>();
        public ConcurrentQueue<string> Sent { get; } = new();
        public int Connections;
        public bool FailFirstConfig;
        public string? ConfiguredName;
        public Task ConnectAsync(Uri uri, string token, CancellationToken cancellationToken) { Interlocked.Increment(ref Connections); return Task.CompletedTask; }
        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            if (message.Contains("hello")) Input.Writer.TryWrite(FailFirstConfig && Connections == 1 ? "{bad-json}" : Config);
            return Task.CompletedTask;
        }
        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var value = await Input.Reader.ReadAsync(cancellationToken);
            return value == "CLOSE" ? null : value;
        }
        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
