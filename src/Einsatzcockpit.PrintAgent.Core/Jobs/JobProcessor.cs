using System.Collections.Concurrent;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Core.Jobs;

public sealed class JobProcessor
{
    private readonly JobStore _store;
    private readonly ICloudChannel _cloud;
    private readonly IArtifactFetcher _fetcher;
    private readonly IPrintSubmitter _submitter;
    private readonly IPrintSpooler _spooler;
    private readonly IClock _clock;
    private readonly ILogger<JobProcessor> _logger;
    private readonly SemaphoreSlim _grants = new(2, 2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _routes = new();

    public JobProcessor(JobStore store, ICloudChannel cloud, IArtifactFetcher fetcher, IPrintSubmitter submitter, IPrintSpooler spooler, IClock clock, ILogger<JobProcessor>? logger = null)
    {
        _store = store; _cloud = cloud; _fetcher = fetcher; _submitter = submitter; _spooler = spooler; _clock = clock;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>.Instance;
    }

    public async Task HandleGrantAsync(LeaseGrant grant, CancellationToken cancellationToken = default)
    {
        var existing = _store.Get(grant.JobId);
        if (existing is not null)
        {
            if (existing.LeaseToken == grant.LeaseToken) return;
            // Ein neuer Grant (hoehere Generation) ist nur zulaessig, solange dieser Agent den Job
            // nachweislich nie an den Spooler uebergeben hat: vor der Uebergabe, verworfen oder
            // beim Abruf gescheitert (dann ohne SpoolRef und ohne Uebergabe-Marker).
            if (existing.Generation >= grant.LeaseGeneration || !JobStates.Regrantable(existing))
            {
                _logger.LogInformation("Neuer Grant für Job {JobId} wird ab Zustandsphase {State} ignoriert.", grant.JobId, existing.State);
                return;
            }
        }
        var now = _clock.UtcNow;
        var job = new StoredJob(grant.JobId, grant.LeaseToken, grant.LeaseGeneration, JobStates.Granted, grant.DocumentType,
            grant.ArtifactUrl, grant.RenderKind, JsonSerializer.Serialize(grant.Options, ProtocolJson.Options), JsonSerializer.Serialize(grant.Route, ProtocolJson.Options), null, null,
            $"ECPG-{grant.JobId}-g{grant.LeaseGeneration}", existing?.Attempts ?? 0, null, existing?.CreatedAt ?? now, now);
        if (existing is null) _store.Insert(job); else _store.Update(job);
        // Auch wartende Grants sofort persistieren, damit der Runtime-Renewal sie erfasst.
        await _grants.WaitAsync(cancellationToken);
        var route = _routes.GetOrAdd(grant.Route.LocalQueue, _ => new SemaphoreSlim(1, 1));
        try
        {
            await route.WaitAsync(cancellationToken);
            try { await ProcessAsync(grant, cancellationToken); }
            finally { route.Release(); }
        }
        finally { _grants.Release(); }
    }

    private async Task ProcessAsync(LeaseGrant grant, CancellationToken cancellationToken)
    {
        var job = _store.Get(grant.JobId);
        if (job is null || job.LeaseToken != grant.LeaseToken || !JobStates.BeforeSubmitting(job.State)) return;
        try
        {
            // Kein Status an die Cloud vor der Druckfreigabe: "printing" wuerde dort die Phase
            // auf "spooled" setzen und den folgenden print_intent ungueltig machen.
            job = Change(job, JobStates.Fetching); _store.Update(job);
            var pdfPath = await _fetcher.FetchAsync(grant, cancellationToken);
            // Abandon/Cancel oder ein neuer Lease können während des Downloads eintreffen.
            job = _store.Get(grant.JobId);
            if (job is null || job.LeaseToken != grant.LeaseToken || !JobStates.BeforeSubmitting(job.State)) return;
            job = Change(job, JobStates.IntentPending, pdfPath: pdfPath); _store.Update(job);
            var intent = await _cloud.SendIntentAsync(job.JobId, job.LeaseToken);
            job = _store.Get(job.JobId);
            if (job is null || job.LeaseToken != grant.LeaseToken || !JobStates.BeforeSubmitting(job.State)) return;
            if (!intent.Ok)
            {
                _store.Update(Change(job, JobStates.Abandoned, error: intent.Reason));
                return;
            }
            // Der persistierte Zustand schützt nach einem Prozessabbruch vor einer zweiten Übergabe.
            job = Change(job, JobStates.Submitting) with { Attempts = job.Attempts + 1 }; _store.Update(job);
            SubmitResult result;
            try
            {
                result = await _submitter.SubmitAsync(new PrintRequest(job.PdfPath!, grant.Route, grant.Options, job.DocumentName), cancellationToken);
            }
            catch (PrintRejectedException ex)
            {
                // Der Druckweg hat den Auftrag nachweislich nicht angenommen (z. B. Queue fehlt,
                // Spooler lehnt StartDoc ab) – sicherer Fehlschlag, die Cloud darf Ersatz drucken.
                _logger.LogWarning(ex, "Druckweg hat Job {JobId} abgelehnt.", job.JobId);
                job = Change(job, JobStates.Failed, error: ex.Message); _store.Update(job);
                await StatusAsync(job, "failed", "submitting", error: ex.Message);
                return;
            }
            catch (Exception ex)
            {
                // Abbruch mitten in der Uebergabe: ob Seiten beim Drucker angekommen sind, ist
                // unbekannt. Nie automatisch neu drucken, sondern als unklar melden.
                _logger.LogError(ex, "Übergabe von Job {JobId} mit unklarem Ausgang abgebrochen.", job.JobId);
                var error = "Übergabe an den Drucker unterbrochen – bitte am Drucker prüfen: " + ex.Message;
                job = Change(job, JobStates.Uncertain, error: error); _store.Update(job);
                await StatusAsync(job, "uncertain", "submitting", error: error);
                return;
            }
            job = _store.Get(job.JobId) ?? job;
            job = Change(job, JobStates.Spooled, spoolRef: result.SpoolRef); _store.Update(job);
            if (job.CancelRequested)
            {
                await CancelSpooledAsync(job, cancellationToken);
                return;
            }
            await StatusAsync(job, "printing", "spooled", result.SpoolRef);
        }
        catch (Exception ex) when (_store.Get(grant.JobId) is { } current && JobStates.BeforeSubmitting(current.State))
        {
            if (current.LeaseToken != grant.LeaseToken) return;
            _logger.LogWarning(ex, "Abruf für Druckjob {JobId} ist fehlgeschlagen.", grant.JobId);
            current = Change(current, JobStates.Failed, error: ex.Message); _store.Update(current);
            await StatusAsync(current, "failed", "fetching", error: ex.Message);
        }
    }

    public async Task MonitorAsync(CancellationToken cancellationToken = default)
    {
        foreach (var job in _store.GetByState(JobStates.Spooled))
        {
            var route = JsonSerializer.Deserialize<RouteConfig>(job.RouteJson, ProtocolJson.Options)!;
            var state = await _spooler.GetJobStateAsync(route.LocalQueue, job.SpoolRef!, cancellationToken);
            if (state.Kind == SpoolJobStateKind.Gone)
            {
                var done = Change(job, JobStates.Done); _store.Update(done); await StatusAsync(done, "done", "spooled", done.SpoolRef);
            }
            else if (state.Kind == SpoolJobStateKind.Deleted)
            {
                var failed = Change(job, JobStates.Failed, error: "Druckauftrag wurde abgebrochen."); _store.Update(failed); await StatusAsync(failed, "failed", "spooled", failed.SpoolRef, failed.Error);
            }
            else if (state.Kind == SpoolJobStateKind.Error)
                await StatusAsync(job, "printing", "spooled", job.SpoolRef, state.Detail ?? $"Spooler-Fehler (Flags {state.Flags})");
        }
    }

    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = _store.Get(jobId); if (job is null) return;
        if (JobStates.BeforeSubmitting(job.State)) { _store.Update(Change(job, JobStates.Abandoned)); return; }
        if (job.State == JobStates.Submitting)
        {
            _store.Update(job with { CancelRequested = true, UpdatedAt = _clock.UtcNow });
            return;
        }
        if (job.State == JobStates.Spooled)
        {
            await CancelSpooledAsync(job, cancellationToken);
        }
    }

    public void Abandon(string jobId, string? reason = null)
    {
        var job = _store.Get(jobId); if (job is null) return;
        if (JobStates.BeforeSubmitting(job.State)) _store.Update(Change(job, JobStates.Abandoned, error: reason));
        else _logger.LogInformation("Abandon für Job {JobId} ab Übergabephase wird ignoriert.", jobId);
    }

    private async Task StatusAsync(StoredJob job, string status, string phase, string? spoolRef = null, string? error = null)
    {
        var message = new JobStatusMessage(job.JobId, job.LeaseToken, status, phase, spoolRef, error);
        _store.AddOutbox("job_status", JsonSerializer.Serialize(message, ProtocolJson.Options), _clock.UtcNow);
        await _cloud.EnqueueStatusAsync(message);
    }
    private async Task CancelSpooledAsync(StoredJob job, CancellationToken cancellationToken)
    {
        var route = JsonSerializer.Deserialize<RouteConfig>(job.RouteJson, ProtocolJson.Options)!;
        await _spooler.CancelJobAsync(route.LocalQueue, job.SpoolRef!, cancellationToken);
        var canceled = Change(job, JobStates.Abandoned) with { CancelRequested = false };
        _store.Update(canceled);
        await StatusAsync(canceled, "canceled", "spooled", job.SpoolRef);
    }
    private StoredJob Change(StoredJob job, string state, string? pdfPath = null, string? spoolRef = null, string? error = null) =>
        job with { State = state, PdfPath = pdfPath ?? job.PdfPath, SpoolRef = spoolRef ?? job.SpoolRef, Error = error, UpdatedAt = _clock.UtcNow };
}
