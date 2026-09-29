using System.IO;
using System.Text.Json;

namespace YuniRetroToolkit.App;

public sealed record V2GuideStep(string Id, string Title, string Body, int HeadingLevel, string AutomationRole);
public sealed record V2GuideCategory(string Id, string Title, IReadOnlyList<V2GuideStep> Steps);
public sealed record V2GuidePresentation(string CategoryId, string CategoryTitle, V2GuideStep Step, int StepIndex, int StepCount);

public sealed class V2GuideCatalog
{
    private const int MaximumGuideBytes = 64 * 1024;
    private static readonly string[] RequiredCategories = ["beginner", "features", "advanced"];
    private readonly IReadOnlyList<V2GuideCategory> categories;

    private V2GuideCatalog(IReadOnlyList<V2GuideCategory> categories) => this.categories = categories;

    public IReadOnlyList<V2GuideCategory> Categories => categories;

    public static V2GuideCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length is <= 0 or > MaximumGuideBytes) throw new InvalidDataException("Guide resource is missing or invalid.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(info.FullName), new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("categories", out var source) || source.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Guide resource is invalid.");
        var parsed = new List<V2GuideCategory>();
        foreach (var category in source.EnumerateArray())
        {
            var id = RequiredText(category, "id");
            var title = RequiredText(category, "title");
            if (!category.TryGetProperty("steps", out var stepsSource) || stepsSource.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Guide steps are invalid.");
            var steps = stepsSource.EnumerateArray().Select(step => new V2GuideStep(
                RequiredText(step, "id"), RequiredText(step, "title"), RequiredText(step, "body"), 2, "document")).ToArray();
            if (steps.Length == 0 || steps.Length > 32) throw new InvalidDataException("Guide steps are invalid.");
            parsed.Add(new(id, title, steps));
        }
        if (parsed.Count != RequiredCategories.Length || parsed.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Count ||
            RequiredCategories.Any(required => parsed.All(item => item.Id != required)))
            throw new InvalidDataException("Guide categories are incomplete.");
        return new(parsed);
    }

    public V2GuidePresentation Navigate(string categoryId, int stepIndex)
    {
        var category = categories.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Id, categoryId))
            ?? throw new KeyNotFoundException("Unknown guide category.");
        if (stepIndex < 0 || stepIndex >= category.Steps.Count) throw new ArgumentOutOfRangeException(nameof(stepIndex));
        return new(category.Id, category.Title, category.Steps[stepIndex], stepIndex, category.Steps.Count);
    }

    private static string RequiredText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Guide text is invalid.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1_024 || text.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
            throw new InvalidDataException("Guide text is invalid.");
        return text;
    }
}
