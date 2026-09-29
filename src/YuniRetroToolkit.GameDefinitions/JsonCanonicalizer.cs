using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YuniRetroToolkit.GameDefinitions;

public static class JsonCanonicalizer
{
    public static byte[] Canonicalize(ReadOnlyMemory<byte> utf8)
    {
        using var document = StrictJson.Parse(utf8);
        var output = new ArrayBufferWriter<byte>(utf8.Length);
        Write(document.RootElement, output);
        return output.WrittenSpan.ToArray();
    }

    private static void Write(JsonElement element, IBufferWriter<byte> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Byte(output, (byte)'{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) Byte(output, (byte)',');
                    firstProperty = false;
                    String(output, property.Name);
                    Byte(output, (byte)':');
                    Write(property.Value, output);
                }
                Byte(output, (byte)'}');
                break;
            case JsonValueKind.Array:
                Byte(output, (byte)'[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) Byte(output, (byte)',');
                    firstItem = false;
                    Write(item, output);
                }
                Byte(output, (byte)']');
                break;
            case JsonValueKind.String:
                String(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var signed)) Text(output, signed.ToString(CultureInfo.InvariantCulture));
                else if (element.TryGetUInt64(out var unsigned)) Text(output, unsigned.ToString(CultureInfo.InvariantCulture));
                else throw new InvalidDataException("JCS only accepts bounded integers in this contract.");
                break;
            case JsonValueKind.True: Text(output, "true"); break;
            case JsonValueKind.False: Text(output, "false"); break;
            case JsonValueKind.Null: Text(output, "null"); break;
            default: throw new InvalidDataException("Unsupported JSON token in canonical input.");
        }
    }

    private static void String(IBufferWriter<byte> output, string value)
    {
        Byte(output, (byte)'"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': Text(output, "\\\""); continue;
                case '\\': Text(output, "\\\\"); continue;
                case '\b': Text(output, "\\b"); continue;
                case '\t': Text(output, "\\t"); continue;
                case '\n': Text(output, "\\n"); continue;
                case '\f': Text(output, "\\f"); continue;
                case '\r': Text(output, "\\r"); continue;
            }
            if (character < 0x20)
            {
                Text(output, "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                continue;
            }
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) throw new InvalidDataException("Unpaired surrogate is not canonical JSON.");
                var pair = value.AsSpan(index, 2);
                var target = output.GetSpan(4);
                var written = Encoding.UTF8.GetBytes(pair, target);
                output.Advance(written);
                index++;
                continue;
            }
            if (char.IsLowSurrogate(character)) throw new InvalidDataException("Unpaired surrogate is not canonical JSON.");
            var source = value.AsSpan(index, 1);
            var destination = output.GetSpan(3);
            var count = Encoding.UTF8.GetBytes(source, destination);
            output.Advance(count);
        }
        Byte(output, (byte)'"');
    }

    private static void Text(IBufferWriter<byte> output, string value)
    {
        var destination = output.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));
        var count = Encoding.UTF8.GetBytes(value, destination);
        output.Advance(count);
    }

    private static void Byte(IBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
}
