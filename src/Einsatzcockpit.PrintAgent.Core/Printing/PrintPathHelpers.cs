using System.Text;

namespace Einsatzcockpit.PrintAgent.Core.Printing;

public static class PrintPathHelpers
{
    public static IReadOnlyList<string> ExpandCommandArguments(IEnumerable<string> arguments, IReadOnlyDictionary<string, string> values) =>
        arguments.Select(argument => Replace(argument, values)).ToArray();

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct();
        var invalidSet = new HashSet<char>(invalid);
        var result = new StringBuilder(name.Length);
        foreach (var character in name)
            result.Append(invalidSet.Contains(character) || char.IsControl(character) ? '_' : character);
        return result.ToString().Trim().Trim('.').TrimEnd(' ') is { Length: > 0 } clean ? clean : "document";
    }

    private static string Replace(string argument, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            argument = argument.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        return argument;
    }
}
