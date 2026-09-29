using System.Text.Json;

namespace YuniRetroToolkit.GameDefinitions;

public enum SchemaDocumentKind
{
    GameDefinition,
    PackManifest,
    PackSignature
}

public sealed record SchemaDocumentValidationResult(
    bool SyntaxValid,
    bool SchemaValid,
    bool SemanticValid,
    IReadOnlyList<string> Problems)
{
    public bool Accepted => SyntaxValid && SchemaValid && SemanticValid && Problems.Count == 0;
}

public sealed class SchemaDocumentValidator : IDisposable
{
    public const string ValidatorVersion = "yuni-schema-validator-1.2.0";
    private readonly Dictionary<SchemaDocumentKind, JsonSchemaSubsetValidator> validators;

    public SchemaDocumentValidator(string schemaDirectory)
    {
        var root = Path.GetFullPath(schemaDirectory);
        validators = new()
        {
            [SchemaDocumentKind.GameDefinition] = Load(root, "game-definition-v1.schema.json"),
            [SchemaDocumentKind.PackManifest] = Load(root, "game-definition-pack-manifest-v1.schema.json"),
            [SchemaDocumentKind.PackSignature] = Load(root, "game-definition-pack-signature-v1.schema.json")
        };
    }

    public SchemaDocumentValidationResult Validate(SchemaDocumentKind kind, ReadOnlyMemory<byte> utf8, TimeSpan? timeout = null)
    {
        if (!validators.TryGetValue(kind, out var validator)) return new(false, false, false, ["Unsupported document kind."]);
        JsonDocument document;
        try
        {
            document = StrictJson.Parse(utf8);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            return new(false, false, false, [SafeDiagnostic(ex)]);
        }

        using (document)
        {
            IReadOnlyList<SchemaProblem> schemaProblems;
            try
            {
                schemaProblems = validator.Validate(document.RootElement, timeout ?? JsonSchemaSubsetValidator.MaximumValidationDuration);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException or InvalidOperationException)
            {
                return new(true, false, false, [SafeDiagnostic(ex)]);
            }
            if (schemaProblems.Count > 0)
                return new(true, false, false, schemaProblems.Select(problem => $"{problem.Path}: {problem.Message}").ToArray());

            try
            {
                var semantic = kind switch
                {
                    SchemaDocumentKind.GameDefinition => ValidateGameDefinition(document.RootElement),
                    SchemaDocumentKind.PackManifest => ValidateManifest(document.RootElement),
                    SchemaDocumentKind.PackSignature => [],
                    _ => ["Unsupported document kind."]
                };
                return new(true, true, semantic.Count == 0, semantic);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException or InvalidOperationException)
            {
                return new(true, true, false, [SafeDiagnostic(ex)]);
            }
        }
    }

    private static IReadOnlyList<string> ValidateGameDefinition(JsonElement root)
    {
        var definition = GameDefinitionRegistry.Parse(root);
        return GameDefinitionRegistry.ValidateSemantics(definition);
    }

    private static IReadOnlyList<string> ValidateManifest(JsonElement root)
    {
        var problems = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in root.GetProperty("contents").EnumerateArray())
        {
            if (!paths.Add(content.GetProperty("path").GetString()!)) problems.Add("Duplicate or case-conflicting content path.");
            var identity = content.GetProperty("definitionId").GetString() + "@" + content.GetProperty("definitionVersion").GetString();
            if (!definitions.Add(identity)) problems.Add("Duplicate definition/version in manifest.");
        }

        if (root.TryGetProperty("signing", out var signing) &&
            root.GetProperty("publisher").TryGetProperty("keyId", out var publisherKey) &&
            !string.Equals(signing.GetProperty("keyId").GetString(), publisherKey.GetString(), StringComparison.Ordinal))
            problems.Add("Publisher and signing key IDs conflict.");
        return problems;
    }

    private static JsonSchemaSubsetValidator Load(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Required schema is missing.", path);
        return new(File.ReadAllBytes(path));
    }

    private static string SafeDiagnostic(Exception exception)
    {
        var text = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 512 ? text : text[..512];
    }

    public void Dispose()
    {
        foreach (var validator in validators.Values) validator.Dispose();
    }
}
