using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.App;

public sealed record V2QuickEditShortcut(
    string PackId, string FieldId, string StableIdentity, string? SlotScope,
    string? RecordId, int Order, bool Enabled, string Label);

public sealed record V2QuickEditPresentation(
    V2QuickEditShortcut Shortcut, V2FieldPresentation? Field, bool IsStale,
    bool IsPending, string Status, string AutomationName);

public sealed class V2QuickEditStore
{
    private const int MaximumEntries = 4_096;
    private const int MaximumFileBytes = 512 * 1024;
    private readonly string? path;
    private readonly List<V2QuickEditShortcut> entries = [];

    public V2QuickEditStore(string? path = null)
    {
        this.path = path is null ? null : Path.GetFullPath(path);
        if (this.path is not null && File.Exists(this.path)) Load();
    }

    public IReadOnlyList<V2QuickEditShortcut> Entries => entries.OrderBy(item => item.Order).ToArray();

    public bool Add(V2QuickEditShortcut entry)
    {
        Validate(entry);
        if (entries.Any(item => SameKey(item, entry))) return false;
        if (entries.Count >= MaximumEntries) throw new InvalidDataException("Quick Edit limit exceeded.");
        entries.Add(entry with { Order = entries.Count });
        try { Save(); }
        catch { entries.RemoveAt(entries.Count - 1); throw; }
        return true;
    }

    public bool Remove(string packId, string fieldId, string? slotScope = null, string? recordId = null)
    {
        var index = entries.FindIndex(item => item.PackId == packId && item.FieldId == fieldId &&
            item.SlotScope == slotScope && item.RecordId == recordId);
        if (index < 0) return false;
        var removed = entries[index];
        entries.RemoveAt(index);
        try { Save(); }
        catch { entries.Insert(index, removed); throw; }
        return true;
    }

    private static bool SameKey(V2QuickEditShortcut left, V2QuickEditShortcut right) =>
        left.PackId == right.PackId && left.FieldId == right.FieldId &&
        left.SlotScope == right.SlotScope && left.RecordId == right.RecordId;

    private static void Validate(V2QuickEditShortcut entry)
    {
        static bool Safe(string? text) => text is { Length: > 0 and <= 256 } &&
            !text.Any(character => char.IsControl(character) || char.IsSurrogate(character));
        if (!Safe(entry.PackId) || !Safe(entry.FieldId) || !Safe(entry.Label) ||
            entry.SlotScope is not null && !Safe(entry.SlotScope) ||
            entry.RecordId is not null && !Safe(entry.RecordId) ||
            entry.StableIdentity.Length != 64 || !entry.StableIdentity.All(Uri.IsHexDigit) ||
            entry.Order < 0 || entry.Order >= MaximumEntries)
            throw new InvalidDataException("Quick Edit preference is invalid.");
    }

    private void Load()
    {
        CheckPath();
        var info = new FileInfo(path!);
        if (info.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Quick Edit preference is invalid.");
        var loaded = JsonSerializer.Deserialize<V2QuickEditShortcut[]>(File.ReadAllBytes(path!))
            ?? throw new InvalidDataException("Quick Edit preference is invalid.");
        if (loaded.Length > MaximumEntries) throw new InvalidDataException("Quick Edit preference is invalid.");
        foreach (var entry in loaded)
        {
            Validate(entry);
            if (entries.Any(item => SameKey(item, entry))) throw new InvalidDataException("Duplicate Quick Edit preference.");
            entries.Add(entry);
        }
    }

    private void Save()
    {
        if (path is null) return;
        CheckPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        CheckPath();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Entries);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Quick Edit preference limit exceeded.");
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CheckPath()
    {
        if (path is null) return;
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Quick Edit preference path cannot be a reparse point.");
    }

    public static string StableIdentity(V2LogicalSaveModel model, V2FieldModel field, V2FieldUiMetadata? metadata = null)
    {
        var addresses = field.BindingRefs.Select(id => model.Addresses.Single(address => address.Id == id))
            .OrderBy(address => address.Id, StringComparer.Ordinal).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            field.Id, field.ValueType, field.Access, field.CodecRef, field.IntegrityRefs,
            field.Constraints, field.BitFlags, field.Computed, Addresses = addresses,
            Control = metadata?.Control, Options = metadata?.Options
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
