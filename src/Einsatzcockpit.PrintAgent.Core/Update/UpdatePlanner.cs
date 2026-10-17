using Einsatzcockpit.PrintAgent.Core.Jobs;

namespace Einsatzcockpit.PrintAgent.Core.Update;

public sealed record UpdatePlan(bool Install, string State, string Message);

public static class UpdatePlanner
{
    public static UpdatePlan Plan(bool autoUpdate, string currentVersion, string availableVersion,
        IReadOnlyCollection<StoredJob> jobs, IReadOnlyCollection<string>? failedVersions = null)
    {
        if (!autoUpdate)
        {
            return new UpdatePlan(false, "up_to_date", "Automatische Updates sind deaktiviert.");
        }

        if (!CalVerVersion.TryParse(currentVersion, out var current) || current.IsDevelopment)
        {
            return new UpdatePlan(false, "up_to_date", "Entwicklungsversion wird nicht automatisch aktualisiert.");
        }

        if (!CalVerVersion.TryParse(availableVersion, out var available) || available.CompareTo(current) <= 0)
        {
            return new UpdatePlan(false, "up_to_date", "Bereits auf dem neuesten Stand.");
        }

        if (failedVersions?.Contains(availableVersion, StringComparer.OrdinalIgnoreCase) == true)
        {
            return new UpdatePlan(false, "failed", "Diese Updateversion ist bereits fehlgeschlagen.");
        }

        var active = jobs.FirstOrDefault(job => job.State is JobStates.Granted or JobStates.Fetching
            or JobStates.IntentPending or JobStates.Submitting or JobStates.Spooled);
        return active is null
            ? new UpdatePlan(true, "installing", "Update wird installiert.")
            : new UpdatePlan(false, "available", "Update wegen laufender Druckübergabe verschoben.");
    }
}
