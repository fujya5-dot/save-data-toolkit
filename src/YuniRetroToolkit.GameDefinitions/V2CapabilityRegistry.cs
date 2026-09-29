using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.GameDefinitions;

public enum V2CapabilityStatus
{
    Defined,
    Planned,
    Implemented,
    Deprecated,
    Removed
}

public sealed record V2CapabilityDescriptor(string Id, Version Version, V2CapabilityStatus Status, string Meaning);

public sealed record V2CapabilityResolution(
    IReadOnlyList<V2CapabilityDescriptor> Required,
    IReadOnlyList<V2CapabilityReference> MissingOptional);

public sealed class V2CapabilityRegistry
{
    private readonly IReadOnlyDictionary<string, V2CapabilityDescriptor> capabilities;

    public V2CapabilityRegistry(IEnumerable<V2CapabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var materialized = descriptors.ToArray();
        if (materialized.Any(item => !IsCapabilityId(item.Id) || item.Version.Major < 1)) throw new InvalidDataException("Invalid capability descriptor.");
        if (materialized.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != materialized.Length) throw new InvalidDataException("Duplicate capability ID.");
        capabilities = materialized.ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public int Count => capabilities.Count;
    public IReadOnlyCollection<V2CapabilityDescriptor> All => capabilities.Values.ToArray();

    public bool TryGet(string id, out V2CapabilityDescriptor descriptor) => capabilities.TryGetValue(id, out descriptor!);

    public V2CapabilityResolution Resolve(
        IEnumerable<V2CapabilityReference> required,
        IEnumerable<V2CapabilityReference> optional)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(optional);
        var requiredResolved = required.Select(ResolveRequired).ToArray();
        var missingOptional = new List<V2CapabilityReference>();
        foreach (var reference in optional)
        {
            ValidateReference(reference);
            if (!capabilities.TryGetValue(reference.Id, out var descriptor) || !IsAvailable(descriptor, reference.MinimumVersion))
                missingOptional.Add(reference);
        }
        return new(requiredResolved, missingOptional);
    }

    private V2CapabilityDescriptor ResolveRequired(V2CapabilityReference reference)
    {
        ValidateReference(reference);
        if (!capabilities.TryGetValue(reference.Id, out var descriptor)) throw new NotSupportedException($"Unknown required capability: {reference.Id}");
        if (!IsAvailable(descriptor, reference.MinimumVersion)) throw new NotSupportedException($"Required capability is unavailable: {reference.Id}@{reference.MinimumVersion}");
        return descriptor;
    }

    private static bool IsAvailable(V2CapabilityDescriptor descriptor, Version minimum) =>
        descriptor.Status == V2CapabilityStatus.Implemented &&
        descriptor.Version.Major == minimum.Major && descriptor.Version >= minimum;

    private static void ValidateReference(V2CapabilityReference reference)
    {
        if (!IsCapabilityId(reference.Id) || reference.MinimumVersion.Major < 1) throw new InvalidDataException("Invalid capability reference.");
    }

    private static bool IsCapabilityId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        var segments = id.Split('.');
        return segments.Length is 2 or 3 && segments.All(segment => segment.Length > 0 &&
            segment[0] is >= 'a' and <= 'z' &&
            segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));
    }

    public static V2CapabilityRegistry CreateFoundation()
    {
        static V2CapabilityDescriptor I(string id, string meaning) => new(id, new(1, 0, 0), V2CapabilityStatus.Implemented, meaning);
        static V2CapabilityDescriptor D(string id, string meaning) => new(id, new(1, 0, 0), V2CapabilityStatus.Defined, meaning);
        static V2CapabilityDescriptor P(string id, string meaning) => new(id, new(1, 0, 0), V2CapabilityStatus.Planned, meaning);
        return new([
            I("core.change-set", "typed target change collection"),
            D("core.backup", "verified backup-before-write"),
            I("core.validation", "schema/semantic/range validation"),
            D("core.diff", "logical/byte effect preview"),
            D("core.verify", "reopen/decode/postcondition verification"),
            D("core.rollback", "verified transaction rollback"),
            I("storage.raw", "single raw file snapshot"),
            I("storage.multi-slot", "0..N slot plus common region model"),
            D("storage.multi-file", "all-or-none file set transaction"),
            P("storage.memory-card", "entry-based card container"),
            D("codec.u8", "unsigned 8-bit integer"),
            D("codec.u16le", "unsigned 16-bit little-endian"),
            D("codec.u16be", "unsigned 16-bit big-endian"),
            D("codec.u32le", "unsigned 32-bit little-endian"),
            D("codec.u32be", "unsigned 32-bit big-endian"),
            D("codec.bitflags", "bounded named bit flags"),
            D("codec.split-bytes", "audited multi-part integer mapping"),
            D("codec.bcd", "bounded binary-coded decimal"),
            D("integrity.sum16", "parameterized 16-bit sum"),
            D("integrity.crc32", "allowlisted CRC-32 variant"),
            D("integrity.mirrored-block", "validated duplicate block synchronization"),
            P("integrity.generation", "generation/active-copy selection"),
            D("ui.field", "scalar/enum/flags renderer"),
            D("ui.table", "bounded virtualized table"),
            D("ui.inspector", "selected record detail"),
            D("ui.tree", "bounded acyclic hierarchy"),
            D("ui.matrix", "bounded relationship grid"),
            D("ui.binary", "read/proposal binary surface"),
            D("analysis.diff", "immutable snapshot comparison"),
            P("analysis.candidate-scan", "bounded experimental candidate scan"),
            D("runtime.cht-export", "declarative offline CHT export"),
            P("runtime.emulator-adapter", "allowlisted emulator-specific runtime target"),
            D("migration.schema", "explicit dry-run/backup/rollback migration")
        ]);
    }
}
