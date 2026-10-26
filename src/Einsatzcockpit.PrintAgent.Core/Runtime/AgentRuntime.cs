using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Cloud;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Einsatzcockpit.PrintAgent.Core.Status;
using Einsatzcockpit.PrintAgent.Core.Update;

namespace Einsatzcockpit.PrintAgent.Core.Runtime;

/// <summary>Plattformneutrale Orchestrierung des Agenten.</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly JobStore _store;
    private readonly RecoveryService _recovery;
    private readonly JobProcessor _processor;
    private readonly ICloudChannel _cloud;
    private readonly IReadinessProvider _readiness;
    private readonly IInventoryProvider _inventory;
    private readonly IClock _clock;
    private readonly string _version;
    private readonly HostInfo _hostInfo;
    private readonly IStatusSink? _statusSink;
    private readonly string? _cloudUrl;
    private readonly IUpdateGate _updateGate;
    private readonly List<Task> _tasks = [];
    private CancellationTokenSource? _lifetime;
    private ConfigSync? _config;
    private IReadOnlyList<RouteReadiness> _readinessResults = [];
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _lastConnectedAt;
    private string? _lastError;

    public AgentRuntime(JobStore store, RecoveryService recovery, JobProcessor processor,
        ICloudChannel cloud, IReadinessProvider readiness, IInventoryProvider inventory,
        string version, HostInfo hostInfo, IClock? clock = null, IStatusSink? statusSink = null,
        string? cloudUrl = null, IUpdateGate? updateGate = null)
    {
        _store = store;
        _recovery = recovery;
        _processor = processor;
        _cloud = cloud;
        _readiness = readiness;
        _inventory = inventory;
        _version = version;
        _hostInfo = hostInfo;
        _clock = clock ?? new SystemClock();
        _statusSink = statusSink;
        _cloudUrl = cloudUrl;
        _updateGate = updateGate ?? new NoUpdateGate();
        _startedAt = _clock.UtcNow;
        var cached = _store.GetValue("config_cache");
        if (cached is not null)
        {
            _config = JsonSerializer.Deserialize<ConfigSync>(cached, ProtocolJson.Options);
        }
        if (cloud is CloudConnection connection)
        {
            connection.ConfigSyncReceived += ApplyConfigAsync;
            connection.JobAvailable += message =>
            {
                _ = PollLeasesAsync(CancellationToken.None);
                return Task.CompletedTask;
            };
            connection.CancelJob += message => _processor.CancelAsync(message.JobId);
            connection.Abandon += message => { _processor.Abandon(message.JobId, message.Reason); return Task.CompletedTask; };
            connection.Connected += async () =>
            {
                await SendHelloAsync();
                await SendInventoryAsync(_lifetime?.Token ?? CancellationToken.None);
            };
        }
    }

    public ConfigSync? CachedConfig => _config;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetime is not null)
        {
            return;
        }
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _recovery.RecoverAsync(_lifetime.Token);
        if (_cloud is CloudConnection connection)
        {
            await connection.StartAsync(_lifetime.Token);
        }
        else
        {
            await SendHelloAsync();
            await SendInventoryAsync(_lifetime.Token);
        }
        await CleanupAsync();
        _tasks.Add(LoopAsync(PollLeasesAsync, () => Seconds(_config?.Intervals.LeasePollS, 15), _lifetime.Token));
        _tasks.Add(LoopAsync(RenewAsync, () => Seconds(_config?.Intervals.RenewS, 20), _lifetime.Token));
        _tasks.Add(LoopAsync(MonitorAsync, () => TimeSpan.FromSeconds(5), _lifetime.Token));
        _tasks.Add(LoopAsync(SendReadinessAsync, () => Seconds(_config?.Intervals.ReadinessS, 30), _lifetime.Token));
        _tasks.Add(LoopAsync(SendInventoryAsync, () => TimeSpan.FromMinutes(10), _lifetime.Token));
        _tasks.Add(LoopAsync(_ => CleanupAsync(), () => TimeSpan.FromDays(1), _lifetime.Token));
        _tasks.Add(LoopAsync(WriteStatusAsync, () => TimeSpan.FromSeconds(5), _lifetime.Token));
        await WriteStatusAsync(_lifetime.Token);
    }

    public Task ApplyConfigAsync(ConfigSync config)
    {
        _config = config;
        _store.SetValue("config_cache", JsonSerializer.Serialize(config, ProtocolJson.Options));
        return Task.CompletedTask;
    }

    private Task SendHelloAsync()
    {
        var cancellationToken = _lifetime?.Token ?? CancellationToken.None;
        // Bei jedem (Re-)Connect den aktuellen lokalen Stand melden, nicht den vom Programmstart:
        // die Cloud verlaengert damit gueltige Leases bzw. schickt abandon fuer verlorene.
        // Unklare Jobs laufen ueber die Outbox und werden hier nicht erneut aufgefuehrt.
        var openJobs = _store.GetByState(JobStates.Granted, JobStates.Fetching, JobStates.IntentPending,
                JobStates.Submitting, JobStates.Spooled)
            .Select(job => new OpenJob(job.JobId, job.LeaseToken, job.State))
            .ToArray();
        return _cloud.SendMessageAsync("hello", new HelloMessage(_version, _hostInfo, openJobs), cancellationToken);
    }

    public async Task PollLeasesAsync(CancellationToken cancellationToken)
    {
        if (_updateGate.IsUpdatePending)
        {
            return;
        }

        var grants = await _cloud.RequestLeasesAsync(5);
        foreach (var grant in grants)
        {
            _ = _processor.HandleGrantAsync(grant, cancellationToken);
        }
    }

    private Task RenewAsync(CancellationToken cancellationToken)
    {
        var leases = _store.GetByState(JobStates.Granted, JobStates.Fetching, JobStates.IntentPending,
                JobStates.Submitting, JobStates.Spooled)
            .Select(job => new LeaseReference(job.JobId, job.LeaseToken)).ToArray();
        return leases.Length == 0 ? Task.CompletedTask : _cloud.RenewAsync(leases);
    }

    private Task MonitorAsync(CancellationToken cancellationToken) => _processor.MonitorAsync(cancellationToken);

    private async Task SendReadinessAsync(CancellationToken cancellationToken)
    {
        var routes = _config?.Routes ?? [];
        var readiness = await _readiness.GetReadinessAsync(routes, cancellationToken);
        _readinessResults = readiness;
        await _cloud.SendMessageAsync("readiness", new ReadinessMessage(readiness), cancellationToken);
    }

    private async Task SendInventoryAsync(CancellationToken cancellationToken)
    {
        var queues = await _inventory.GetInventoryAsync(cancellationToken);
        await _cloud.SendMessageAsync("inventory", new InventoryMessage(queues), cancellationToken);
    }

    private Task CleanupAsync()
    {
        var now = _clock.UtcNow;
        foreach (var job in _store.Cleanup(now.AddDays(-7), now))
        {
            if (job.PdfPath is not null)
            {
                try { File.Delete(job.PdfPath); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    private Task WriteStatusAsync(CancellationToken cancellationToken)
    {
        if (_statusSink is null)
        {
            return Task.CompletedTask;
        }

        var connected = _cloud is CloudConnection connection && connection.IsConnected;
        if (_cloud is CloudConnection activeConnection && connected)
        {
            _lastConnectedAt = activeConnection.LastConnectedAt;
        }

        var readiness = _readinessResults.ToDictionary(route => route.RouteId);
        var routes = (_config?.Routes ?? []).Select(route =>
        {
            readiness.TryGetValue(route.RouteId, out var result);
            return new RouteStatus(route.PrinterName, route.LocalQueue, route.RenderMode, route.Priority,
                result?.Ready ?? false, result?.State ?? "unbekannt", result?.Detail);
        }).ToArray();
        var counts = _store.StateCounts().ToDictionary(item => item.State, item => item.Count);
        var open = _store.GetByState(JobStates.Granted, JobStates.Fetching, JobStates.IntentPending,
            JobStates.Submitting, JobStates.Spooled).Count;
        var uncertain = _store.GetByState(JobStates.Uncertain).Count;
        var snapshot = new AgentStatusSnapshot(_version, _startedAt, _clock.UtcNow, connected,
            _lastConnectedAt, _cloudUrl, _config?.Name, routes, counts, open, uncertain, _lastError,
            (_statusSink as StatusFile)?.UpdateStatus ?? new UpdateStatus());
        return _statusSink.WriteAsync(snapshot, cancellationToken);
    }

    private async Task LoopAsync(Func<CancellationToken, Task> action, Func<TimeSpan> interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval(), cancellationToken); await action(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }
    }

    private static TimeSpan Seconds(int? value, int fallback) => TimeSpan.FromSeconds(value is > 0 ? value.Value : fallback);

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null)
        {
            return;
        }
        await _lifetime.CancelAsync();
        try { await Task.WhenAll(_tasks); } catch (OperationCanceledException) { }
        if (_cloud is CloudConnection connection)
        {
            await connection.StopAsync();
        }
        try
        {
            await WriteStatusAsync(CancellationToken.None);
        }
        catch
        {
            // Beim Stoppen darf eine nicht schreibbare Statusdatei den Dienst nicht aufhalten.
        }
        _lifetime.Dispose();
        _lifetime = null;
    }
}
