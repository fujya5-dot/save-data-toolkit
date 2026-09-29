using System.Collections.ObjectModel;

namespace YuniRetroToolkit.Domain;

public sealed record V2CapabilityReference(string Id, Version MinimumVersion);

public sealed record V2LogicalAddress(
    string Id,
    string ContainerRef,
    string? EntryRef,
    string? SlotRef,
    string? RegionRef,
    string? RecordRef,
    string? FieldRef);

public enum V2SlotState
{
    Empty,
    Used,
    Invalid,
    Unknown
}

public sealed record V2SlotModel(
    string Id,
    int DisplayOrder,
    string Kind,
    V2SlotState State,
    string RootRef,
    IReadOnlyList<string> IntegrityRefs,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record V2CommonAreaModel(string Id, string RegionRef, IReadOnlyDictionary<string, string> Metadata);

public sealed record V2ValueConstraints(long? Minimum, long? Maximum, IReadOnlyList<long> AllowedValues, int? MaximumLength = null);
public sealed record V2BitFlag(string Id, int Bit);
public sealed record V2BitFlagsMetadata(IReadOnlyList<V2BitFlag> Flags);
public sealed record V2ComputedMetadata(string ExpressionId, IReadOnlyList<string> DependencyRefs);

public sealed record V2FieldModel(
    string Id,
    string ValueType,
    IReadOnlyList<string> BindingRefs,
    V2ValueConstraints Constraints,
    string? CodecRef,
    IReadOnlyList<string> IntegrityRefs,
    string Access,
    string VerificationStatus,
    string? GroupRef = null,
    V2BitFlagsMetadata? BitFlags = null,
    V2ComputedMetadata? Computed = null);

public sealed record V2GroupModel(string Id, IReadOnlyList<string> ChildRefs);
public sealed record V2RecordModel(string Id, IReadOnlyList<string> FieldRefs);
public sealed record V2ArrayModel(string Id, string ItemRef, int MinimumCount, int MaximumCount);
public sealed record V2TableModel(string Id, string RowRef, int MinimumRows, int MaximumRows);
public sealed record V2ReferenceModel(string Id, string SourceRef, string TargetType, string ResolverCapability, int MaximumSteps);
public sealed record V2DependencyModel(string Id, IReadOnlyList<string> FromRefs, string ToRef, string Kind, int Order);

public sealed record V2GenericDataModel(
    IReadOnlyList<V2GroupModel> Groups,
    IReadOnlyList<V2FieldModel> Fields,
    IReadOnlyList<V2RecordModel> Records,
    IReadOnlyList<V2ArrayModel> Arrays,
    IReadOnlyList<V2TableModel> Tables,
    IReadOnlyList<V2ReferenceModel> References,
    IReadOnlyList<V2DependencyModel> Dependencies);

public sealed record V2LogicalSaveModel(
    string ContainerId,
    IReadOnlyList<V2LogicalAddress> Addresses,
    IReadOnlyList<V2SlotModel> Slots,
    IReadOnlyList<V2CommonAreaModel> CommonAreas,
    V2GenericDataModel Data)
{
    public V2SlotModel SelectSlot(string slotId) =>
        Slots.SingleOrDefault(slot => StringComparer.Ordinal.Equals(slot.Id, slotId))
        ?? throw new KeyNotFoundException($"Unknown slot: {slotId}");
}

public sealed record V2TrustMetadata(
    string Provenance,
    IReadOnlyList<string> EvidenceRefs,
    string PublisherClaim,
    string CleanRoomStatus,
    string LegalStatus);

public sealed record V2MigrationMetadata(
    string Compatibility,
    string Downgrade,
    string? PredecessorDefinitionRef,
    string? MigrationId);

public sealed record V2FoundationDocument(
    string DocumentType,
    string DefinitionId,
    Version DefinitionVersion,
    Version PackSchemaVersion,
    Version UiSchemaVersion,
    Version MinimumCoreVersion,
    IReadOnlyList<V2CapabilityReference> RequiredCapabilities,
    IReadOnlyList<V2CapabilityReference> OptionalCapabilities,
    IReadOnlyList<V2CapabilityReference> ProvidedCapabilities,
    string PlatformId,
    string StorageKind,
    V2LogicalSaveModel LogicalSave,
    V2TrustMetadata Trust,
    V2MigrationMetadata Migration,
    IReadOnlyDictionary<string, int> ResourceDeclarations,
    bool LegacyCompatibilityBridge = false);

public sealed record V2ProposedChange(V2LogicalAddress Target, long OriginalValue, long ProposedValue);

public static class V2ModelValidator
{
    private static readonly HashSet<string> SlotKinds = new(StringComparer.Ordinal) { "manual", "auto", "quick", "special" };
    private static readonly HashSet<string> SlotStates = new(StringComparer.Ordinal) { "empty", "used", "invalid", "unknown" };
    private const int MaximumFields = 4_096;
    private const int MaximumRecords = 4_096;
    private const int MaximumArrayLength = 4_096;
    private const int MaximumTableRows = 4_096;
    private const int MaximumReferences = 8_192;
    private const int MaximumDependencyEdges = 8_192;
    private const int MaximumSlots = 128;

    public static V2LogicalSaveModel Validate(V2LogicalSaveModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        RequireId(model.ContainerId, nameof(model.ContainerId));
        if (model.Data.Fields.Count > MaximumFields || model.Data.Records.Count > MaximumRecords ||
            model.Data.References.Count > MaximumReferences || model.Slots.Count > MaximumSlots)
            throw new InvalidDataException("V2 model exceeds a Phase 1 resource ceiling.");

        Unique(model.Addresses.Select(x => x.Id), "logical address");
        Unique(model.Slots.Select(x => x.Id), "slot");
        Unique(model.CommonAreas.Select(x => x.Id), "common area");
        Unique(model.Data.Groups.Select(x => x.Id), "group");
        Unique(model.Data.Fields.Select(x => x.Id), "field");
        Unique(model.Data.Records.Select(x => x.Id), "record");
        Unique(model.Data.Arrays.Select(x => x.Id), "array");
        Unique(model.Data.Tables.Select(x => x.Id), "table");
        Unique(model.Data.References.Select(x => x.Id), "reference");
        Unique(model.Data.Dependencies.Select(x => x.Id), "dependency");

        var addresses = model.Addresses.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var slots = model.Slots.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var groups = model.Data.Groups.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var fields = model.Data.Fields.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var records = model.Data.Records.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = fields.Concat(records).Concat(model.Data.Arrays.Select(x => x.Id)).Concat(model.Data.Tables.Select(x => x.Id)).ToHashSet(StringComparer.Ordinal);

        foreach (var address in model.Addresses)
        {
            RequireId(address.Id, "logical address");
            if (!StringComparer.Ordinal.Equals(address.ContainerRef, model.ContainerId)) throw new InvalidDataException($"Logical address has the wrong container: {address.Id}");
            if (address.SlotRef is not null && !slots.Contains(address.SlotRef)) throw new InvalidDataException($"Logical address has an unknown slot: {address.Id}");
            if (address.FieldRef is not null && !fields.Contains(address.FieldRef)) throw new InvalidDataException($"Logical address has an unknown field: {address.Id}");
            if (address.RecordRef is not null && !records.Contains(address.RecordRef)) throw new InvalidDataException($"Logical address has an unknown record: {address.Id}");
        }

        foreach (var slot in model.Slots)
        {
            RequireId(slot.Id, "slot");
            if (!SlotKinds.Contains(slot.Kind)) throw new InvalidDataException($"Unknown slot kind: {slot.Kind}");
            if (!SlotStates.Contains(slot.State.ToString().ToLowerInvariant())) throw new InvalidDataException($"Unknown slot state: {slot.State}");
            if (slot.DisplayOrder < 0 || string.IsNullOrWhiteSpace(slot.RootRef)) throw new InvalidDataException($"Invalid slot metadata: {slot.Id}");
        }

        foreach (var common in model.CommonAreas)
            if (!addresses.ContainsKey(common.RegionRef)) throw new InvalidDataException($"Common area has an unknown region: {common.Id}");

        foreach (var field in model.Data.Fields)
        {
            RequireId(field.Id, "field");
            if (field.BindingRefs.Count == 0 || field.BindingRefs.Any(binding => !addresses.ContainsKey(binding))) throw new InvalidDataException($"Field has an unresolved binding: {field.Id}");
            if (field.BindingRefs.Any(binding => !StringComparer.Ordinal.Equals(addresses[binding].FieldRef, field.Id))) throw new InvalidDataException($"Field binding targets a different field: {field.Id}");
            if (field.GroupRef is not null && !groups.Contains(field.GroupRef)) throw new InvalidDataException($"Field has an unresolved group: {field.Id}");
            if (field.Access is not ("read-write" or "read-only")) throw new InvalidDataException($"Invalid field access: {field.Id}");
            if (field.Computed is not null && field.Access != "read-only") throw new InvalidDataException($"Computed field must be read-only: {field.Id}");
            if (field.Constraints.Minimum > field.Constraints.Maximum) throw new InvalidDataException($"Invalid field range: {field.Id}");
            if (field.Constraints.MaximumLength is < 0) throw new InvalidDataException($"Invalid field length: {field.Id}");
            if (field.BitFlags is not null)
            {
                Unique(field.BitFlags.Flags.Select(x => x.Id), "bit flag");
                if (field.BitFlags.Flags.Any(flag => flag.Bit is < 0 or > 63)) throw new InvalidDataException($"Invalid bit flag: {field.Id}");
            }
        }

        foreach (var group in model.Data.Groups)
            if (group.ChildRefs.Any(child => !nodes.Contains(child))) throw new InvalidDataException($"Group has an unresolved child: {group.Id}");
        foreach (var record in model.Data.Records)
            if (record.FieldRefs.Any(field => !fields.Contains(field))) throw new InvalidDataException($"Record has an unresolved field: {record.Id}");
        foreach (var array in model.Data.Arrays)
            if (array.MinimumCount < 0 || array.MaximumCount < array.MinimumCount || array.MaximumCount > MaximumArrayLength || !nodes.Contains(array.ItemRef))
                throw new InvalidDataException($"Invalid array: {array.Id}");
        foreach (var table in model.Data.Tables)
            if (table.MinimumRows < 0 || table.MaximumRows < table.MinimumRows || table.MaximumRows > MaximumTableRows || !records.Contains(table.RowRef))
                throw new InvalidDataException($"Invalid table: {table.Id}");
        foreach (var reference in model.Data.References)
            if (!nodes.Contains(reference.SourceRef) || reference.MaximumSteps is < 1 or > MaximumReferences)
                throw new InvalidDataException($"Invalid reference: {reference.Id}");

        var edges = model.Data.Dependencies.Sum(x => x.FromRefs.Count);
        if (edges > MaximumDependencyEdges) throw new InvalidDataException("Dependency edge limit exceeded.");
        foreach (var dependency in model.Data.Dependencies)
            if (dependency.FromRefs.Count == 0 || dependency.FromRefs.Any(source => !nodes.Contains(source)) || !nodes.Contains(dependency.ToRef))
                throw new InvalidDataException($"Invalid dependency: {dependency.Id}");
        RejectDependencyCycles(model.Data.Dependencies);
        return model;
    }

    private static void RejectDependencyCycles(IReadOnlyList<V2DependencyModel> dependencies)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
            foreach (var source in dependency.FromRefs)
                graph.GetValueOrDefault(source, graph[source] = []).Add(dependency.ToRef);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            if (!visiting.Add(node)) return false;
            if (visited.Contains(node)) { visiting.Remove(node); return true; }
            foreach (var next in graph.GetValueOrDefault(node, [])) if (!Visit(next)) return false;
            visiting.Remove(node);
            visited.Add(node);
            return true;
        }
        if (graph.Keys.Any(node => !Visit(node))) throw new InvalidDataException("Dependency cycle is forbidden.");
    }

    private static void Unique(IEnumerable<string> ids, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            RequireId(id, kind);
            if (!seen.Add(id)) throw new InvalidDataException($"Duplicate {kind} id: {id}");
        }
    }

    private static void RequireId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new InvalidDataException($"Invalid {kind} id.");
    }
}

public static class V2ChangeSetBridge
{
    public static ChangePreview Apply(WorkingCopySession session, V2FoundationDocument foundation, V2ProposedChange proposed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(foundation);
        ArgumentNullException.ThrowIfNull(proposed);
        V2ModelValidator.Validate(foundation.LogicalSave);
        if (proposed.Target.FieldRef is null)
            throw new InvalidDataException("A proposed change must target exactly one field.");
        if (!StringComparer.Ordinal.Equals(foundation.DefinitionId, session.Definition.DefinitionId) ||
            !StringComparer.Ordinal.Equals(proposed.Target.ContainerRef, session.Definition.DefinitionId))
            throw new InvalidDataException("The proposed change targets a different definition.");
        var registered = foundation.LogicalSave.Addresses.SingleOrDefault(address => StringComparer.Ordinal.Equals(address.Id, proposed.Target.Id));
        if (registered is null || registered != proposed.Target) throw new InvalidDataException("The proposed logical address is not registered by the active definition.");
        var modelField = foundation.LogicalSave.Data.Fields.SingleOrDefault(field => StringComparer.Ordinal.Equals(field.Id, proposed.Target.FieldRef));
        if (modelField is null || !modelField.BindingRefs.Contains(proposed.Target.Id, StringComparer.Ordinal) ||
            modelField.Access != "read-write" || modelField.Computed is not null)
            throw new InvalidDataException("The proposed field is not writable.");
        var current = session.ReadField(proposed.Target.FieldRef);
        if (current != proposed.OriginalValue) throw new InvalidDataException("The proposed change is stale.");
        var validation = session.Apply(proposed.Target.FieldRef, proposed.ProposedValue);
        if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Issues.Select(issue => issue.Message)));
        return session.Preview();
    }
}
