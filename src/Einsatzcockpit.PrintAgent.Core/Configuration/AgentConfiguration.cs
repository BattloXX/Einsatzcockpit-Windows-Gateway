using System.Text.Json;

namespace Einsatzcockpit.PrintAgent.Core.Configuration;

/// <summary>Lokale, bewusst kleine Konfiguration des Windows-Druckagents.</summary>
public sealed record AgentOptions(
    string? CloudUrl = null,
    string? Proxy = null,
    string LogLevel = "Information",
    IReadOnlyList<string>? AllowedCommands = null,
    string? EdgePath = null,
    int HtmlReadyTimeoutS = 9)
{
    public IReadOnlyList<string> EffectiveAllowedCommands => AllowedCommands ?? [];

    public AgentOptions Normalized() => this with
    {
        AllowedCommands = EffectiveAllowedCommands,
        HtmlReadyTimeoutS = HtmlReadyTimeoutS > 0 ? HtmlReadyTimeoutS : 9
    };
}

public static class AgentConfiguration
{
    public static AgentOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AgentOptions().Normalized();
        }

        var json = File.ReadAllText(path);
        return (JsonSerializer.Deserialize<AgentOptions>(json, JsonOptions) ?? new AgentOptions()).Normalized();
    }

    public static void Save(string path, AgentOptions options)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(options.Normalized(), JsonOptions));
    }

    public static Uri WebSocketUri(string cloudUrl)
    {
        var cloud = new Uri(cloudUrl, UriKind.Absolute);
        var scheme = cloud.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeWss
            : cloud.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                ? Uri.UriSchemeWs
                : throw new ArgumentException("Cloud-URL muss mit http oder https beginnen.", nameof(cloudUrl));
        var builder = new UriBuilder(cloud)
        {
            Scheme = scheme,
            Port = cloud.IsDefaultPort ? -1 : cloud.Port,
            Path = "/ws/print-agent",
            Query = string.Empty
        };
        return builder.Uri;
    }

    public static string SpoolFileName(string jobId, int generation) => $"{jobId}-g{generation}.pdf";

    public static bool HasPdfSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
