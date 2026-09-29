using System.Text;
using System.Text.Json;

namespace YuniRetroToolkit.GameDefinitions;

public static class StrictJson
{
    public const int MaximumDefinitionBytes = 2 * 1024 * 1024;
    public const int MaximumDepth = 64;
    public const int MaximumTokenCount = 100_000;
    public const int MaximumStringCharacters = 65_536;
    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8, int maximumBytes = MaximumDefinitionBytes)
    {
        if (utf8.Length == 0 || utf8.Length > maximumBytes) throw new InvalidDataException("JSON size is outside the allowed range.");
        if (utf8.Length >= 3 && utf8.Span[0] == 0xEF && utf8.Span[1] == 0xBB && utf8.Span[2] == 0xBF) throw new InvalidDataException("UTF-8 BOM is not allowed.");
        _ = new UTF8Encoding(false, true).GetString(utf8.Span);
        RejectDangerousText(utf8.Span);
        RejectDuplicatesAndNonCanonicalNumbers(utf8.Span);
        return JsonDocument.Parse(utf8, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumDepth });
    }

    private static void RejectDangerousText(ReadOnlySpan<byte> utf8)
    {
        var text = Encoding.UTF8.GetString(utf8);
        foreach (var c in text)
            if (c == '\0' || c is '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069') throw new InvalidDataException("NUL and bidirectional control characters are forbidden.");
    }

    private static void RejectDuplicatesAndNonCanonicalNumbers(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumDepth });
        var propertySets = new Stack<HashSet<string>>();
        var tokens = 0;
        while (reader.Read())
        {
            if (++tokens > MaximumTokenCount) throw new InvalidDataException("JSON token count exceeds the allowed limit.");
            if (reader.TokenType == JsonTokenType.StartObject) propertySets.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) propertySets.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !propertySets.Peek().Add(reader.GetString()!)) throw new InvalidDataException("Duplicate JSON property is forbidden.");
            else if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                var text = reader.GetString()!;
                if (text.Length > MaximumStringCharacters) throw new InvalidDataException("JSON string exceeds the allowed limit.");
                if (ContainsForbiddenControl(text)) throw new InvalidDataException("NUL and bidirectional control characters are forbidden.");
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                var number = Encoding.UTF8.GetString(reader.ValueSpan);
                if (number.Contains('.') || number.Contains('e', StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only canonical integer numbers are allowed.");
            }
        }
    }

    private static bool ContainsForbiddenControl(string text) => text.Any(c => c == '\0' || c is '\u202A' or '\u202B' or '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069');
}
