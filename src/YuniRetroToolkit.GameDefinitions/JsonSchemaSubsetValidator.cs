using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YuniRetroToolkit.GameDefinitions;

public sealed record SchemaProblem(string Path, string Message);

public sealed class JsonSchemaSubsetValidator : IDisposable
{
    public const int MaximumVisitedNodes = 100_000;
    public static readonly TimeSpan MaximumValidationDuration = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "type", "additionalProperties", "required", "properties", "$defs", "$ref",
        "const", "enum", "oneOf", "allOf", "if", "then", "minLength", "maxLength", "pattern", "format", "minItems",
        "maxItems", "uniqueItems", "items", "default", "minimum", "maximum"
    };
    private readonly JsonDocument schema;
    private readonly object validationLock = new();
    private Stopwatch? stopwatch;
    private TimeSpan timeout;
    private int visitedNodes;

    public JsonSchemaSubsetValidator(ReadOnlyMemory<byte> schemaUtf8)
    {
        schema = StrictJson.Parse(schemaUtf8);
        var unsupported = FindUnsupportedKeywords(schema.RootElement);
        if (unsupported.Count > 0) throw new InvalidDataException("Schema uses unsupported keywords: " + string.Join(", ", unsupported.Take(8)));
        ValidateReferences(schema.RootElement);
    }

    public IReadOnlyList<SchemaProblem> Validate(JsonElement instance)
        => Validate(instance, MaximumValidationDuration);

    public IReadOnlyList<SchemaProblem> Validate(JsonElement instance, TimeSpan validationTimeout)
    {
        if (validationTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(validationTimeout));
        lock (validationLock)
        {
            stopwatch = Stopwatch.StartNew();
            timeout = validationTimeout;
            visitedNodes = 0;
            var problems = new List<SchemaProblem>();
            ValidateNode(schema.RootElement, instance, "$", problems);
            return problems;
        }
    }

    private bool ValidateNode(JsonElement rule, JsonElement value, string path, List<SchemaProblem> problems)
    {
        if (++visitedNodes > MaximumVisitedNodes || stopwatch!.Elapsed >= timeout)
            throw new InvalidDataException("Schema validation resource limit exceeded.");
        var start = problems.Count;
        if (rule.TryGetProperty("$ref", out var reference))
        {
            var target = Resolve(reference.GetString() ?? throw new InvalidDataException("Empty schema reference."));
            ValidateNode(target, value, path, problems);
            return problems.Count == start;
        }

        if (rule.TryGetProperty("type", out var type) && !MatchesType(type.GetString()!, value))
        {
            problems.Add(new(path, $"Expected {type.GetString()}."));
            return false;
        }
        if (rule.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) problems.Add(new(path, "Value does not match const."));
        if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, value))) problems.Add(new(path, "Value is not in enum."));

        if (rule.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = 0;
            foreach (var candidate in oneOf.EnumerateArray())
            {
                var scratch = new List<SchemaProblem>();
                ValidateNode(candidate, value, path, scratch);
                if (scratch.Count == 0) matches++;
            }
            if (matches != 1) problems.Add(new(path, $"Expected exactly one oneOf match; got {matches}."));
        }
        if (rule.TryGetProperty("allOf", out var allOf)) foreach (var candidate in allOf.EnumerateArray()) ValidateNode(candidate, value, path, problems);
        if (rule.TryGetProperty("if", out var ifRule))
        {
            var scratch = new List<SchemaProblem>();
            ValidateNode(ifRule, value, path, scratch);
            if (scratch.Count == 0 && rule.TryGetProperty("then", out var thenRule)) ValidateNode(thenRule, value, path, problems);
        }

        if (value.ValueKind == JsonValueKind.Object) ValidateObject(rule, value, path, problems);
        if (value.ValueKind == JsonValueKind.Array) ValidateArray(rule, value, path, problems);
        if (value.ValueKind == JsonValueKind.String) ValidateString(rule, value, path, problems);
        if (value.ValueKind == JsonValueKind.Number) ValidateNumber(rule, value, path, problems);
        return problems.Count == start;
    }

    private void ValidateObject(JsonElement rule, JsonElement value, string path, List<SchemaProblem> problems)
    {
        if (rule.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray().Select(v => v.GetString()!))
                if (!value.TryGetProperty(name, out _)) problems.Add(new(path, $"Missing required property '{name}'."));

        var hasProperties = rule.TryGetProperty("properties", out var properties);
        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertyRule)) ValidateNode(propertyRule, property.Value, path + "." + property.Name, problems);
            else if (rule.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False) problems.Add(new(path + "." + property.Name, "Unknown property."));
        }
    }

    private void ValidateArray(JsonElement rule, JsonElement value, string path, List<SchemaProblem> problems)
    {
        var count = value.GetArrayLength();
        if (rule.TryGetProperty("minItems", out var min) && count < min.GetInt32()) problems.Add(new(path, "Too few items."));
        if (rule.TryGetProperty("maxItems", out var max) && count > max.GetInt32()) problems.Add(new(path, "Too many items."));
        if (rule.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
        {
            var items = value.EnumerateArray().Select(SemanticKey).ToArray();
            if (items.Distinct(StringComparer.Ordinal).Count() != items.Length) problems.Add(new(path, "Array items must be unique."));
        }
        if (rule.TryGetProperty("items", out var itemRule))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) ValidateNode(itemRule, item, $"{path}[{i++}]", problems);
        }
    }

    private static void ValidateString(JsonElement rule, JsonElement value, string path, List<SchemaProblem> problems)
    {
        var text = value.GetString()!;
        if (rule.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32()) problems.Add(new(path, "String is too short."));
        if (rule.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32()) problems.Add(new(path, "String is too long."));
        if (rule.TryGetProperty("pattern", out var pattern))
        {
            try
            {
                if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) problems.Add(new(path, "String does not match pattern."));
            }
            catch (RegexMatchTimeoutException) { problems.Add(new(path, "Pattern validation timed out.")); }
        }
        if (rule.TryGetProperty("format", out var format) && format.GetString() == "date-time" && (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) || !text.Contains('T'))) problems.Add(new(path, "Invalid date-time."));
    }

    private static void ValidateNumber(JsonElement rule, JsonElement value, string path, List<SchemaProblem> problems)
    {
        if (!value.TryGetInt64(out var signed))
        {
            if (!value.TryGetUInt64(out var unsigned) || unsigned > uint.MaxValue) { problems.Add(new(path, "Integer is out of range.")); return; }
            signed = checked((long)unsigned);
        }
        if (rule.TryGetProperty("minimum", out var min) && signed < min.GetInt64()) problems.Add(new(path, "Number is below minimum."));
        if (rule.TryGetProperty("maximum", out var max) && (max.TryGetInt64(out var signedMax) ? signed > signedMax : (ulong)signed > max.GetUInt64())) problems.Add(new(path, "Number is above maximum."));
    }

    private JsonElement Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) throw new InvalidDataException("Only internal schema references are allowed.");
        var current = schema.RootElement;
        foreach (var token in reference[2..].Split('/')) current = current.GetProperty(token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        return current;
    }

    private static string SemanticKey(JsonElement value)
    {
        var output = new StringBuilder();
        Append(value, output);
        return output.ToString();

        static void Append(JsonElement element, StringBuilder target)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    target.Append('{');
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        target.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                        Append(property.Value, target);
                        target.Append(';');
                    }
                    target.Append('}');
                    break;
                case JsonValueKind.Array:
                    target.Append('[');
                    foreach (var item in element.EnumerateArray()) { Append(item, target); target.Append(';'); }
                    target.Append(']');
                    break;
                case JsonValueKind.String: target.Append(JsonSerializer.Serialize(element.GetString())); break;
                case JsonValueKind.Number:
                    target.Append(element.TryGetInt64(out var signed) ? signed.ToString(CultureInfo.InvariantCulture) : element.GetUInt64().ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValueKind.True: target.Append("true"); break;
                case JsonValueKind.False: target.Append("false"); break;
                case JsonValueKind.Null: target.Append("null"); break;
            }
        }
    }

    private static IReadOnlyList<string> FindUnsupportedKeywords(JsonElement root)
    {
        var problems = new List<string>();
        InspectRule(root, "$", problems);
        return problems;

        static void InspectRule(JsonElement rule, string path, List<string> problems)
        {
            if (rule.ValueKind != JsonValueKind.Object) return;
            foreach (var property in rule.EnumerateObject())
            {
                if (!SupportedKeywords.Contains(property.Name)) problems.Add(path + "." + property.Name);
                if (property.Name is "properties" or "$defs")
                {
                    foreach (var child in property.Value.EnumerateObject()) InspectRule(child.Value, path + "." + property.Name + "." + child.Name, problems);
                }
                else if (property.Name is "items" or "if" or "then") InspectRule(property.Value, path + "." + property.Name, problems);
                else if (property.Name is "oneOf" or "allOf")
                {
                    var index = 0;
                    foreach (var child in property.Value.EnumerateArray()) InspectRule(child, $"{path}.{property.Name}[{index++}]", problems);
                }
            }
        }
    }

    private void ValidateReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "$ref") _ = Resolve(property.Value.GetString() ?? throw new InvalidDataException("Empty schema reference."));
                else ValidateReferences(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateReferences(item);
        }
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || value.TryGetUInt64(out _)),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false
    };

    public void Dispose() => schema.Dispose();
}
