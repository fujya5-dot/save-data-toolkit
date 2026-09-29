using System.Collections.ObjectModel;
using System.Text.Json;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.GameDefinitions;

public sealed class V2SchemaRuntime(V2CapabilityRegistry capabilities, Version coreVersion)
{
    private static readonly Version SupportedSchema = new(2, 0, 0);
    private static readonly string[] RequiredRootFields =
    [
        "document_type", "definition_id", "definition_version", "pack_schema_version", "ui_schema_version",
        "min_core_version", "required_capabilities", "optional_capabilities", "provided_capabilities", "platform",
        "storage", "logical_address", "slots", "codecs", "integrity", "groups", "fields", "records", "arrays",
        "tables", "trees", "matrices", "references", "dependencies", "ui", "trust_metadata", "migration",
        "resource_declarations"
    ];

    public V2FoundationDocument Parse(ReadOnlyMemory<byte> utf8)
    {
        using var json = StrictJson.Parse(utf8);
        var root = RequireObject(json.RootElement, "root");
        EnsureProperties(root, RequiredRootFields, [.. RequiredRootFields, "extensions"]);

        var documentType = RequiredString(root, "document_type");
        if (documentType is not ("platform_definition" or "game_definition" or "ui_definition" or "local_overlay"))
            throw new InvalidDataException("Unsupported document_type.");
        var definitionId = RequiredString(root, "definition_id");
        var definitionVersion = RequiredVersion(root, "definition_version");
        var packSchema = RequiredVersion(root, "pack_schema_version");
        var uiSchema = RequiredVersion(root, "ui_schema_version");
        var minimumCore = RequiredVersion(root, "min_core_version");
        if (packSchema != SupportedSchema || uiSchema != SupportedSchema) throw new NotSupportedException("Future or unsupported V2 schema version.");
        if (minimumCore > coreVersion) throw new NotSupportedException("The definition requires a newer Core version.");

        var required = ParseCapabilities(root.GetProperty("required_capabilities"), "required_capabilities");
        var optional = ParseCapabilities(root.GetProperty("optional_capabilities"), "optional_capabilities");
        var provided = ParseCapabilities(root.GetProperty("provided_capabilities"), "provided_capabilities");
        _ = capabilities.Resolve(required, optional);
        foreach (var reference in provided)
            if (!capabilities.TryGet(reference.Id, out var descriptor) || descriptor.Version.Major != reference.MinimumVersion.Major || descriptor.Version < reference.MinimumVersion)
                throw new NotSupportedException($"Unknown or unsupported provided capability: {reference.Id}@{reference.MinimumVersion}");

        var platform = RequireObject(root.GetProperty("platform"), "platform");
        EnsureProperties(platform, ["id"], ["id"]);
        var storage = RequireObject(root.GetProperty("storage"), "storage");
        EnsureProperties(storage, ["kind"], ["kind"]);

        RequireEmptyArray(root, "codecs");
        RequireEmptyArray(root, "integrity");
        RequireEmptyArray(root, "trees");
        RequireEmptyArray(root, "matrices");
        if (root.TryGetProperty("extensions", out var extensions) && RequireArray(extensions, "extensions").GetArrayLength() != 0)
            throw new NotSupportedException("V2 extensions are not implemented in Foundation Phase 1.");
        var ui = RequireObject(root.GetProperty("ui"), "ui");
        EnsureProperties(ui, [], []);

        var fields = ParseFields(root.GetProperty("fields"));
        var groups = ParseGroups(root.GetProperty("groups"));
        var records = ParseRecords(root.GetProperty("records"));
        var arrays = ParseArrays(root.GetProperty("arrays"));
        var tables = ParseTables(root.GetProperty("tables"));
        var references = ParseReferences(root.GetProperty("references"));
        var dependencies = ParseDependencies(root.GetProperty("dependencies"));
        var addresses = ParseAddresses(root.GetProperty("logical_address"), out var containerId);
        var slots = ParseSlots(root.GetProperty("slots"), out var commonAreas);
        var data = new V2GenericDataModel(groups, fields, records, arrays, tables, references, dependencies);
        var logical = V2ModelValidator.Validate(new(containerId, addresses, slots, commonAreas, data));

        var trust = ParseTrust(root.GetProperty("trust_metadata"));
        var migration = ParseMigration(root.GetProperty("migration"));
        var resources = ParseResources(root.GetProperty("resource_declarations"));
        ValidateDeclarations(resources, logical);
        return new(documentType, definitionId, definitionVersion, packSchema, uiSchema, minimumCore, required, optional,
            provided, RequiredString(platform, "id"), RequiredString(storage, "kind"), logical, trust, migration, resources);
    }

    private static IReadOnlyList<V2CapabilityReference> ParseCapabilities(JsonElement element, string path)
    {
        var array = RequireArray(element, path);
        var result = new List<V2CapabilityReference>();
        foreach (var item in array.EnumerateArray())
        {
            var value = RequireObject(item, path + "[]");
            EnsureProperties(value, ["id", "version"], ["id", "version"]);
            result.Add(new(RequiredString(value, "id"), RequiredVersion(value, "version")));
        }
        if (result.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException($"Duplicate capability reference in {path}.");
        return result;
    }

    private static IReadOnlyList<V2LogicalAddress> ParseAddresses(JsonElement element, out string containerId)
    {
        var value = RequireObject(element, "logical_address");
        EnsureProperties(value, ["root", "bindings"], ["root", "bindings"]);
        containerId = RequiredString(value, "root");
        var bindings = RequireArray(value.GetProperty("bindings"), "logical_address.bindings");
        return bindings.EnumerateArray().Select(item =>
        {
            var binding = RequireObject(item, "logical_address.bindings[]");
            EnsureProperties(binding, ["id", "container_ref"], ["id", "container_ref", "entry_ref", "slot_ref", "region_ref", "record_ref", "field_ref"]);
            return new V2LogicalAddress(RequiredString(binding, "id"), RequiredString(binding, "container_ref"), OptionalString(binding, "entry_ref"),
                OptionalString(binding, "slot_ref"), OptionalString(binding, "region_ref"), OptionalString(binding, "record_ref"), OptionalString(binding, "field_ref"));
        }).ToArray();
    }

    private static IReadOnlyList<V2SlotModel> ParseSlots(JsonElement element, out IReadOnlyList<V2CommonAreaModel> commonAreas)
    {
        var value = RequireObject(element, "slots");
        EnsureProperties(value, ["mode", "items", "common_areas"], ["mode", "items", "common_areas"]);
        var mode = RequiredString(value, "mode");
        if (mode is not ("none" or "multi")) throw new InvalidDataException("Unsupported slot mode.");
        var items = RequireArray(value.GetProperty("items"), "slots.items").EnumerateArray().Select(item =>
        {
            var slot = RequireObject(item, "slots.items[]");
            EnsureProperties(slot, ["id", "display_order", "kind", "state", "root_ref", "integrity_refs", "metadata"],
                ["id", "display_order", "kind", "state", "root_ref", "integrity_refs", "metadata"]);
            if (!Enum.TryParse<V2SlotState>(RequiredString(slot, "state"), true, out var state)) throw new InvalidDataException("Unknown slot state.");
            return new V2SlotModel(RequiredString(slot, "id"), RequiredInt(slot, "display_order"), RequiredString(slot, "kind"), state,
                RequiredString(slot, "root_ref"), ParseStrings(slot.GetProperty("integrity_refs"), "slot.integrity_refs"), ParseStringMap(slot.GetProperty("metadata"), "slot.metadata"));
        }).ToArray();
        if (mode == "none" && items.Length != 0) throw new InvalidDataException("Slot mode none cannot contain slots.");
        commonAreas = RequireArray(value.GetProperty("common_areas"), "slots.common_areas").EnumerateArray().Select(item =>
        {
            var area = RequireObject(item, "slots.common_areas[]");
            EnsureProperties(area, ["id", "region_ref", "metadata"], ["id", "region_ref", "metadata"]);
            return new V2CommonAreaModel(RequiredString(area, "id"), RequiredString(area, "region_ref"), ParseStringMap(area.GetProperty("metadata"), "common.metadata"));
        }).ToArray();
        return items;
    }

    private static IReadOnlyList<V2FieldModel> ParseFields(JsonElement element) => RequireArray(element, "fields").EnumerateArray().Select(item =>
    {
        var field = RequireObject(item, "fields[]");
        EnsureProperties(field,
            ["id", "value_type", "binding_refs", "constraints", "integrity_refs", "access", "verification_status"],
            ["id", "value_type", "binding_refs", "constraints", "codec_ref", "integrity_refs", "access", "verification_status", "group_ref", "bit_flags", "computed"]);
        var constraintsObject = RequireObject(field.GetProperty("constraints"), "field.constraints");
        EnsureProperties(constraintsObject, [], ["minimum", "maximum", "allowed_values", "maximum_length"]);
        var constraints = new V2ValueConstraints(OptionalLong(constraintsObject, "minimum"), OptionalLong(constraintsObject, "maximum"),
            constraintsObject.TryGetProperty("allowed_values", out var allowed) ? RequireArray(allowed, "constraints.allowed_values").EnumerateArray().Select(x => x.GetInt64()).ToArray() : [],
            constraintsObject.TryGetProperty("maximum_length", out var maximumLength) ? maximumLength.GetInt32() : null);
        V2BitFlagsMetadata? bitFlags = null;
        if (field.TryGetProperty("bit_flags", out var bits))
            bitFlags = new(RequireArray(bits, "field.bit_flags").EnumerateArray().Select(bit =>
            {
                var flag = RequireObject(bit, "field.bit_flags[]");
                EnsureProperties(flag, ["id", "bit"], ["id", "bit"]);
                return new V2BitFlag(RequiredString(flag, "id"), RequiredInt(flag, "bit"));
            }).ToArray());
        V2ComputedMetadata? computed = null;
        if (field.TryGetProperty("computed", out var computedElement))
        {
            var computedObject = RequireObject(computedElement, "field.computed");
            EnsureProperties(computedObject, ["expression_id", "dependency_refs"], ["expression_id", "dependency_refs"]);
            computed = new(RequiredString(computedObject, "expression_id"), ParseStrings(computedObject.GetProperty("dependency_refs"), "computed.dependency_refs"));
        }
        return new V2FieldModel(RequiredString(field, "id"), RequiredString(field, "value_type"), ParseStrings(field.GetProperty("binding_refs"), "field.binding_refs"),
            constraints, OptionalString(field, "codec_ref"), ParseStrings(field.GetProperty("integrity_refs"), "field.integrity_refs"), RequiredString(field, "access"),
            RequiredString(field, "verification_status"), OptionalString(field, "group_ref"), bitFlags, computed);
    }).ToArray();

    private static IReadOnlyList<V2GroupModel> ParseGroups(JsonElement element) => RequireArray(element, "groups").EnumerateArray().Select(item =>
    {
        var value = RequireObject(item, "groups[]"); EnsureProperties(value, ["id", "child_refs"], ["id", "child_refs"]);
        return new V2GroupModel(RequiredString(value, "id"), ParseStrings(value.GetProperty("child_refs"), "group.child_refs"));
    }).ToArray();

    private static IReadOnlyList<V2RecordModel> ParseRecords(JsonElement element) => RequireArray(element, "records").EnumerateArray().Select(item =>
    {
        var value = RequireObject(item, "records[]"); EnsureProperties(value, ["id", "field_refs"], ["id", "field_refs"]);
        return new V2RecordModel(RequiredString(value, "id"), ParseStrings(value.GetProperty("field_refs"), "record.field_refs"));
    }).ToArray();

    private static IReadOnlyList<V2ArrayModel> ParseArrays(JsonElement element) => RequireArray(element, "arrays").EnumerateArray().Select(item =>
    {
        var value = RequireObject(item, "arrays[]"); EnsureProperties(value, ["id", "item_ref", "bounds"], ["id", "item_ref", "bounds"]);
        var bounds = ParseBounds(value.GetProperty("bounds"), "array.bounds");
        return new V2ArrayModel(RequiredString(value, "id"), RequiredString(value, "item_ref"), bounds.Minimum, bounds.Maximum);
    }).ToArray();

    private static IReadOnlyList<V2TableModel> ParseTables(JsonElement element) => RequireArray(element, "tables").EnumerateArray().Select(item =>
    {
        var value = RequireObject(item, "tables[]"); EnsureProperties(value, ["id", "row_ref", "bounds"], ["id", "row_ref", "bounds"]);
        var bounds = ParseBounds(value.GetProperty("bounds"), "table.bounds");
        return new V2TableModel(RequiredString(value, "id"), RequiredString(value, "row_ref"), bounds.Minimum, bounds.Maximum);
    }).ToArray();

    private static IReadOnlyList<V2ReferenceModel> ParseReferences(JsonElement element) => RequireArray(element, "references").EnumerateArray().Select(item =>
    {
        var value = RequireObject(item, "references[]");
        EnsureProperties(value, ["id", "source_ref", "target_type", "resolver_capability", "max_steps"], ["id", "source_ref", "target_type", "resolver_capability", "max_steps"]);
        return new V2ReferenceModel(RequiredString(value, "id"), RequiredString(value, "source_ref"), RequiredString(value, "target_type"), RequiredString(value, "resolver_capability"), RequiredInt(value, "max_steps"));
    }).ToArray();

    private static IReadOnlyList<V2DependencyModel> ParseDependencies(JsonElement element) => RequireArray(element, "dependencies").EnumerateArray().Select(item =>
    {
        var value = RequireObject(item, "dependencies[]"); EnsureProperties(value, ["id", "from_refs", "to_ref", "kind", "order"], ["id", "from_refs", "to_ref", "kind", "order"]);
        return new V2DependencyModel(RequiredString(value, "id"), ParseStrings(value.GetProperty("from_refs"), "dependency.from_refs"), RequiredString(value, "to_ref"), RequiredString(value, "kind"), RequiredInt(value, "order"));
    }).ToArray();

    private static V2TrustMetadata ParseTrust(JsonElement element)
    {
        var value = RequireObject(element, "trust_metadata");
        EnsureProperties(value, ["provenance", "evidence_refs", "publisher_claim", "clean_room_status", "legal_status"], ["provenance", "evidence_refs", "publisher_claim", "clean_room_status", "legal_status"]);
        return new(RequiredString(value, "provenance"), ParseStrings(value.GetProperty("evidence_refs"), "trust.evidence_refs"), RequiredString(value, "publisher_claim"),
            RequiredString(value, "clean_room_status"), RequiredString(value, "legal_status"));
    }

    private static V2MigrationMetadata ParseMigration(JsonElement element)
    {
        var value = RequireObject(element, "migration");
        EnsureProperties(value, ["compatibility", "downgrade"], ["compatibility", "downgrade", "predecessor_definition_ref", "migration_id"]);
        return new(RequiredString(value, "compatibility"), RequiredString(value, "downgrade"), OptionalString(value, "predecessor_definition_ref"), OptionalString(value, "migration_id"));
    }

    private static IReadOnlyDictionary<string, int> ParseResources(JsonElement element)
    {
        var value = RequireObject(element, "resource_declarations");
        var allowed = new[] { "field_count", "record_count", "array_count", "table_count", "slot_count", "reference_count", "dependency_edge_count" };
        EnsureProperties(value, allowed, allowed);
        return new ReadOnlyDictionary<string, int>(value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal));
    }

    private static void ValidateDeclarations(IReadOnlyDictionary<string, int> resources, V2LogicalSaveModel model)
    {
        var observed = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["field_count"] = model.Data.Fields.Count,
            ["record_count"] = model.Data.Records.Count,
            ["array_count"] = model.Data.Arrays.Count,
            ["table_count"] = model.Data.Tables.Count,
            ["slot_count"] = model.Slots.Count,
            ["reference_count"] = model.Data.References.Count,
            ["dependency_edge_count"] = model.Data.Dependencies.Sum(item => item.FromRefs.Count)
        };
        if (resources.Any(pair => pair.Value < 0 || pair.Value != observed[pair.Key])) throw new InvalidDataException("Resource declaration does not match the parsed model.");
    }

    private static (int Minimum, int Maximum) ParseBounds(JsonElement element, string path)
    {
        var value = RequireObject(element, path); EnsureProperties(value, ["minimum", "maximum"], ["minimum", "maximum"]);
        return (RequiredInt(value, "minimum"), RequiredInt(value, "maximum"));
    }

    private static IReadOnlyList<string> ParseStrings(JsonElement element, string path) => RequireArray(element, path).EnumerateArray().Select(item => item.GetString() ?? throw new InvalidDataException($"String expected at {path}.")).ToArray();
    private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement element, string path) => new ReadOnlyDictionary<string, string>(RequireObject(element, path).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? throw new InvalidDataException($"String expected at {path}."), StringComparer.Ordinal));
    private static JsonElement RequireArray(JsonElement element, string path) => element.ValueKind == JsonValueKind.Array ? element : throw new InvalidDataException($"Array expected at {path}.");
    private static JsonElement RequireObject(JsonElement element, string path) => element.ValueKind == JsonValueKind.Object ? element : throw new InvalidDataException($"Object expected at {path}.");
    private static string RequiredString(JsonElement element, string name) => element.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new InvalidDataException($"Non-empty string expected at {name}.");
    private static string? OptionalString(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static int RequiredInt(JsonElement element, string name) => element.GetProperty(name).GetInt32();
    private static long? OptionalLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetInt64() : null;
    private static Version RequiredVersion(JsonElement element, string name) => Version.TryParse(RequiredString(element, name), out var version) && version.Build >= 0 && version.Revision < 0 ? version : throw new InvalidDataException($"SemVer 2.0.0 value expected at {name}.");
    private static void RequireEmptyArray(JsonElement root, string name) { if (RequireArray(root.GetProperty(name), name).GetArrayLength() != 0) throw new NotSupportedException($"{name} is not implemented in Foundation Phase 1."); }

    private static void EnsureProperties(JsonElement element, IReadOnlyCollection<string> required, IReadOnlyCollection<string> allowed)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        var missing = required.Where(name => !names.Contains(name, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Missing required properties: " + string.Join(", ", missing));
        var unknown = names.Where(name => !allowed.Contains(name, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0) throw new InvalidDataException("Unknown properties: " + string.Join(", ", unknown));
    }
}
