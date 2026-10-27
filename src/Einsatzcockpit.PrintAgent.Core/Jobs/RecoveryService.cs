using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace Einsatzcockpit.PrintAgent.Core.Jobs;

public sealed class RecoveryService
{
    private readonly JobStore _store;
    private readonly IPrintSpooler _spooler;
    private readonly IClock _clock;
    private readonly ILogger<RecoveryService> _logger;
    public RecoveryService(JobStore store, IPrintSpooler spooler, IClock clock, ILogger<RecoveryService>? logger = null)
    { _store = store; _spooler = spooler; _clock = clock; _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RecoveryService>.Instance; }

    public async Task<IReadOnlyList<OpenJob>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var job in _store.GetByState(JobStates.Granted, JobStates.Fetching, JobStates.IntentPending))
        {
            _store.Update(job with { State = JobStates.Abandoned, UpdatedAt = _clock.UtcNow });
            _logger.LogInformation("Unvollständiger Job {JobId} vor Druckübergabe wurde verworfen.", job.JobId);
        }
        // Nach einer Beobachtungsluecke beweist ein verschwundener Auftrag keinen Abschluss.
        foreach (var job in _store.GetByState(JobStates.Spooled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = "Spoolerauftrag nach Neustart nicht mehr nachweisbar – bitte am Drucker prüfen";
            try
            {
                var route = JsonSerializer.Deserialize<RouteConfig>(job.RouteJson, ProtocolJson.Options)
                    ?? throw new InvalidDataException("Missing recovery route");
                var state = await _spooler.GetJobStateAsync(route.LocalQueue, job.SpoolRef!, cancellationToken);
                if (state.Kind == SpoolJobStateKind.Deleting)
                    _store.Update(job with { SpoolDeleting = true, UpdatedAt = _clock.UtcNow });
                if (state.Kind is not (SpoolJobStateKind.Gone or SpoolJobStateKind.Deleted)) continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                error = "Spoolerstatus beim Neustart nicht feststellbar – bitte am Drucker prüfen";
                _logger.LogWarning("Recovery fuer Job {JobId} nicht moeglich ({ErrorType}).", job.JobId, ex.GetType().Name);
            }
            var uncertain = job with { State = JobStates.Uncertain, Error = error, UpdatedAt = _clock.UtcNow };
            var status = new JobStatusMessage(job.JobId, job.LeaseToken, "uncertain", "spooled", job.SpoolRef, error);
            _store.UpdateWithOutbox(uncertain, "job_status", JsonSerializer.Serialize(status, ProtocolJson.Options), _clock.UtcNow);
        }
        foreach (var job in _store.GetByState(JobStates.Submitting))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpoolJobInfo? found = null;
            var error = "Übergabe beim Neustart unterbrochen – bitte am Drucker prüfen";
            try
            {
                var route = JsonSerializer.Deserialize<RouteConfig>(job.RouteJson, ProtocolJson.Options)
                    ?? throw new InvalidDataException("Missing recovery route");
                found = await _spooler.FindJobByDocumentNameAsync(route.LocalQueue, job.DocumentName, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Ein defekter Spooler/Queue blockiert weder Cloud-Reconnect noch
                // die Wiederherstellung anderer Jobs. Keine Exception-Nutzdaten loggen.
                error = "Spoolerstatus beim Neustart nicht feststellbar – bitte am Drucker prüfen";
                _logger.LogWarning("Recovery fuer Job {JobId} nicht moeglich ({ErrorType}).", job.JobId, ex.GetType().Name);
            }
            if (found is not null) _store.Update(job with { State = JobStates.Spooled, SpoolRef = found.SpoolRef, UpdatedAt = _clock.UtcNow });
            else
            {
                var uncertain = job with { State = JobStates.Uncertain, Error = error, UpdatedAt = _clock.UtcNow };
                var status = new JobStatusMessage(job.JobId, job.LeaseToken, "uncertain", "submitting", null, error);
                _store.UpdateWithOutbox(uncertain, "job_status", JsonSerializer.Serialize(status, ProtocolJson.Options), _clock.UtcNow);
            }
        }
        return _store.GetByState(JobStates.Submitting, JobStates.Spooled, JobStates.Uncertain)
            .Select(j => new OpenJob(j.JobId, j.LeaseToken, j.State)).ToArray();
    }
}
