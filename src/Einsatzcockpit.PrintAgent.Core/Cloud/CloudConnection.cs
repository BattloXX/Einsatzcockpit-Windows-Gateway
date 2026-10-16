using System.Collections.Concurrent;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Core.Cloud;

public sealed class CloudConnection : ICloudChannel
{
    private readonly IWebSocketTransport _transport;
    private readonly Uri _uri;
    private readonly string _token;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _requests = new();
    private readonly OutboxPump _outbox;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private volatile bool _connected;
    private int _heartbeatSeconds = 30;

    public CloudConnection(IWebSocketTransport transport, Uri uri, string bearerToken, JobStore store, IClock? clock = null)
    {
        _transport = transport;
        _uri = uri;
        _token = bearerToken;
        _clock = clock ?? new SystemClock();
        _outbox = new OutboxPump(store, SendRawAsync);
    }

    public event Func<ConfigSync, Task>? ConfigSyncReceived;
    public event Func<JobAvailableMessage, Task>? JobAvailable;
    public event Func<CancelJobMessage, Task>? CancelJob;
    public event Func<AbandonMessage, Task>? Abandon;
    public event Func<AckMessage, Task>? AckReceived;
    public event Func<Task>? Connected;
    public bool IsConnected => _connected;

    public static TimeSpan ReconnectDelay(int failures)
    {
        var seconds = Math.Min(60, 1 << Math.Min(Math.Max(failures, 0), 6));
        return TimeSpan.FromSeconds(seconds);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_lifetime is null)
        {
            return;
        }
        await _lifetime.CancelAsync();
        try { await _transport.CloseAsync(); } catch { }
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _loop = null;
        _lifetime.Dispose();
        _lifetime = null;
    }

    public async Task<LeaseGrant[]> RequestLeasesAsync(int max)
    {
        var answer = await RequestAsync("lease_request", new LeaseRequest(max));
        return answer is null ? [] : Deserialize<LeaseGrantMessage>(answer.Payload).Jobs.ToArray();
    }

    public async Task<IntentResult> SendIntentAsync(string jobId, string token)
    {
        var answer = await RequestAsync("print_intent", new { job_id = jobId, lease_token = token });
        return answer is null ? new IntentResult(jobId, false, "no_connection") : Deserialize<IntentResult>(answer.Payload);
    }

    public Task EnqueueStatusAsync(JobStatusMessage status)
    {
        return _connected ? _outbox.PumpAsync() : Task.CompletedTask;
    }

    public Task RenewAsync(IReadOnlyList<LeaseReference> leases)
    {
        return SendMessageAsync("lease_renew", new LeaseRenew(leases));
    }

    public Task SendMessageAsync(string type, object payload, CancellationToken cancellationToken = default)
    {
        return SendEnvelopeAsync(new Envelope(type, null, null, ProtocolJson.Element(payload)), cancellationToken);
    }

    private async Task<Envelope?> RequestAsync(string type, object payload)
    {
        if (!_connected)
        {
            return null;
        }
        var rid = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(rid, completion))
        {
            return null;
        }
        try
        {
            await SendEnvelopeAsync(new Envelope(type, null, rid, ProtocolJson.Element(payload)), CancellationToken.None);
            var timeout = Task.Delay(TimeSpan.FromSeconds(15));
            var done = await Task.WhenAny(completion.Task, timeout);
            return done == completion.Task ? await completion.Task : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _requests.TryRemove(rid, out _);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _transport.ConnectAsync(_uri, _token, cancellationToken);
                _connected = true;
                failures = 0;
                await _outbox.PumpAsync(cancellationToken);
                if (Connected is not null)
                {
                    await Connected();
                }
                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(_heartbeatSeconds));
                var receive = ReceiveLoopAsync(cancellationToken);
                while (!cancellationToken.IsCancellationRequested && !receive.IsCompleted)
                {
                    if (await heartbeat.WaitForNextTickAsync(cancellationToken))
                    {
                        await SendMessageAsync("heartbeat", new { }, cancellationToken);
                    }
                }
                await receive;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { }
            finally
            {
                _connected = false;
                foreach (var request in _requests.Values)
                {
                    request.TrySetCanceled();
                }
                try { await _transport.CloseAsync(CancellationToken.None); } catch { }
            }
            var delay = ReconnectDelay(failures++);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var raw = await _transport.ReceiveAsync(cancellationToken);
            if (raw is null)
            {
                return;
            }
            var message = JsonSerializer.Deserialize<Envelope>(raw, ProtocolJson.Options);
            if (message is not null)
            {
                await DispatchAsync(message);
            }
        }
    }

    private async Task DispatchAsync(Envelope message)
    {
        if (message.Rid is not null && _requests.TryGetValue(message.Rid, out var request))
        {
            request.TrySetResult(message);
            return;
        }
        switch (message.Type)
        {
            case "ping": await SendMessageAsync("pong", new { }); break;
            case "ack" when Deserialize<AckMessage>(message.Payload) is { } ack:
                _outbox.Ack(ack.Id);
                if (AckReceived is not null) await AckReceived(ack);
                break;
            case "config_sync":
                var config = Deserialize<ConfigSync>(message.Payload);
                _heartbeatSeconds = Math.Max(1, config.Intervals.HeartbeatS);
                if (ConfigSyncReceived is not null) await ConfigSyncReceived(config);
                break;
            case "job_available" when JobAvailable is not null: await JobAvailable(Deserialize<JobAvailableMessage>(message.Payload)); break;
            case "cancel_job" when CancelJob is not null: await CancelJob(Deserialize<CancelJobMessage>(message.Payload)); break;
            case "abandon" when Abandon is not null: await Abandon(Deserialize<AbandonMessage>(message.Payload)); break;
        }
    }

    private async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var raw = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        await SendRawAsync(raw, cancellationToken);
    }

    private async Task SendRawAsync(string raw, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Keine Cloud-Verbindung.");
        }
        await _sendGate.WaitAsync(cancellationToken);
        try { await _transport.SendAsync(raw, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private static T Deserialize<T>(JsonElement payload) => payload.Deserialize<T>(ProtocolJson.Options)!;
}
