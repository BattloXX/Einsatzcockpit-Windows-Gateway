namespace Einsatzcockpit.PrintAgent.Core.Status;

public enum TrayIndicator
{
    Green,
    Yellow,
    Red
}

public sealed record TrayStatus(
    TrayIndicator Indicator,
    string Tooltip,
    int ReadyRoutes,
    bool IsConnected,
    IReadOnlyList<string> Events);

public static class TrayStatusEvaluator
{
    public static TrayStatus Evaluate(
        AgentStatusSnapshot? snapshot,
        DateTimeOffset statusWrittenAt,
        DateTimeOffset now,
        AgentStatusSnapshot? previous = null)
    {
        var stale = snapshot is null || now - statusWrittenAt > TimeSpan.FromSeconds(30);
        var connected = !stale && snapshot!.Connected;
        var readyRoutes = snapshot?.Routes.Count(route => route.Ready) ?? 0;
        var warning = snapshot is not null
            && (snapshot.Routes.Any(route => !route.Ready)
                || snapshot.UncertainJobs > 0
                || string.Equals(snapshot.Update.State, "failed", StringComparison.OrdinalIgnoreCase));
        var indicator = !connected
            ? TrayIndicator.Red
            : warning ? TrayIndicator.Yellow : TrayIndicator.Green;
        var state = connected ? "verbunden" : "getrennt";
        var tooltip = TrimTooltip($"Einsatzcockpit Druckagent – {state} · {readyRoutes} Druckziele bereit");
        var events = new List<string>();
        if (previous is not null && previous.Connected && !connected)
        {
            events.Add("Die Verbindung zum Einsatzcockpit wurde getrennt.");
        }

        if (snapshot is not null && snapshot.UncertainJobs > (previous?.UncertainJobs ?? 0))
        {
            events.Add("Neue unklare Druckaufträge – bitte in der Druckhistorie prüfen.");
        }

        if (snapshot is not null
            && previous is not null
            && IsNewerVersion(snapshot.Version, previous.Version))
        {
            events.Add($"Update auf Version {snapshot.Version} wurde erfolgreich installiert.");
        }

        return new TrayStatus(indicator, tooltip, readyRoutes, connected, events);
    }

    public static string TrimTooltip(string text)
    {
        return text.Length <= 127 ? text : text[..127];
    }

    private static bool IsNewerVersion(string current, string previous)
    {
        return Version.TryParse(current.TrimStart('v'), out var currentVersion)
            && Version.TryParse(previous.TrimStart('v'), out var previousVersion)
            && currentVersion > previousVersion;
    }
}
