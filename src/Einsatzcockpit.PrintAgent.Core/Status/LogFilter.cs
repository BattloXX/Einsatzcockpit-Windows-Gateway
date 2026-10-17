namespace Einsatzcockpit.PrintAgent.Core.Status;

public enum LogLevelKind
{
    Other,
    Warning,
    Error
}

public enum LogFilterMode
{
    All,
    WarningsAndErrors,
    ErrorsOnly
}

public static class LogFilter
{
    public static LogLevelKind GetLevel(string line)
    {
        if (line.Contains("[Critical]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[Error]", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevelKind.Error;
        }

        return line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase)
            ? LogLevelKind.Warning
            : LogLevelKind.Other;
    }

    public static bool Matches(string line, LogFilterMode mode, string? search = null)
    {
        if (!string.IsNullOrWhiteSpace(search)
            && !line.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var level = GetLevel(line);
        return mode switch
        {
            LogFilterMode.WarningsAndErrors => level is LogLevelKind.Warning or LogLevelKind.Error,
            LogFilterMode.ErrorsOnly => level == LogLevelKind.Error,
            _ => true
        };
    }

    public static IReadOnlyList<string> Tail(IEnumerable<string> lines, int count)
    {
        return lines.TakeLast(Math.Max(0, count)).ToArray();
    }
}
