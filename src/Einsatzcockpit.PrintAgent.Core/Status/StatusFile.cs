using System.Text.Json;

namespace Einsatzcockpit.PrintAgent.Core.Status;

public sealed class StatusFile(string path) : IStatusSink, IUpdateStatusSink
{
    private UpdateStatus _update = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public async Task WriteAsync(AgentStatusSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, Options);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    public AgentStatusSnapshot? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<AgentStatusSnapshot>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }

    public void SetUpdateStatus(UpdateStatus status)
    {
        _update = status;
    }

    public UpdateStatus UpdateStatus => _update;
}
