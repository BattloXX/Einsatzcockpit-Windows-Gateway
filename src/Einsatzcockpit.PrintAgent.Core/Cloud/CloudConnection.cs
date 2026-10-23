using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private volatile int _heartbeatSeconds = 30;
    private volatile bool _configured;
    private readonly ILogger<CloudConnection> _logger;
    private string _connectionId = "";
    private string? _gatewayId;
    public DateTimeOffset? LastConnectedAt { get; private set; }

    public CloudConnection(IWebSocketTransport transport, Uri uri, string bearerToken, JobStore store, IClock? clock = null, ILogger<CloudConnection>? logger = null)
    {
        _logger = logger ?? NullLogger<CloudConnection>.Instance;
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
    public bool IsConnected => _connected && _configured;

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
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _transport.CloseAsync(timeout.Token); } catch { }
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
            _connectionId = Guid.NewGuid().ToString("N");
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? receive = null;
            Task? heartbeat = null;
            try
            {
                _logger.LogInformation("agent_connection_attempt gateway_id={GatewayId} connection_id={ConnectionId} at={At}", _gatewayId, _connectionId, _clock.UtcNow);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await _transport.ConnectAsync(_uri, _token, connectTimeout.Token);
                }
                _connected = true;
                _logger.LogInformation("agent_authentication_succeeded gateway_id={GatewayId} connection_id={ConnectionId} at={At}", _gatewayId, _connectionId, _clock.UtcNow);
                // Hello vor Outbox und allen anderen Nachrichten, bei jedem Reconnect.
                if (Connected is not null) await Connected();
                await _outbox.PumpAsync(session.Token);
                receive = ReceiveLoopAsync(session.Token);
                heartbeat = HeartbeatLoopAsync(session.Token);
                var completed = await Task.WhenAny(receive, heartbeat);
                await completed;
                failures = 0;
                _logger.LogWarning("agent_connection_closed gateway_id={GatewayId} connection_id={ConnectionId} close_code={CloseCode} at={At}",
                    _gatewayId, _connectionId, _transport.CloseCode, _clock.UtcNow);
                if (_transport.CloseCode == 4401)
                    _logger.LogWarning("agent_authentication_failed gateway_id={GatewayId} connection_id={ConnectionId} at={At}", _gatewayId, _connectionId, _clock.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_transport.ConnectStatusCode is 401 or 403)
                    _logger.LogWarning("agent_handshake_rejected gateway_id={GatewayId} connection_id={ConnectionId} http_status={Status} at={At}",
                        _gatewayId, _connectionId, _transport.ConnectStatusCode, _clock.UtcNow);
                // Keine rohen Fehlerantworten, Header, Tokens oder URLs loggen.
                _logger.LogWarning("agent_connection_failed gateway_id={GatewayId} connection_id={ConnectionId} error_type={ErrorType} at={At}",
                    _gatewayId, _connectionId, ex.GetType().Name, _clock.UtcNow);
            }
            finally
            {
                if (_configured) failures = 0;
                _connected = false;
                _configured = false;
                await session.CancelAsync();
                foreach (var request in _requests.Values) request.TrySetCanceled();
                try { using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _transport.CloseAsync(close.Token); } catch { }
                if (receive is not null) { try { await receive; } catch { } }
                if (heartbeat is not null) { try { await heartbeat; } catch { } }
            }
            await Task.Delay(ReconnectDelay(failures++), cancellationToken);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatSeconds), cancellationToken);
            await SendMessageAsync("heartbeat", new { }, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveTimeout.CancelAfter(TimeSpan.FromSeconds(75));
            var raw = await _transport.ReceiveAsync(receiveTimeout.Token);
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
                if (string.IsNullOrWhiteSpace(config.AgentId) || config.Routes is null || config.Intervals is null)
                    throw new JsonException("Unvollstaendige Agent-Konfiguration.");
                _heartbeatSeconds = Math.Max(1, config.Intervals.HeartbeatS);
                if (ConfigSyncReceived is not null) await ConfigSyncReceived(config);
                if (!_configured)
                {
                    LastConnectedAt = _clock.UtcNow;
                    _logger.LogInformation("agent_reconnect_ready gateway_id={GatewayId} connection_id={ConnectionId} at={At}",
                        config.AgentId, _connectionId, LastConnectedAt);
                }
                _gatewayId = config.AgentId;
                _configured = true;
                _logger.LogInformation("agent_config_sync gateway_id={GatewayId} connection_id={ConnectionId} at={At}",
                    config.AgentId, _connectionId, LastConnectedAt);
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
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _transport.SendAsync(raw, timeout.Token);
        }
        finally { _sendGate.Release(); }
    }

    private static T Deserialize<T>(JsonElement payload) => payload.Deserialize<T>(ProtocolJson.Options)!;
}
