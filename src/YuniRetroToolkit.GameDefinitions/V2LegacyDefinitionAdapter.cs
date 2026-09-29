using System.Collections.ObjectModel;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.GameDefinitions;

public static class V2LegacyDefinitionAdapter
{
    public static V2FoundationDocument Adapt(GameDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var groupId = "legacy.fields";
        var slots = definition.SaveFormats.SelectMany(format => format.Slots).Select((slot, index) => new V2SlotModel(
            slot.Id,
            index,
            "manual",
            V2SlotState.Unknown,
            $"slot.{slot.Id}",
            definition.ChecksumRules.Select(rule => rule.Id).ToArray(),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["legacy_save_format"] = definition.SaveFormats.Single(format => format.Slots.Contains(slot)).Id
            }))).ToArray();

        var addresses = definition.Fields.Select(field =>
        {
            var location = definition.SaveLocations.Single(item => item.Id == field.LocationRefs[0]);
            return new V2LogicalAddress($"address.{field.Id}", definition.DefinitionId, "entry.raw", location.SlotId, "region.save", null, field.Id);
        }).ToArray();
        var fields = definition.Fields.Select(field =>
        {
            var location = definition.SaveLocations.Single(item => item.Id == field.LocationRefs[0]);
            var codec = location.WidthBytes switch
            {
                1 => "codec.u8",
                2 when location.LittleEndian => "codec.u16le",
                2 => "codec.u16be",
                4 when location.LittleEndian => "codec.u32le",
                4 => "codec.u32be",
                _ => null
            };
            return new V2FieldModel(field.Id, "integer", [$"address.{field.Id}"],
                new(field.ValueSpec.SafeMinimum, field.ValueSpec.SafeMaximum, field.AllowedValues ?? []), codec,
                definition.ChecksumRules.Select(rule => rule.Id).ToArray(), "read-write", field.VerificationStatus, groupId);
        }).ToArray();
        var data = new V2GenericDataModel([new(groupId, fields.Select(field => field.Id).ToArray())], fields, [], [], [], [], []);
        var logical = V2ModelValidator.Validate(new(definition.DefinitionId, addresses, slots, [], data));
        var resources = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["field_count"] = fields.Length,
            ["record_count"] = 0,
            ["array_count"] = 0,
            ["table_count"] = 0,
            ["slot_count"] = slots.Length,
            ["reference_count"] = 0,
            ["dependency_edge_count"] = 0
        });
        return new("game_definition", definition.DefinitionId, ParseVersion(definition.DefinitionVersion), new(2, 0, 0), new(2, 0, 0), new(2, 0, 0),
            [new("core.change-set", new(1, 0, 0)), new("core.validation", new(1, 0, 0)), new("storage.raw", new(1, 0, 0)), new("storage.multi-slot", new(1, 0, 0))],
            [], [], definition.Platform, "legacy.raw", logical,
            new("legacy-v1-adapter", [], "unverified-by-adapter", "preserved", "preserved"),
            new("legacy-compatibility", "unsupported", definition.DefinitionId, null), resources, true);
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : throw new InvalidDataException("Legacy definition version is not compatible with the V2 bridge.");
}
