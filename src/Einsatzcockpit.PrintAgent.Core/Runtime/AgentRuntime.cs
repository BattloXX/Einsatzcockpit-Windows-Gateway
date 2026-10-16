using System.Collections.Concurrent;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Cloud;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

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
    private readonly SemaphoreSlim _grants = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _routes = new();
    private readonly List<Task> _tasks = [];
    private CancellationTokenSource? _lifetime;
    private ConfigSync? _config;

    public AgentRuntime(JobStore store, RecoveryService recovery, JobProcessor processor,
        ICloudChannel cloud, IReadinessProvider readiness, IInventoryProvider inventory,
        string version, HostInfo hostInfo, IClock? clock = null)
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
        var cached = _store.GetValue("config_cache");
        if (cached is not null)
        {
            _config = JsonSerializer.Deserialize<ConfigSync>(cached, ProtocolJson.Options);
        }
        if (cloud is CloudConnection connection)
        {
            connection.ConfigSyncReceived += ApplyConfigAsync;
            connection.JobAvailable += _ => PollLeasesAsync(CancellationToken.None);
            connection.CancelJob += message => _processor.CancelAsync(message.JobId);
            connection.Abandon += message => { _processor.Abandon(message.JobId, message.Reason); return Task.CompletedTask; };
            connection.Connected += SendHelloAsync;
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
        }
        await SendInventoryAsync(_lifetime.Token);
        await CleanupAsync();
        _tasks.Add(LoopAsync(PollLeasesAsync, () => Seconds(_config?.Intervals.LeasePollS, 15), _lifetime.Token));
        _tasks.Add(LoopAsync(RenewAsync, () => Seconds(_config?.Intervals.RenewS, 20), _lifetime.Token));
        _tasks.Add(LoopAsync(MonitorAsync, () => TimeSpan.FromSeconds(5), _lifetime.Token));
        _tasks.Add(LoopAsync(SendReadinessAsync, () => Seconds(_config?.Intervals.ReadinessS, 30), _lifetime.Token));
        _tasks.Add(LoopAsync(SendInventoryAsync, () => TimeSpan.FromMinutes(10), _lifetime.Token));
        _tasks.Add(LoopAsync(_ => CleanupAsync(), () => TimeSpan.FromDays(1), _lifetime.Token));
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
        var grants = await _cloud.RequestLeasesAsync(5);
        foreach (var grant in grants)
        {
            _ = HandleGrantAsync(grant, cancellationToken);
        }
    }

    private async Task HandleGrantAsync(LeaseGrant grant, CancellationToken cancellationToken)
    {
        await _grants.WaitAsync(cancellationToken);
        var route = _routes.GetOrAdd(grant.Route.LocalQueue, _ => new SemaphoreSlim(1, 1));
        try
        {
            await route.WaitAsync(cancellationToken);
            try { await _processor.HandleGrantAsync(grant, cancellationToken); }
            finally { route.Release(); }
        }
        finally { _grants.Release(); }
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
        await _cloud.SendMessageAsync("readiness", new ReadinessMessage(readiness), cancellationToken);
    }

    private async Task SendInventoryAsync(CancellationToken cancellationToken)
    {
        var queues = await _inventory.GetInventoryAsync(cancellationToken);
        await _cloud.SendMessageAsync("inventory", new InventoryMessage(queues), cancellationToken);
    }

    private Task CleanupAsync()
    {
        foreach (var job in _store.Cleanup(_clock.UtcNow.AddDays(-7)))
        {
            if (job.PdfPath is not null)
            {
                try { File.Delete(job.PdfPath); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    private async Task LoopAsync(Func<CancellationToken, Task> action, Func<TimeSpan> interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval(), cancellationToken); await action(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { /* Der nächste Durchlauf versucht es erneut. */ }
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
        _lifetime.Dispose();
        _lifetime = null;
    }
}
