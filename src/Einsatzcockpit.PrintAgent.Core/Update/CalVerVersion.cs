namespace Einsatzcockpit.PrintAgent.Core.Update;

public readonly record struct CalVerVersion(int Year, int Month, int Day, int Revision) : IComparable<CalVerVersion>
{
    public bool IsDevelopment => Year == 0 && Month == 0 && Day == 0 && Revision == -1;

    public static bool TryParse(string? value, out CalVerVersion version)
    {
        version = default;
        if (string.Equals(value, "0.0.0-dev", StringComparison.OrdinalIgnoreCase))
        {
            version = new CalVerVersion(0, 0, 0, -1);
            return true;
        }

        var parts = value?.Trim().TrimStart('v').Split('.') ?? [];
        if (parts.Length is < 3 or > 4 || !parts.All(part => int.TryParse(part, out _)))
        {
            return false;
        }

        var revision = parts.Length == 4 ? int.Parse(parts[3]) : 0;
        if (!int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month)
            || !int.TryParse(parts[2], out var day) || year < 1 || month is < 1 or > 12
            || day is < 1 or > 31 || revision < 0)
        {
            return false;
        }

        version = new CalVerVersion(year, month, day, revision);
        return true;
    }

    public int CompareTo(CalVerVersion other)
    {
        return (Year, Month, Day, Revision).CompareTo((other.Year, other.Month, other.Day, other.Revision));
    }

    public override string ToString()
    {
        if (IsDevelopment)
        {
            return "0.0.0-dev";
        }

        return Revision == 0
            ? $"{Year:D4}.{Month:D2}.{Day:D2}"
            : $"{Year:D4}.{Month:D2}.{Day:D2}.{Revision}";
    }
}
