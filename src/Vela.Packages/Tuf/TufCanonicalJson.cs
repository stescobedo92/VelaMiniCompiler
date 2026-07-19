using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vela.Packages.Tuf;

internal static class TufCanonicalJson
{
    public static byte[] EncodeSignedPortion(ReadOnlySpan<byte> metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson.ToArray());
        if (!document.RootElement.TryGetProperty("signed", out var signed))
        {
            throw new TufVerificationException("TUF metadata is missing the signed portion.");
        }

        using var stream = new MemoryStream();
        WriteElement(stream, signed);
        return stream.ToArray();
    }

    private static void WriteElement(Stream stream, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(stream, element);
                break;
            case JsonValueKind.Array:
                WriteArray(stream, element);
                break;
            case JsonValueKind.String:
                WriteString(stream, element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                WriteNumber(stream, element);
                break;
            case JsonValueKind.True:
                stream.WriteByte((byte)'t');
                stream.WriteByte((byte)'r');
                stream.WriteByte((byte)'u');
                stream.WriteByte((byte)'e');
                break;
            case JsonValueKind.False:
                stream.WriteByte((byte)'f');
                stream.WriteByte((byte)'a');
                stream.WriteByte((byte)'l');
                stream.WriteByte((byte)'s');
                stream.WriteByte((byte)'e');
                break;
            case JsonValueKind.Null:
                stream.WriteByte((byte)'n');
                stream.WriteByte((byte)'u');
                stream.WriteByte((byte)'l');
                stream.WriteByte((byte)'l');
                break;
            default:
                throw new TufVerificationException("Unsupported JSON value in TUF metadata.");
        }
    }

    private static void WriteObject(Stream stream, JsonElement element)
    {
        stream.WriteByte((byte)'{');
        var first = true;
        foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (!first)
            {
                stream.WriteByte((byte)',');
            }

            first = false;
            WriteString(stream, property.Name);
            stream.WriteByte((byte)':');
            WriteElement(stream, property.Value);
        }

        stream.WriteByte((byte)'}');
    }

    private static void WriteArray(Stream stream, JsonElement element)
    {
        stream.WriteByte((byte)'[');
        var first = true;
        foreach (var item in element.EnumerateArray())
        {
            if (!first)
            {
                stream.WriteByte((byte)',');
            }

            first = false;
            WriteElement(stream, item);
        }

        stream.WriteByte((byte)']');
    }

    private static void WriteString(Stream stream, string value)
    {
        stream.WriteByte((byte)'"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'"');
                    break;
                case '\\':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'\\');
                    break;
                case '\b':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'b');
                    break;
                case '\f':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'f');
                    break;
                case '\n':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'n');
                    break;
                case '\r':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'r');
                    break;
                case '\t':
                    stream.WriteByte((byte)'\\');
                    stream.WriteByte((byte)'t');
                    break;
                default:
                    if (ch < 0x20)
                    {
                        stream.WriteByte((byte)'\\');
                        stream.WriteByte((byte)'u');
                        stream.Write(Encoding.UTF8.GetBytes(((int)ch).ToString("x4", CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        stream.WriteByte((byte)ch);
                    }

                    break;
            }
        }

        stream.WriteByte((byte)'"');
    }

    private static void WriteNumber(Stream stream, JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            stream.Write(Encoding.UTF8.GetBytes(integer.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        throw new TufVerificationException("TUF metadata contains non-integer numeric values.");
    }
}
