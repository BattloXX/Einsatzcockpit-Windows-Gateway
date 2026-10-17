namespace Einsatzcockpit.PrintAgent.Core.Status;

public sealed record AgentStatusSnapshot(
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    bool Connected,
    DateTimeOffset? LastConnectedAt,
    string? CloudUrl,
    string? AgentName,
    IReadOnlyList<RouteStatus> Routes,
    IReadOnlyDictionary<string, int> JobCounts,
    int OpenJobs,
    int UncertainJobs,
    string? LastError,
    UpdateStatus Update);

public sealed record RouteStatus(
    string? PrinterName,
    string LocalQueue,
    string RenderMode,
    int? Priority,
    bool Ready,
    string State,
    string? Detail);

public sealed record UpdateStatus(
    string State = "idle",
    string? AvailableVersion = null,
    DateTimeOffset? LastCheckAt = null,
    string? Message = null);

public interface IStatusSink
{
    Task WriteAsync(AgentStatusSnapshot snapshot, CancellationToken cancellationToken = default);
}

public interface IUpdateStatusSink
{
    void SetUpdateStatus(UpdateStatus status);
}
