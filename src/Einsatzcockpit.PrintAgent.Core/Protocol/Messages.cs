using System.Text.Json;
using System.Text.Json.Serialization;

namespace Einsatzcockpit.PrintAgent.Core.Protocol;

public sealed record Envelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("rid")] string? Rid = null,
    [property: JsonPropertyName("payload")] JsonElement Payload = default);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

public sealed record LeaseGrant([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId, string LeaseToken, int LeaseGeneration, int LeaseExpiresIn,
    string DocumentType, string ArtifactUrl, string RenderKind, PrintOptions? Options, RouteConfig Route);
public sealed record RouteConfig(long RouteId, long PrinterId, string LocalQueue, string RenderMode,
    JsonElement? Options = null, string? PrinterName = null, int? Priority = null);
// Color kommt aus der Cloud als "color" oder "monochrome" (wie beim Linux-Gateway / CUPS).
public sealed record PrintOptions(int? Copies = null, string? Duplex = null, string? Color = null, string? Media = null);
public sealed record IntentResult([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId, bool Ok, string? Reason = null);
public sealed record JobStatusMessage([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId, string LeaseToken, string Status, string Phase,
    string? SpoolRef = null, string? Error = null);
public sealed record OpenJob([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId, string LeaseToken, string LocalState);
public sealed record HelloMessage(string Version, HostInfo HostInfo, IReadOnlyList<OpenJob> OpenJobs);
public sealed record HostInfo(string Host, string Os, string MachineId);
public sealed record LeaseRequest(int Max);
public sealed record LeaseRenew(IReadOnlyList<LeaseReference> Leases);
public sealed record LeaseReference([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId, string LeaseToken);
public sealed record LeaseGrantMessage(IReadOnlyList<LeaseGrant> Jobs);
public sealed record ConfigSync([property: JsonConverter(typeof(ProtocolIdConverter))] string AgentId, string Name, IReadOnlyList<RouteConfig> Routes, AgentIntervals Intervals);
public sealed record AgentIntervals(int HeartbeatS, int ReadinessS, int LeasePollS, int RenewS);
public sealed record CancelJobMessage([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId);
public sealed record AbandonMessage([property: JsonConverter(typeof(ProtocolIdConverter))] string JobId, string? Reason = null);
public sealed record AckMessage(string Id);
public sealed record JobAvailableMessage([property: JsonConverter(typeof(ProtocolIdsConverter))] IReadOnlyList<string> JobIds);
public sealed record LogEvent(string Level, string Message);
public sealed record InventoryMessage(IReadOnlyList<QueueInfo> Queues);
public sealed record QueueInfo(string Queue, string? Driver, string? Port, string? Host, JsonElement? Identity, JsonElement? Capabilities, string? Status);
public sealed record ReadinessMessage(IReadOnlyList<RouteReadiness> Routes);
public sealed record RouteReadiness(long RouteId, bool Ready, string State, string? Detail, JsonElement? Identity, JsonElement? Capabilities = null);
