using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Core.Abstractions;

public interface ICloudChannel
{
    Task<LeaseGrant[]> RequestLeasesAsync(int max);
    Task<IntentResult> SendIntentAsync(string jobId, string token);
    Task EnqueueStatusAsync(JobStatusMessage status);
    Task RenewAsync(IReadOnlyList<LeaseReference> leases);
}

public interface IArtifactFetcher
{
    Task<string> FetchAsync(LeaseGrant grant, CancellationToken cancellationToken);
}

public sealed record PrintRequest(string PdfPath, RouteConfig Route, PrintOptions? Options, string DocumentName);
public sealed record SubmitResult(string SpoolRef);
/// <summary>Druckweg hat den Auftrag nachweislich nicht angenommen (nichts gedruckt).</summary>
public sealed class PrintRejectedException(string message, Exception? inner = null) : Exception(message, inner);
public interface IPrintSubmitter
{
    string Name { get; }
    Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken);
}

public enum SpoolJobStateKind { InQueue, Printing, Error, Gone, Deleted }
public sealed record SpoolJobState(SpoolJobStateKind Kind, string? Detail = null, int Flags = 0)
{
    public static SpoolJobState InQueue { get; } = new(SpoolJobStateKind.InQueue);
    public static SpoolJobState Printing { get; } = new(SpoolJobStateKind.Printing);
    public static SpoolJobState Gone { get; } = new(SpoolJobStateKind.Gone);
    public static SpoolJobState Deleted { get; } = new(SpoolJobStateKind.Deleted);
    public static SpoolJobState Error(int flags, string? detail) => new(SpoolJobStateKind.Error, detail, flags);
}
public sealed record SpoolJobInfo(string SpoolRef);
public interface IPrintSpooler
{
    Task<SpoolJobInfo?> FindJobByDocumentNameAsync(string queue, string documentName, CancellationToken cancellationToken);
    Task<SpoolJobState> GetJobStateAsync(string queue, string spoolRef, CancellationToken cancellationToken);
    Task CancelJobAsync(string queue, string spoolRef, CancellationToken cancellationToken);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
