using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace YuniRetroToolkit.Domain;

public sealed record LocalizedText(string Ja, string En);
public sealed record RomIdentity(string Id, string FileSha256, long FileSize, string HeaderPolicy, string? CanonicalSha256 = null);
public sealed record SaveSlot(string Id, int Offset, int Length);
public sealed record SaveFormat(string Id, IReadOnlyList<int> AllowedSizes, IReadOnlyList<SaveSlot> Slots);
public sealed record SaveLocation(string Id, string SaveFormatId, string SlotId, int Offset, int WidthBytes, bool LittleEndian, uint? BitMask, int Shift, bool Writable);
public sealed record ByteRange(int Offset, int Length);
public sealed record ChecksumRule(string Id, string AlgorithmId, IReadOnlyList<ByteRange> InputRanges, IReadOnlyList<ByteRange> ExcludedRanges, string OutputLocationRef, int Order);
public sealed record IntegerValueSpec(long StorageMinimum, long StorageMaximum, long SafeMinimum, long SafeMaximum);
public sealed record DefinitionField(string Id, LocalizedText Label, LocalizedText Description, IntegerValueSpec ValueSpec, IReadOnlyList<string> LocationRefs, string Visibility, string Confidence, string VerificationStatus, IReadOnlyList<long>? AllowedValues = null);
public sealed record GameDefinition(
    string SchemaVersion,
    string DefinitionId,
    string DefinitionVersion,
    string Platform,
    LocalizedText Title,
    string Region,
    string Revision,
    IReadOnlyList<RomIdentity> RomIdentities,
    IReadOnlyList<SaveFormat> SaveFormats,
    IReadOnlyList<ChecksumRule> ChecksumRules,
    IReadOnlyList<DefinitionField> Fields,
    IReadOnlyList<SaveLocation> SaveLocations,
    IReadOnlyList<string> RequiredCapabilities,
    bool SaveEdit,
    bool InGameTweaks);

public sealed record FileFingerprint(string FullPath, long Length, string Sha256, uint? VolumeSerial = null, ulong? FileIndex = null)
{
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed class OriginalFile
{
    private readonly byte[] bytes;
    public OriginalFile(FileFingerprint fingerprint, ReadOnlySpan<byte> content)
    {
        Fingerprint = fingerprint;
        bytes = content.ToArray();
    }

    public FileFingerprint Fingerprint { get; }
    public ReadOnlyMemory<byte> Snapshot() => bytes.ToArray();
}

public sealed record ValidationIssue(string Code, string Message, bool Blocking, string? SubjectId = null);
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(i => !i.Blocking);
    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationIssue>());
}

public sealed record ChangeEntry(string FieldId, long OriginalValue, long NewValue, DateTimeOffset Timestamp, string ValidationStatus);
public sealed record ByteChange(int Offset, byte OriginalValue, byte NewValue);
public sealed record ChangePreview(IReadOnlyList<ChangeEntry> Fields, IReadOnlyList<ByteChange> Bytes, string ResultSha256);

public sealed class WorkingCopySession
{
    private sealed record Edit(string FieldId, long Before, long After);
    private readonly byte[] original;
    private readonly byte[] working;
    private readonly Stack<Edit> undo = new();
    private readonly Stack<Edit> redo = new();
    private readonly Dictionary<string, long> initialValues;
    private readonly Dictionary<string, DateTimeOffset> editedAt = new(StringComparer.Ordinal);

    public WorkingCopySession(OriginalFile source, GameDefinition definition)
    {
        Original = source;
        Definition = definition;
        original = source.Snapshot().ToArray();
        working = original.ToArray();
        var integrity = ValidateIntegrity();
        if (!integrity.IsValid) throw new InvalidDataException(string.Join(" ", integrity.Issues.Select(i => i.Message)));
        initialValues = definition.Fields.ToDictionary(f => f.Id, f => ReadField(f.Id));
    }

    public OriginalFile Original { get; }
    public GameDefinition Definition { get; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public ReadOnlyMemory<byte> WorkingBytes => working.ToArray();

    public long ReadField(string fieldId)
    {
        var field = GetField(fieldId);
        var location = GetLocation(field.LocationRefs[0]);
        return ReadValue(location);
    }

    public ValidationResult ValidateValue(string fieldId, long value)
    {
        var field = GetField(fieldId);
        var spec = field.ValueSpec;
        if (field.AllowedValues is not null && !field.AllowedValues.Contains(value))
            return new([new("VALUE_NOT_ALLOWED", "この値は定義の確認済み対象に含まれません。", true, fieldId)]);
        if (value < spec.StorageMinimum || value > spec.StorageMaximum)
            return new([new("VALUE_OUTSIDE_STORAGE", "値が格納可能範囲外です。", true, fieldId)]);
        if (value < spec.SafeMinimum || value > spec.SafeMaximum)
            return new([new("VALUE_OUTSIDE_SAFE", "値が安全範囲外です。", true, fieldId)]);
        foreach (var reference in field.LocationRefs)
        {
            var location = GetLocation(reference);
            if (!location.Writable)
                return new([new("LOCATION_READ_ONLY", "読み取り専用の場所は変更できません。", true, fieldId)]);
            var widthMaximum = location.WidthBytes == 4 ? uint.MaxValue : (1UL << (location.WidthBytes * 8)) - 1;
            var maskMaximum = location.BitMask is null ? widthMaximum : location.BitMask.Value >> location.Shift;
            if (value < 0 || (ulong)value > maskMaximum)
                return new([new("VALUE_DOES_NOT_FIT", "値が保存領域に収まりません。", true, fieldId)]);
        }
        return ValidationResult.Valid;
    }

    public ValidationResult Apply(string fieldId, long value)
    {
        var validation = ValidateValue(fieldId, value);
        if (!validation.IsValid) return validation;
        var before = ReadField(fieldId);
        if (before == value) return ValidationResult.Valid;
        ApplyCore(fieldId, value);
        editedAt[fieldId] = DateTimeOffset.UtcNow;
        undo.Push(new(fieldId, before, value));
        redo.Clear();
        return ValidationResult.Valid;
    }

    public bool Undo()
    {
        if (!undo.TryPop(out var edit)) return false;
        ApplyCore(edit.FieldId, edit.Before);
        editedAt[edit.FieldId] = DateTimeOffset.UtcNow;
        redo.Push(edit);
        return true;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out var edit)) return false;
        ApplyCore(edit.FieldId, edit.After);
        editedAt[edit.FieldId] = DateTimeOffset.UtcNow;
        undo.Push(edit);
        return true;
    }

    public ChangePreview Preview()
    {
        var fields = Definition.Fields
            .Select(f => new ChangeEntry(f.Id, initialValues[f.Id], ReadField(f.Id), editedAt.GetValueOrDefault(f.Id, DateTimeOffset.MinValue), "valid"))
            .Where(c => c.OriginalValue != c.NewValue)
            .ToArray();
        var bytes = original.Select((b, i) => new ByteChange(i, b, working[i])).Where(c => c.OriginalValue != c.NewValue).ToArray();
        return new(fields, bytes, FileFingerprint.Hash(working));
    }

    public ValidationResult Validate()
    {
        var issues = new List<ValidationIssue>();
        foreach (var field in Definition.Fields) issues.AddRange(ValidateValue(field.Id, ReadField(field.Id)).Issues);
        issues.AddRange(ValidateIntegrity().Issues);
        return new(issues);
    }

    private ValidationResult ValidateIntegrity()
    {
        var issues = new List<ValidationIssue>();
        foreach (var field in Definition.Fields)
            if (field.LocationRefs.Select(r => ReadValue(GetLocation(r))).Distinct().Skip(1).Any())
                issues.Add(new("WRITE_GROUP_MISMATCH", "同一fieldの保存値が一致しないため編集を拒否します。", true, field.Id));
        foreach (var rule in Definition.ChecksumRules.OrderBy(r => r.Order))
        {
            var output = GetLocation(rule.OutputLocationRef);
            var expected = CalculateChecksum(rule, output);
            if ((ulong)ReadValue(output) != expected) issues.Add(new("CHECKSUM_MISMATCH", "データ整合性を確認できません。対応する正しいセーブデータか確認してください。", true, rule.Id));
        }
        return new(issues);
    }

    private void ApplyCore(string fieldId, long value)
    {
        var field = GetField(fieldId);
        foreach (var reference in field.LocationRefs) WriteValue(GetLocation(reference), (ulong)value);
        RecalculateChecksums();
    }

    private DefinitionField GetField(string id) => Definition.Fields.FirstOrDefault(f => f.Id == id) ?? throw new KeyNotFoundException($"Unknown field: {id}");
    private SaveLocation GetLocation(string id) => Definition.SaveLocations.FirstOrDefault(l => l.Id == id) ?? throw new KeyNotFoundException($"Unknown location: {id}");

    private long ReadValue(SaveLocation location)
    {
        EnsureRange(location.Offset, location.WidthBytes);
        ulong raw = 0;
        for (var i = 0; i < location.WidthBytes; i++)
        {
            var index = location.LittleEndian ? i : location.WidthBytes - 1 - i;
            raw |= (ulong)working[location.Offset + i] << (index * 8);
        }
        if (location.BitMask is not null) raw = (raw & location.BitMask.Value) >> location.Shift;
        return checked((long)raw);
    }

    private void WriteValue(SaveLocation location, ulong value)
    {
        EnsureRange(location.Offset, location.WidthBytes);
        ulong raw = 0;
        for (var i = 0; i < location.WidthBytes; i++)
        {
            var index = location.LittleEndian ? i : location.WidthBytes - 1 - i;
            raw |= (ulong)working[location.Offset + i] << (index * 8);
        }
        raw = location.BitMask is null ? value : (raw & ~location.BitMask.Value) | ((value << location.Shift) & location.BitMask.Value);
        for (var i = 0; i < location.WidthBytes; i++)
        {
            var index = location.LittleEndian ? i : location.WidthBytes - 1 - i;
            working[location.Offset + i] = (byte)(raw >> (index * 8));
        }
    }

    private void RecalculateChecksums()
    {
        foreach (var rule in Definition.ChecksumRules.OrderBy(r => r.Order))
        {
            var output = GetLocation(rule.OutputLocationRef);
            WriteValue(output, CalculateChecksum(rule, output));
        }
    }

    private ulong CalculateChecksum(ChecksumRule rule, SaveLocation output) => rule.AlgorithmId switch
    {
        "sum8" => Sum(rule) & 0xff,
        "sum16-le" or "sum16-be" => Sum(rule) & 0xffff,
        "crc16-ccitt-false" => Crc16(rule),
        "mirror-copy" => ReadRangeValue(rule.InputRanges[0], output.WidthBytes, output.LittleEndian),
        _ => throw new InvalidOperationException($"Unsupported checksum: {rule.AlgorithmId}")
    };

    private ulong Sum(ChecksumRule rule)
    {
        ulong sum = 0;
        foreach (var range in rule.InputRanges)
        {
            EnsureRange(range.Offset, range.Length);
            for (var i = range.Offset; i < range.Offset + range.Length; i++)
                if (!rule.ExcludedRanges.Any(e => i >= e.Offset && i < e.Offset + e.Length)) sum += working[i];
        }
        return sum;
    }

    private ushort Crc16(ChecksumRule rule)
    {
        ushort crc = 0xffff;
        foreach (var range in rule.InputRanges)
        {
            EnsureRange(range.Offset, range.Length);
            for (var i = range.Offset; i < range.Offset + range.Length; i++)
            {
                if (rule.ExcludedRanges.Any(e => i >= e.Offset && i < e.Offset + e.Length)) continue;
                crc ^= (ushort)(working[i] << 8);
                for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }
        return crc;
    }

    private ulong ReadRangeValue(ByteRange range, int width, bool little)
    {
        if (range.Length < width) throw new InvalidOperationException("Mirror range is too short.");
        EnsureRange(range.Offset, width);
        ulong value = 0;
        for (var i = 0; i < width; i++) value |= (ulong)working[range.Offset + i] << ((little ? i : width - i - 1) * 8);
        return value;
    }

    private void EnsureRange(int offset, int length)
    {
        if (offset < 0 || length < 1 || offset > working.Length - length) throw new InvalidDataException("Definition points outside the save file.");
    }
}
