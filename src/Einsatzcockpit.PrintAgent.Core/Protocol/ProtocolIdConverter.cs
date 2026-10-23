using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Einsatzcockpit.PrintAgent.Core.Protocol;

// Die Cloud verwendet numerische IDs. Lokale SQLite-Schluessel bleiben Strings.
// Nur ID-Properties konvertieren: Tokens, Spool-Referenzen und andere Strings bleiben unveraendert.
public sealed class ProtocolIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString() ?? throw new JsonException("ID fehlt."),
            _ => throw new JsonException("Ungueltiger ID-Typ.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            writer.WriteNumberValue(number);
        else
            writer.WriteStringValue(value); // Kompatible lokale/testweise Text-IDs.
    }
}

public sealed class ProtocolIdsConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("ID-Liste fehlt.");
        var result = new List<string>();
        var converter = new ProtocolIdConverter();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            result.Add(converter.Read(ref reader, typeof(string), options));
        return result;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> values, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        var converter = new ProtocolIdConverter();
        foreach (var value in values) converter.Write(writer, value, options);
        writer.WriteEndArray();
    }
}
