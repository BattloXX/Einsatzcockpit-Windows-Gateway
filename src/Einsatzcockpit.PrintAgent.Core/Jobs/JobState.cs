namespace Einsatzcockpit.PrintAgent.Core.Jobs;

public static class JobStates
{
    public const string Granted = "granted";
    public const string Fetching = "fetching";
    public const string IntentPending = "intent_pending";
    public const string Submitting = "submitting";
    public const string Spooled = "spooled";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";
    public const string Uncertain = "uncertain";
    public static bool BeforeSubmitting(string state) => state is Granted or Fetching or IntentPending;
    public static bool Regrantable(StoredJob job) =>
        BeforeSubmitting(job.State) || job.State == Abandoned || (job.State == Failed && job.SpoolRef is null && !job.SubmitAttempted);
}

public sealed record StoredJob(string JobId, string LeaseToken, int Generation, string State, string DocumentType,
    string ArtifactUrl, string RenderKind, string? OptionsJson, string RouteJson, string? PdfPath, string? SpoolRef,
    string DocumentName, int Attempts, string? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool CancelRequested = false, bool SpoolDeleting = false)
{
    /// <summary>True, sobald der Job je den Zustand "submitting" erreicht hat (Attempts zaehlt die Uebergaben an den Druckweg).</summary>
    public bool SubmitAttempted => Attempts > 0;
}
public sealed record OutboxItem(string Id, string Type, string PayloadJson, DateTimeOffset CreatedAt, int Attempts);
