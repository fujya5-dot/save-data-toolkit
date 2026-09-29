using System.Text.Json;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.GameDefinitions;

public sealed record DefinitionRejection(string FileName, string Reason);

public sealed class GameDefinitionRegistry(string definitionsDirectory, string schemaPath) : IGameDefinitionRegistry
{
    private static readonly HashSet<string> Capabilities = new(StringComparer.Ordinal)
    {
        "save.read.v1", "save.write.integer.v1", "checksum.sum8.v1", "checksum.sum16.v1", "checksum.crc16.v1", "checksum.mirror.v1"
    };
    private readonly List<GameDefinition> active = [];
    private readonly List<DefinitionRejection> rejected = [];
    private IReadOnlyList<string> additionalDefinitionDirectories = [];
    public IReadOnlyList<GameDefinition> ActiveDefinitions => active;
    public IReadOnlyList<DefinitionRejection> Rejections => rejected;

    public void SetAdditionalDefinitionDirectories(IEnumerable<string> directories)
        => additionalDefinitionDirectories = directories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        active.Clear();
        rejected.Clear();
        var schemaBytes = await File.ReadAllBytesAsync(schemaPath, cancellationToken);
        using var schema = new JsonSchemaSubsetValidator(schemaBytes);
        var directories = new[] { Path.GetFullPath(definitionsDirectory) }.Concat(additionalDefinitionDirectories).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in directories.Where(Directory.Exists).SelectMany(directory => Directory.EnumerateFiles(directory, "*.yrt-game.json", SearchOption.TopDirectoryOnly)).Order(StringComparer.Ordinal))
        {
            try
            {
                if ((new FileInfo(file).Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked definition files are forbidden.");
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                using var json = StrictJson.Parse(bytes);
                var schemaProblems = schema.Validate(json.RootElement);
                if (schemaProblems.Count > 0) throw new InvalidDataException(string.Join("; ", schemaProblems.Take(8).Select(p => p.Path + ": " + p.Message)));
                var definition = Parse(json.RootElement);
                var semantic = ValidateSemantics(definition);
                if (semantic.Count > 0) throw new InvalidDataException(string.Join("; ", semantic));
                if (active.Any(d => d.DefinitionId == definition.DefinitionId && d.DefinitionVersion == definition.DefinitionVersion)) throw new InvalidDataException("Duplicate definition/version.");
                active.Add(definition);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                rejected.Add(new(Path.GetFileName(file), ex.Message));
            }
        }
    }

    public GameDefinition? Detect(string romSha256, long romSize, long saveSize)
    {
        var matches = active.Where(d => d.RomIdentities.Any(r => r.FileSha256 == romSha256 && r.FileSize == romSize) && d.SaveFormats.Any(s => s.AllowedSizes.Contains(checked((int)saveSize)))).ToArray();
        return matches.Length switch { 0 => null, 1 => matches[0], _ => throw new InvalidDataException("複数の Game Definition が同じファイルに一致しました。") };
    }

    internal static GameDefinition Parse(JsonElement root)
    {
        static LocalizedText L(JsonElement e) => new(e.GetProperty("ja").GetString()!, e.GetProperty("en").GetString()!);
        var roms = root.GetProperty("romIdentities").EnumerateArray().Select(e => new RomIdentity(e.GetProperty("id").GetString()!, e.GetProperty("fileSha256").GetString()!, e.GetProperty("fileSize").GetInt64(), e.GetProperty("headerPolicy").GetString()!, e.TryGetProperty("canonicalSha256", out var c) ? c.GetString() : null)).ToArray();
        var formats = root.GetProperty("saveFormats").EnumerateArray().Select(e => new SaveFormat(e.GetProperty("id").GetString()!, e.GetProperty("allowedSizes").EnumerateArray().Select(x => x.GetInt32()).ToArray(), e.GetProperty("slots").EnumerateArray().Select(s => new SaveSlot(s.GetProperty("id").GetString()!, s.GetProperty("offset").GetInt32(), s.GetProperty("length").GetInt32())).ToArray())).ToArray();
        var locations = root.GetProperty("locations").GetProperty("save").EnumerateArray().Select(e => new SaveLocation(e.GetProperty("id").GetString()!, e.GetProperty("saveFormatId").GetString()!, e.GetProperty("slotId").GetString()!, e.GetProperty("offset").GetInt32(), e.GetProperty("widthBytes").GetInt32(), e.GetProperty("endianness").GetString() == "little", e.TryGetProperty("bitMask", out var m) ? m.GetUInt32() : null, e.TryGetProperty("shift", out var s) ? s.GetInt32() : 0, e.GetProperty("access").GetString() == "read-write")).ToArray();
        var checksums = root.GetProperty("checksumRules").EnumerateArray().Select(e => new ChecksumRule(e.GetProperty("id").GetString()!, e.GetProperty("algorithmId").GetString()!, e.GetProperty("inputRanges").EnumerateArray().Select(Range).ToArray(), e.TryGetProperty("excludedRanges", out var x) ? x.EnumerateArray().Select(Range).ToArray() : [], e.GetProperty("outputLocationRef").GetString()!, e.GetProperty("order").GetInt32())).ToArray();
        var memberships = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var rule in root.GetProperty("validationRules").EnumerateArray())
        {
            if (rule.GetProperty("kind").GetString() != "membership" || !rule.GetProperty("blocking").GetBoolean())
                throw new InvalidDataException("Unsupported validation rule; definition cannot activate.");
            var subject = rule.GetProperty("subjectRef").GetString()!;
            var values = rule.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetInt64()).ToList();
            if (memberships.TryGetValue(subject, out var existing)) values = existing.Intersect(values).ToList();
            if (values.Count == 0) throw new InvalidDataException("Empty membership intersection.");
            memberships[subject] = values;
        }
        var fields = new List<DefinitionField>();
        foreach (var group in root.GetProperty("entities").EnumerateObject())
            foreach (var e in group.Value.EnumerateArray())
            {
                var spec = e.GetProperty("valueSpec");
                if (spec.GetProperty("type").GetString() != "integer") continue;
                fields.Add(new(e.GetProperty("id").GetString()!, L(e.GetProperty("labels")), L(e.GetProperty("descriptions")), new(spec.GetProperty("storageMinimum").GetInt64(), spec.GetProperty("storageMaximum").GetInt64(), spec.GetProperty("safeMinimum").GetInt64(), spec.GetProperty("safeMaximum").GetInt64()), e.GetProperty("locationRefs").EnumerateArray().Select(x => x.GetString()!).ToArray(), e.GetProperty("visibility").GetString()!, e.GetProperty("confidence").GetString()!, e.GetProperty("verificationStatus").GetString()!, memberships.GetValueOrDefault(e.GetProperty("id").GetString()!)));
            }
        if (memberships.Keys.Any(id => fields.All(f => f.Id != id))) throw new InvalidDataException("Unresolved membership subject.");
        var caps = root.GetProperty("capabilities");
        return new(root.GetProperty("schemaVersion").GetString()!, root.GetProperty("definitionId").GetString()!, root.GetProperty("definitionVersion").GetString()!, root.GetProperty("platform").GetString()!, L(root.GetProperty("title")), root.GetProperty("region").GetString()!, root.GetProperty("revision").GetString()!, roms, formats, checksums, fields, locations, caps.GetProperty("requiredAppCapabilities").EnumerateArray().Select(x => x.GetString()!).ToArray(), caps.GetProperty("saveEdit").GetBoolean(), caps.GetProperty("inGameTweaks").GetBoolean());

        static ByteRange Range(JsonElement e) => new(e.GetProperty("offset").GetInt32(), e.GetProperty("length").GetInt32());
    }

    internal static IReadOnlyList<string> ValidateSemantics(GameDefinition d)
    {
        var errors = new List<string>();
        if (d.SchemaVersion != "1.0.0" || d.Platform != "sfc") errors.Add("Unsupported schema/platform.");
        if (d.RequiredCapabilities.Any(c => !Capabilities.Contains(c))) errors.Add("Unknown app capability.");
        var officialSyntheticDemo = d.DefinitionId == OfficialDemoPack.DefinitionId && d.Region == "DEMO";
        foreach (var identity in d.RomIdentities)
            if (identity.FileSize < 32768 && (!officialSyntheticDemo || identity.FileSize != 32))
                errors.Add("Small identity files are reserved for the Official Synthetic Demo.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id) { if (!ids.Add(id)) errors.Add($"Duplicate id: {id}"); }
        foreach (var r in d.RomIdentities) Add(r.Id);
        foreach (var f in d.SaveFormats) { Add(f.Id); foreach (var s in f.Slots) Add(s.Id); }
        foreach (var l in d.SaveLocations) Add(l.Id);
        foreach (var c in d.ChecksumRules) Add(c.Id);
        foreach (var f in d.Fields) Add(f.Id);
        foreach (var field in d.Fields)
        {
            if (!(field.ValueSpec.StorageMinimum <= field.ValueSpec.SafeMinimum && field.ValueSpec.SafeMinimum <= field.ValueSpec.SafeMaximum && field.ValueSpec.SafeMaximum <= field.ValueSpec.StorageMaximum)) errors.Add($"Unsafe range ordering: {field.Id}");
            foreach (var reference in field.LocationRefs) if (d.SaveLocations.All(l => l.Id != reference)) errors.Add($"Unresolved location: {reference}");
            if (field.LocationRefs.Any(r => d.SaveLocations.FirstOrDefault(l => l.Id == r)?.Writable == false)) errors.Add($"Read-only location used by editable field: {field.Id}");
        }
        foreach (var location in d.SaveLocations)
        {
            var format = d.SaveFormats.FirstOrDefault(f => f.Id == location.SaveFormatId);
            var slot = format?.Slots.FirstOrDefault(s => s.Id == location.SlotId);
            if (format is null || slot is null) { errors.Add($"Unresolved save format/slot: {location.Id}"); continue; }
            var end = (long)location.Offset + location.WidthBytes;
            if (location.Offset < slot.Offset || end > (long)slot.Offset + slot.Length || format.AllowedSizes.Any(size => end > size)) errors.Add($"Location outside save/slot: {location.Id}");
            if (location.BitMask is not null && location.Shift >= location.WidthBytes * 8) errors.Add($"Invalid bit shift: {location.Id}");
        }
        foreach (var rule in d.ChecksumRules)
        {
            var output = d.SaveLocations.FirstOrDefault(l => l.Id == rule.OutputLocationRef);
            if (output is null || !output.Writable) errors.Add($"Invalid checksum output: {rule.Id}");
            foreach (var range in rule.InputRanges.Concat(rule.ExcludedRanges)) if (d.SaveFormats.SelectMany(f => f.AllowedSizes).Any(size => (long)range.Offset + range.Length > size)) errors.Add($"Checksum range outside save: {rule.Id}");
        }
        return errors;
    }
}
