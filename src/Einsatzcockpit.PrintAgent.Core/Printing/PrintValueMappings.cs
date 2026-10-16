namespace Einsatzcockpit.PrintAgent.Core.Printing;

/// <summary>Gemeinsame, betriebssystemunabhängige Normalisierung von Druckoptionen.</summary>
public static class PrintValueMappings
{
    public static string Duplex(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "long" or "long-edge" => "long-edge",
        "short" or "short-edge" => "short-edge",
        _ => "off"
    };

    /// <summary>Farbe ist Standard; nur "monochrome" (bzw. "grayscale"/"sw") druckt schwarz-weiß.</summary>
    public static bool IsColor(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "monochrome" or "grayscale" or "sw" => false,
        _ => true
    };

    public static string Media(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "A3" => "A3",
        _ => "A4"
    };
}
