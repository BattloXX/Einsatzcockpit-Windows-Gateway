using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Cloud;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Tests;

public sealed class CloudTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ecpg-cloud-{Guid.NewGuid()}.db");

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(20, 60)]
    public void Reconnect_backoff_ist_exponentiell_und_gedeckelt(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), CloudConnection.ReconnectDelay(failures));
    }

    [Fact]
    public async Task Intent_ohne_verbindung_wird_nie_freigegeben()
    {
        var cloud = new CloudConnection(new FakeTransport(), new Uri("wss://example.test/ws"), "token", new JobStore(_path));
        var result = await cloud.SendIntentAsync("j1", "lease");
        Assert.False(result.Ok);
        Assert.Equal("no_connection", result.Reason);
    }

    [Fact]
    public async Task Outbox_behaelt_nachricht_bis_ack_und_sendet_immer_id()
    {
        var store = new JobStore(_path);
        var id = store.AddOutbox("job_status", "{\"job_id\":\"j1\"}", DateTimeOffset.UtcNow);
        var sent = new List<string>();
        var pump = new OutboxPump(store, (message, _) => { sent.Add(message); return Task.CompletedTask; });

        await pump.PumpAsync();
        await pump.PumpAsync();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, message => Assert.Equal(id,
            JsonSerializer.Deserialize<Envelope>(message, ProtocolJson.Options)!.Id));
        Assert.Single(store.PendingOutbox());

        pump.Ack(id);
        await pump.PumpAsync();
        Assert.Empty(store.PendingOutbox());
        Assert.Equal(2, sent.Count);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private sealed class FakeTransport : IWebSocketTransport
    {
        public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> ReceiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
