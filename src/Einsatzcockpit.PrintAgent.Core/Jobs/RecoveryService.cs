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
        foreach (var job in _store.GetByState(JobStates.Submitting))
        {
            var route = JsonSerializer.Deserialize<RouteConfig>(job.RouteJson, ProtocolJson.Options)!;
            var found = await _spooler.FindJobByDocumentNameAsync(route.LocalQueue, job.DocumentName, cancellationToken);
            if (found is not null) _store.Update(job with { State = JobStates.Spooled, SpoolRef = found.SpoolRef, UpdatedAt = _clock.UtcNow });
            else
            {
                const string error = "Übergabe beim Neustart unterbrochen – bitte am Drucker prüfen";
                var uncertain = job with { State = JobStates.Uncertain, Error = error, UpdatedAt = _clock.UtcNow };
                var status = new JobStatusMessage(job.JobId, job.LeaseToken, "uncertain", "submitting", null, error);
                _store.UpdateWithOutbox(uncertain, "job_status", JsonSerializer.Serialize(status, ProtocolJson.Options), _clock.UtcNow);
            }
        }
        return _store.GetByState(JobStates.Submitting, JobStates.Spooled, JobStates.Uncertain)
            .Select(j => new OpenJob(j.JobId, j.LeaseToken, j.State)).ToArray();
    }
}
