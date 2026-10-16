using System.Buffers.Binary;
using System.Text;

namespace Einsatzcockpit.PrintAgent.Core.Printing;

public sealed record IppAttribute(byte Tag, string Name, object Value);
public sealed record IppResponse(ushort StatusCode, int RequestId, IReadOnlyDictionary<string, object> Attributes);

/// <summary>Kleiner IPP/1.1-Codec für die vom Agent benötigten Operationen.</summary>
public static class IppMessage
{
    public const ushort PrintJob = 0x0002;
    public const ushort GetJobAttributes = 0x0009;
    public const ushort GetPrinterAttributes = 0x000B;

    public static byte[] Encode(ushort operation, int requestId, IEnumerable<IppAttribute> attributes, byte[]? data = null)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(1); stream.WriteByte(1);
        WriteU16(stream, operation); WriteI32(stream, requestId);
        stream.WriteByte(0x01); // operation attributes
        foreach (var attribute in attributes) WriteAttribute(stream, attribute);
        stream.WriteByte(0x03);
        if (data is not null) stream.Write(data);
        return stream.ToArray();
    }

    public static IppResponse Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("IPP-Antwort ist zu kurz.");
        var status = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var requestId = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var position = 8;
        string? previousName = null;
        while (position < data.Length)
        {
            var tag = data[position++];
            if (tag == 0x03) break;
            if (tag <= 0x0f) { previousName = null; continue; }
            Ensure(data, position, 2); var nameLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]); position += 2;
            Ensure(data, position, nameLength); var name = nameLength == 0 ? previousName : Encoding.UTF8.GetString(data.Slice(position, nameLength)); position += nameLength;
            Ensure(data, position, 2); var valueLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]); position += 2;
            Ensure(data, position, valueLength); var raw = data.Slice(position, valueLength); position += valueLength;
            if (name is null) continue;
            previousName = name;
            values[name] = DecodeValue(tag, raw);
        }
        return new IppResponse(status, requestId, values);
    }

    private static void WriteAttribute(Stream stream, IppAttribute attribute)
    {
        stream.WriteByte(attribute.Tag);
        WriteString(stream, attribute.Name);
        var value = attribute.Value switch { int n => IntBytes(n), bool b => new[] { b ? (byte)1 : (byte)0 }, _ => Encoding.UTF8.GetBytes(Convert.ToString(attribute.Value) ?? "") };
        WriteU16(stream, (ushort)value.Length); stream.Write(value);
    }
    private static object DecodeValue(byte tag, ReadOnlySpan<byte> raw) => tag switch
    {
        0x21 or 0x23 => raw.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(raw) : 0,
        0x22 => raw.Length > 0 && raw[0] != 0,
        _ => Encoding.UTF8.GetString(raw)
    };
    private static void WriteString(Stream stream, string value) { var bytes = Encoding.UTF8.GetBytes(value); WriteU16(stream, (ushort)bytes.Length); stream.Write(bytes); }
    private static void WriteU16(Stream stream, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    private static void WriteI32(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static byte[] IntBytes(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return bytes; }
    private static void Ensure(ReadOnlySpan<byte> source, int position, int length) { if (position + length > source.Length) throw new InvalidDataException("IPP-Attribut ist unvollständig."); }
}
