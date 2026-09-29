using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.App;

public enum V2UiMode { Simple, Advanced }
public enum V2UiVisibility { Simple, Advanced, Both }
public enum V2UiControl { Number, Enum, Boolean, Text, BitFlags, Unsupported }

public sealed record V2UiOption(long Value, string Label);
public sealed record V2FieldUiMetadata(
    string FieldRef,
    string Label,
    string Help,
    V2UiControl Control,
    V2UiVisibility Visibility,
    long Step = 1,
    IReadOnlyList<V2UiOption>? Options = null,
    IReadOnlyList<string>? Keywords = null,
    bool FavoriteEligible = true);
public sealed record V2GroupUiMetadata(string GroupRef, string Label, int Order, V2UiVisibility Visibility, IReadOnlyList<string>? Keywords = null);
public sealed record V2RecordUiMetadata(string RecordRef, string Label, IReadOnlyList<string>? Keywords = null);
public sealed record V2TableUiMetadata(
    string TableRef,
    string Label,
    IReadOnlyList<string> RecordRefs,
    IReadOnlyList<string> ColumnFieldRefs,
    IReadOnlyList<string> InspectorFieldRefs,
    V2UiVisibility Visibility);
public sealed record V2UnsupportedUiComponent(string Id, string ComponentType, string Reason, V2UiVisibility Visibility);
public sealed record V2UiDefinition(
    IReadOnlyList<V2GroupUiMetadata> Groups,
    IReadOnlyList<V2FieldUiMetadata> Fields,
    IReadOnlyList<V2TableUiMetadata> Tables,
    IReadOnlyList<V2UnsupportedUiComponent> UnsupportedComponents,
    IReadOnlyList<V2RecordUiMetadata>? Records = null);

public sealed record V2UiValueKey(string? SlotId, string? RecordId, string FieldId);
public sealed record V2PendingUiChange(V2LogicalAddress Target, long OriginalValue, long ProposedValue);
public sealed record V2UiSafetyStatus(
    string PackTrust,
    string Compatibility,
    string Validation,
    string BackupReadiness,
    int PendingChanges,
    string WriteReadiness = "Ready",
    string? BlockingReason = null,
    string? NextAction = null,
    bool SaveChanged = false,
    string? CurrentSlot = null);
public sealed record V2SafetyRailPresentation(
    string Summary,
    bool IsBlocked,
    string WhatHappened,
    string SaveChangeStatus,
    string NextAction,
    string AutomationRole);
public enum V2SearchResultKind { Field, Group, Record, Table }
public sealed record V2SearchResult(V2SearchResultKind Kind, string Id, string Label, string? TableRef, string? RecordRef, bool IsFavorite, string AutomationName);
public sealed record V2SearchPresentation(string Query, IReadOnlyList<V2SearchResult> Results, string? EmptyMessage, string AutomationRole);
public sealed record V2FavoritePresentation(string FieldId, string Label, bool IsVisibleInCurrentMode, string AutomationName, string AutomationRole);

public sealed record V2ModeTogglePresentation(string Label, V2UiMode Mode, string AutomationRole, bool IsPressed);
public sealed record V2SlotOptionPresentation(string Id, string Label, V2SlotState State, bool IsSelected, string AutomationRole, int PendingChanges, string AccessibleState);
public sealed record V2SlotSelectorPresentation(string Label, IReadOnlyList<V2SlotOptionPresentation> Options, string? SelectedSlotId, bool HasCommonData, string AutomationRole, string CurrentSummary, int CommonPendingChanges, string? LastChangeNotice);
public sealed record V2FieldPresentation(
    string FieldId,
    string Label,
    string Help,
    V2UiControl Control,
    long Value,
    long? Minimum,
    long? Maximum,
    long Step,
    IReadOnlyList<V2UiOption> Options,
    V2BitFlagsMetadata? BitFlags,
    bool IsReadOnly,
    string AutomationName,
    string AutomationRole,
    string ErrorMessageId,
    string? ValidationError);
public sealed record V2GroupPresentation(string Id, string Label, int Order, IReadOnlyList<V2FieldPresentation> Fields);
public sealed record V2TableColumnPresentation(string FieldId, string Label);
public sealed record V2TableRowPresentation(string RecordId, IReadOnlyDictionary<string, long> Values, bool IsSelected, string AutomationRole);
public sealed record V2InspectorPresentation(string RecordId, IReadOnlyList<V2FieldPresentation> Fields, string AutomationName);
public sealed record V2TablePresentation(
    string Id,
    string Label,
    IReadOnlyList<V2TableColumnPresentation> Columns,
    IReadOnlyList<V2TableRowPresentation> Rows,
    V2InspectorPresentation? Inspector);
public sealed record V2UnsupportedPresentation(string Id, string ComponentType, string Message, string AutomationRole);
public sealed record V2UiPresentation(
    V2UiMode Mode,
    IReadOnlyList<V2ModeTogglePresentation> ModeToggles,
    V2SlotSelectorPresentation Slots,
    IReadOnlyList<V2GroupPresentation> Groups,
    IReadOnlyList<V2TablePresentation> Tables,
    IReadOnlyList<V2UnsupportedPresentation> Unsupported,
    V2UiSafetyStatus Safety,
    V2SafetyRailPresentation SafetyRail,
    IReadOnlyList<V2FavoritePresentation> Favorites);

public sealed class V2FavoriteStore
{
    private const int MaximumFavorites = 4_096;
    private const int MaximumFileBytes = 64 * 1024;
    private readonly string? path;
    private readonly HashSet<string> fieldIds = new(StringComparer.Ordinal);

    public V2FavoriteStore(string? path = null)
    {
        this.path = path is null ? null : Path.GetFullPath(path);
        if (this.path is not null)
        {
            ValidateStoragePath(this.path);
            if (File.Exists(this.path)) Load();
        }
    }

    public IReadOnlyCollection<string> FieldIds => fieldIds.Order(StringComparer.Ordinal).ToArray();
    public bool Contains(string fieldId) => fieldIds.Contains(fieldId);
    public bool Add(string fieldId)
    {
        ValidateId(fieldId);
        if (fieldIds.Count >= MaximumFavorites && !fieldIds.Contains(fieldId)) throw new InvalidDataException("Favorite limit exceeded.");
        var changed = fieldIds.Add(fieldId);
        if (changed) Save();
        return changed;
    }
    public bool Remove(string fieldId)
    {
        var changed = fieldIds.Remove(fieldId);
        if (changed) Save();
        return changed;
    }

    private void Load()
    {
        var info = new FileInfo(path!);
        if (info.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Favorite preference file is invalid.");
        var values = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(path!)) ?? throw new InvalidDataException("Favorite preference file is invalid.");
        if (values.Length > MaximumFavorites || values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new InvalidDataException("Favorite preference file is invalid.");
        foreach (var value in values) { ValidateId(value); fieldIds.Add(value); }
    }

    private void Save()
    {
        if (path is null) return;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Favorite preference path is invalid.");
        Directory.CreateDirectory(directory);
        ValidateStoragePath(path);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(FieldIds));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => char.IsControl(character)))
            throw new InvalidDataException("Favorite field ID is invalid.");
    }

    private static void ValidateStoragePath(string target)
    {
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Favorite preference path is invalid.");
        if (Directory.Exists(directory) && (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Favorite preference directory cannot be a reparse point.");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Favorite preference file cannot be a reparse point.");
    }
}

public sealed class V2GenericUiSession
{
    private readonly V2FoundationDocument foundation;
    private readonly V2UiDefinition ui;
    private readonly Dictionary<V2UiValueKey, long> values;
    private readonly Dictionary<string, V2PendingUiChange> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> appliedTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> selectedRecords = new(StringComparer.Ordinal);
    private readonly V2FavoriteStore favorites;
    private readonly V2QuickEditStore quickEdits;
    private V2UiSafetyStatus safety;

    public V2GenericUiSession(
        V2FoundationDocument foundation,
        V2UiDefinition ui,
        IReadOnlyDictionary<V2UiValueKey, long> initialValues,
        V2UiSafetyStatus safety,
        V2FavoriteStore? favorites = null,
        V2QuickEditStore? quickEdits = null)
    {
        ArgumentNullException.ThrowIfNull(foundation);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(initialValues);
        this.foundation = foundation;
        this.ui = V2UiDefinitionValidator.Validate(foundation.LogicalSave, ui);
        values = new(initialValues, EqualityComparer<V2UiValueKey>.Default);
        this.safety = safety;
        this.favorites = favorites ?? new();
        this.quickEdits = quickEdits ?? new();
        SelectedSlotId = foundation.LogicalSave.Slots.FirstOrDefault()?.Id;
        foreach (var table in ui.Tables)
            if (table.RecordRefs.Count > 0) selectedRecords[table.TableRef] = table.RecordRefs[0];
        ValidateInitialValues();
    }

    public V2UiMode Mode { get; private set; }
    public string? SelectedSlotId { get; private set; }
    public IReadOnlyCollection<V2PendingUiChange> PendingChanges => pending.Values.ToArray();
    public string? LastSlotChangeNotice { get; private set; }

    public void SetMode(V2UiMode mode) => Mode = mode;

    public void SelectSlot(string slotId)
    {
        var previous = SelectedSlotId;
        var previousPending = previous is null ? 0 : pending.Values.Count(change => change.Target.SlotRef == previous);
        _ = foundation.LogicalSave.SelectSlot(slotId);
        SelectedSlotId = slotId;
        LastSlotChangeNotice = previousPending == 0 ? null : $"{previousPending} pending change(s) remain bound to {previous}.";
    }

    public bool AddFavorite(string fieldId)
    {
        var metadata = ui.Fields.SingleOrDefault(field => field.FieldRef == fieldId) ?? throw new KeyNotFoundException($"Unknown UI field: {fieldId}");
        if (!metadata.FavoriteEligible) throw new InvalidOperationException("This field cannot be favorited.");
        return favorites.Add(fieldId);
    }
    public bool RemoveFavorite(string fieldId) => favorites.Remove(fieldId);

    public bool AddQuickEdit(string fieldId, string? slotScope = null, string? recordId = null)
    {
        if (Mode != V2UiMode.Advanced) throw new InvalidOperationException("Add Quick Edit from Advanced mode.");
        var metadata = ui.Fields.SingleOrDefault(item => item.FieldRef == fieldId)
            ?? throw new KeyNotFoundException("Quick Edit field is unavailable.");
        var field = foundation.LogicalSave.Data.Fields.Single(item => item.Id == fieldId);
        if (field.Access != "read-write" || field.Computed is not null ||
            metadata.Control is V2UiControl.Text or V2UiControl.Unsupported)
            throw new InvalidOperationException("Read-only or unsupported field cannot be added.");
        if (slotScope is not null)
        {
            _ = foundation.LogicalSave.SelectSlot(slotScope);
            if (slotScope != SelectedSlotId) throw new InvalidOperationException("Select the scoped slot before adding Quick Edit.");
        }
        if (recordId is not null && foundation.LogicalSave.Data.Records.All(item => item.Id != recordId))
            throw new InvalidOperationException("Quick Edit record is unavailable.");
        _ = ResolveTarget(field, recordId);
        return quickEdits.Add(new(foundation.DefinitionId, fieldId,
            V2QuickEditStore.StableIdentity(foundation.LogicalSave, field, metadata), slotScope,
            recordId, quickEdits.Entries.Count, true, metadata.Label));
    }

    public bool CanAddQuickEdit(string fieldId)
    {
        if (Mode != V2UiMode.Advanced || quickEdits.Entries.Any(item => item.PackId == foundation.DefinitionId && item.FieldId == fieldId)) return false;
        var metadata = ui.Fields.SingleOrDefault(item => item.FieldRef == fieldId);
        var field = foundation.LogicalSave.Data.Fields.SingleOrDefault(item => item.Id == fieldId);
        if (metadata is null || field is null || field.Access != "read-write" || field.Computed is not null ||
            metadata.Control is V2UiControl.Text or V2UiControl.Unsupported) return false;
        try { _ = ResolveTarget(field, null); return true; }
        catch (InvalidDataException) { return false; }
    }

    public bool RemoveQuickEdit(string fieldId, string? slotScope = null, string? recordId = null) =>
        quickEdits.Remove(foundation.DefinitionId, fieldId, slotScope, recordId);

    public IReadOnlyList<V2QuickEditPresentation> GetQuickEdits()
    {
        var result = new List<V2QuickEditPresentation>();
        foreach (var shortcut in quickEdits.Entries.Where(item => item.PackId == foundation.DefinitionId))
        {
            var field = foundation.LogicalSave.Data.Fields.SingleOrDefault(item => item.Id == shortcut.FieldId);
            var metadata = ui.Fields.SingleOrDefault(item => item.FieldRef == shortcut.FieldId);
            var stale = field is null || metadata is null ||
                field is not null && V2QuickEditStore.StableIdentity(foundation.LogicalSave, field, metadata) != shortcut.StableIdentity;
            V2FieldPresentation? presentation = null;
            if (!stale && shortcut.Enabled && (shortcut.SlotScope is null || shortcut.SlotScope == SelectedSlotId))
            {
                try { presentation = PresentField(metadata!, shortcut.RecordId); }
                catch (InvalidDataException) { presentation = null; }
            }
            var pendingChange = presentation is not null && (pending.Values.Any(item => item.Target.FieldRef == shortcut.FieldId &&
                item.Target.RecordRef == shortcut.RecordId &&
                (item.Target.SlotRef is null || item.Target.SlotRef == SelectedSlotId)) ||
                appliedTargets.Contains(ResolveTarget(field!, shortcut.RecordId).Id));
            var blocked = safety.PackTrust != "Official Signed" || safety.Compatibility != "Compatible" ||
                safety.Validation != "Valid" || safety.BackupReadiness != "Ready" || safety.WriteReadiness != "Ready";
            result.Add(new(shortcut, presentation, stale, pendingChange,
                stale ? "Unavailable: field identity changed or disappeared" :
                presentation is null ? "Unavailable in this slot" : blocked ? "Blocked by safety status" : presentation.IsReadOnly ? "Read only" :
                pendingChange ? "Pending" : "Ready", shortcut.Label + " Quick Edit"));
        }
        return result;
    }

    public V2PendingUiChange ProposeQuickEdit(V2QuickEditShortcut shortcut, long value)
    {
        var item = GetQuickEdits().SingleOrDefault(entry => entry.Shortcut == shortcut);
        if (item?.Field is null || item.IsStale || item.Field.IsReadOnly ||
            safety.PackTrust != "Official Signed" || safety.Compatibility != "Compatible" ||
            safety.Validation != "Valid" || safety.BackupReadiness != "Ready" || safety.WriteReadiness != "Ready")
            throw new InvalidOperationException("Quick Edit is unavailable or blocked by safety checks.");
        return ProposeCore(shortcut.FieldId, value, shortcut.RecordId, true);
    }

    public IReadOnlyList<V2FavoritePresentation> GetFavorites() => favorites.FieldIds
        .Select(id => ui.Fields.SingleOrDefault(field => field.FieldRef == id))
        .Where(metadata => metadata is not null)
        .Select(metadata => new V2FavoritePresentation(metadata!.FieldRef, metadata.Label, IsVisible(metadata.Visibility, Mode), metadata.Label + " favorite", "button"))
        .ToArray();

    public V2SearchPresentation Search(string? query)
    {
        var normalized = query?.Trim() ?? "";
        if (normalized.Length == 0) return new("", [], null, "search");
        var results = new List<V2SearchResult>();
        foreach (var group in ui.Groups.Where(group => IsVisible(group.Visibility, Mode)))
            if (Matches(normalized, group.Label, group.Keywords)) results.Add(new(V2SearchResultKind.Group, group.GroupRef, group.Label, null, null, false, group.Label));
        foreach (var field in ui.Fields.Where(field => IsVisible(field.Visibility, Mode)))
            if (Matches(normalized, field.Label + " " + field.Help, field.Keywords))
                results.Add(new(V2SearchResultKind.Field, field.FieldRef, field.Label, null, null, favorites.Contains(field.FieldRef), field.Label));
        if (Mode == V2UiMode.Advanced)
        {
            foreach (var table in ui.Tables.Where(table => IsVisible(table.Visibility, Mode)))
                if (Matches(normalized, table.Label, null)) results.Add(new(V2SearchResultKind.Table, table.TableRef, table.Label, table.TableRef, null, false, table.Label));
            foreach (var record in ui.Records ?? [])
                if (Matches(normalized, record.Label, record.Keywords))
                {
                    var table = ui.Tables.FirstOrDefault(item => item.RecordRefs.Contains(record.RecordRef, StringComparer.Ordinal));
                    if (table is not null && IsVisible(table.Visibility, Mode)) results.Add(new(V2SearchResultKind.Record, record.RecordRef, record.Label, table.TableRef, record.RecordRef, false, record.Label));
                }
        }
        return new(normalized, results, results.Count == 0 ? "No matching fields or records." : null, "search");
    }

    public void Navigate(V2SearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Kind == V2SearchResultKind.Record && result.TableRef is not null && result.RecordRef is not null)
            SelectRecord(result.TableRef, result.RecordRef);
        else if (result.Kind == V2SearchResultKind.Field && ui.Fields.All(field => field.FieldRef != result.Id))
            throw new KeyNotFoundException("Search target no longer exists.");
    }

    public void SelectRecord(string tableId, string recordId)
    {
        var table = ui.Tables.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.TableRef, tableId))
            ?? throw new KeyNotFoundException($"Unknown table: {tableId}");
        if (!table.RecordRefs.Contains(recordId, StringComparer.Ordinal)) throw new KeyNotFoundException($"Unknown record: {recordId}");
        selectedRecords[tableId] = recordId;
    }

    public string? GetSelectedRecord(string tableId)
    {
        if (ui.Tables.All(table => table.TableRef != tableId)) throw new KeyNotFoundException($"Unknown table: {tableId}");
        return selectedRecords.GetValueOrDefault(tableId);
    }

    public V2PendingUiChange Propose(string fieldId, long value, string? recordId = null)
        => ProposeCore(fieldId, value, recordId, false);

    private V2PendingUiChange ProposeCore(string fieldId, long value, string? recordId, bool quickEdit)
    {
        var metadata = ui.Fields.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.FieldRef, fieldId))
            ?? throw new KeyNotFoundException($"Unknown UI field: {fieldId}");
        if (!quickEdit && !IsVisible(metadata.Visibility, Mode)) throw new InvalidOperationException("The field is not available in the current UI mode.");
        var field = foundation.LogicalSave.Data.Fields.Single(item => StringComparer.Ordinal.Equals(item.Id, fieldId));
        if (field.Access != "read-write" || field.Computed is not null || metadata.Control is V2UiControl.Text or V2UiControl.Unsupported)
            throw new InvalidOperationException("The field is read-only.");
        ValidateValue(field, metadata, value);
        var target = ResolveTarget(field, recordId);
        if (target.SlotRef is not null && foundation.LogicalSave.SelectSlot(target.SlotRef).State == V2SlotState.Empty)
            throw new InvalidOperationException("An empty slot cannot be edited.");
        var key = new V2UiValueKey(target.SlotRef, target.RecordRef, fieldId);
        if (!values.TryGetValue(key, out var original)) throw new InvalidDataException("The UI value has no decoded source value.");
        var change = new V2PendingUiChange(target, original, value);
        if (original == value) pending.Remove(target.Id); else pending[target.Id] = change;
        return change;
    }

    public ChangePreview ApplyPending(WorkingCopySession session, string targetId)
    {
        if (!pending.TryGetValue(targetId, out var change)) throw new KeyNotFoundException($"Unknown pending change: {targetId}");
        var preview = V2ChangeSetBridge.Apply(session, foundation, new(change.Target, change.OriginalValue, change.ProposedValue));
        values[new(change.Target.SlotRef, change.Target.RecordRef, change.Target.FieldRef!)] = change.ProposedValue;
        pending.Remove(targetId);
        if (preview.Fields.Any(field => field.FieldId == change.Target.FieldRef)) appliedTargets.Add(change.Target.Id);
        else appliedTargets.Remove(change.Target.Id);
        safety = safety with { PendingChanges = preview.Fields.Count, SaveChanged = preview.Fields.Count > 0 };
        return preview;
    }

    public void SynchronizeFrom(WorkingCopySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!StringComparer.Ordinal.Equals(session.Definition.DefinitionId, foundation.DefinitionId))
            throw new InvalidDataException("The Working Copy belongs to a different definition.");
        foreach (var field in foundation.LogicalSave.Data.Fields.Where(field => session.Definition.Fields.Any(item => item.Id == field.Id)))
            foreach (var address in field.BindingRefs.Select(id => foundation.LogicalSave.Addresses.Single(item => item.Id == id)).Where(address => address.RecordRef is null))
                values[new(address.SlotRef, null, field.Id)] = session.ReadField(field.Id);
        var validation = session.Validate();
        var preview = session.Preview();
        appliedTargets.Clear();
        foreach (var field in preview.Fields)
            foreach (var address in foundation.LogicalSave.Addresses.Where(address => address.FieldRef == field.FieldId))
                appliedTargets.Add(address.Id);
        safety = safety with { Validation = validation.IsValid ? "Valid" : "Invalid", PendingChanges = preview.Fields.Count, SaveChanged = preview.Fields.Count > 0 };
    }

    public V2UiPresentation Render()
    {
        var tableFields = ui.Tables.SelectMany(table => table.ColumnFieldRefs.Concat(table.InspectorFieldRefs)).ToHashSet(StringComparer.Ordinal);
        var groupPresentations = ui.Groups
            .Where(group => IsVisible(group.Visibility, Mode))
            .OrderBy(group => group.Order)
            .Select(group => new V2GroupPresentation(group.GroupRef, group.Label, group.Order,
                ui.Fields.Where(metadata => IsVisible(metadata.Visibility, Mode))
                    .Where(metadata => !tableFields.Contains(metadata.FieldRef))
                    .Where(metadata => foundation.LogicalSave.Data.Fields.Single(field => field.Id == metadata.FieldRef).GroupRef == group.GroupRef)
                    .Select(metadata => PresentField(metadata, null)).ToArray()))
            .Where(group => group.Fields.Count > 0)
            .ToArray();
        var tables = Mode == V2UiMode.Advanced
            ? ui.Tables.Where(table => IsVisible(table.Visibility, Mode)).Select(PresentTable).ToArray()
            : [];
        var unsupported = ui.UnsupportedComponents.Where(item => IsVisible(item.Visibility, Mode))
            .Select(item => new V2UnsupportedPresentation(item.Id, item.ComponentType, "Unsupported: " + item.Reason, "status"))
            .ToArray();
        var slots = foundation.LogicalSave.Slots.Select(slot =>
        {
            var slotPending = pending.Values.Count(change => StringComparer.Ordinal.Equals(change.Target.SlotRef, slot.Id));
            var state = slot.State == V2SlotState.Empty ? "Empty" : "Used";
            var label = slot.Metadata.GetValueOrDefault("label", slot.Id) + $" — {state}" + (slotPending == 0 ? "" : $" — {slotPending} pending");
            return new V2SlotOptionPresentation(slot.Id, label, slot.State,
                StringComparer.Ordinal.Equals(slot.Id, SelectedSlotId), "option", slotPending, $"{state}; {slotPending} pending changes");
        }).ToArray();
        var commonPending = pending.Values.Count(change => change.Target.SlotRef is null);
        var selectedSlot = slots.SingleOrDefault(slot => slot.IsSelected);
        var status = safety with
        {
            PendingChanges = Math.Max(safety.PendingChanges, pending.Count),
            CurrentSlot = SelectedSlotId
        };
        return new(Mode,
            [new("Simple", V2UiMode.Simple, "button", Mode == V2UiMode.Simple), new("Advanced", V2UiMode.Advanced, "button", Mode == V2UiMode.Advanced)],
            new("Edit save", slots, SelectedSlotId, foundation.LogicalSave.CommonAreas.Count > 0, "combobox",
                selectedSlot is null ? "No slot selected" : selectedSlot.Label, commonPending, LastSlotChangeNotice),
            groupPresentations, tables, unsupported, status, V2SafetyRailFormatter.Format(status, Mode), GetFavorites());
    }

    private V2TablePresentation PresentTable(V2TableUiMetadata metadata)
    {
        var columns = metadata.ColumnFieldRefs.Select(fieldId => new V2TableColumnPresentation(fieldId, ui.Fields.Single(field => field.FieldRef == fieldId).Label)).ToArray();
        selectedRecords.TryGetValue(metadata.TableRef, out var selected);
        var rows = metadata.RecordRefs.Select(recordId => new V2TableRowPresentation(recordId,
            new ReadOnlyDictionary<string, long>(metadata.ColumnFieldRefs.ToDictionary(fieldId => fieldId, fieldId => GetValue(fieldId, recordId), StringComparer.Ordinal)),
            StringComparer.Ordinal.Equals(selected, recordId), "row")).ToArray();
        var inspector = selected is null ? null : new V2InspectorPresentation(selected,
            metadata.InspectorFieldRefs.Select(fieldId => PresentField(ui.Fields.Single(field => field.FieldRef == fieldId), selected)).ToArray(),
            "Selected record inspector");
        return new(metadata.TableRef, metadata.Label, columns, rows, inspector);
    }

    private V2FieldPresentation PresentField(V2FieldUiMetadata metadata, string? recordId)
    {
        var field = foundation.LogicalSave.Data.Fields.Single(item => item.Id == metadata.FieldRef);
        var value = GetValue(field.Id, recordId);
        var role = metadata.Control switch
        {
            V2UiControl.Enum => "combobox",
            V2UiControl.Boolean or V2UiControl.BitFlags => "checkbox",
            V2UiControl.Number => "spinbutton",
            _ => "text"
        };
        var target = ResolveTarget(field, recordId);
        var emptySlot = target.SlotRef is not null && foundation.LogicalSave.SelectSlot(target.SlotRef).State == V2SlotState.Empty;
        var readOnly = emptySlot || field.Access != "read-write" || field.Computed is not null || metadata.Control is V2UiControl.Text or V2UiControl.Unsupported;
        var error = ValidateValueForPresentation(field, metadata, value);
        return new(field.Id, metadata.Label, metadata.Help, metadata.Control, value, field.Constraints.Minimum, field.Constraints.Maximum,
            metadata.Step, metadata.Options ?? [], field.BitFlags, readOnly, metadata.Label, role, $"error.{field.Id}", error);
    }

    private long GetValue(string fieldId, string? recordId)
    {
        var field = foundation.LogicalSave.Data.Fields.Single(item => item.Id == fieldId);
        var target = ResolveTarget(field, recordId);
        if (pending.TryGetValue(target.Id, out var change)) return change.ProposedValue;
        return values.TryGetValue(new(target.SlotRef, target.RecordRef, fieldId), out var value)
            ? value : throw new InvalidDataException($"Missing decoded UI value: {fieldId}");
    }

    private V2LogicalAddress ResolveTarget(V2FieldModel field, string? recordId)
    {
        var candidates = field.BindingRefs.Select(id => foundation.LogicalSave.Addresses.Single(address => address.Id == id))
            .Where(address => address.SlotRef is null || StringComparer.Ordinal.Equals(address.SlotRef, SelectedSlotId))
            .Where(address => recordId is null ? address.RecordRef is null : StringComparer.Ordinal.Equals(address.RecordRef, recordId))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new InvalidDataException("Logical UI target is missing or ambiguous.");
    }

    private void ValidateInitialValues()
    {
        foreach (var pair in values)
        {
            if (foundation.LogicalSave.Data.Fields.All(field => field.Id != pair.Key.FieldId)) throw new InvalidDataException("Initial UI value references an unknown field.");
            if (pair.Key.SlotId is not null && foundation.LogicalSave.Slots.All(slot => slot.Id != pair.Key.SlotId)) throw new InvalidDataException("Initial UI value references an unknown slot.");
            if (pair.Key.RecordId is not null && foundation.LogicalSave.Data.Records.All(record => record.Id != pair.Key.RecordId)) throw new InvalidDataException("Initial UI value references an unknown record.");
        }
    }

    private static void ValidateValue(V2FieldModel field, V2FieldUiMetadata metadata, long value)
    {
        var error = ValidateValueForPresentation(field, metadata, value);
        if (error is not null) throw new InvalidDataException(error);
    }

    private static string? ValidateValueForPresentation(V2FieldModel field, V2FieldUiMetadata metadata, long value)
    {
        if (field.Constraints.Minimum is long minimum && value < minimum) return "Value is below the minimum.";
        if (field.Constraints.Maximum is long maximum && value > maximum) return "Value is above the maximum.";
        if (field.Constraints.AllowedValues.Count > 0 && !field.Constraints.AllowedValues.Contains(value)) return "Value is not in the allowed set.";
        if (metadata.Control == V2UiControl.Boolean && value is not (0 or 1)) return "Boolean value must be 0 or 1.";
        if (metadata.Control == V2UiControl.Enum && (metadata.Options is null || metadata.Options.All(option => option.Value != value))) return "Enum value is not declared.";
        if (metadata.Control == V2UiControl.BitFlags && field.BitFlags is null) return "BitFlags metadata is missing.";
        if (metadata.Control == V2UiControl.BitFlags && field.BitFlags is not null)
        {
            var mask = field.BitFlags.Flags.Aggregate(0UL, (current, flag) => current | (1UL << flag.Bit));
            if (value < 0 || ((ulong)value & ~mask) != 0) return "BitFlags value contains an undeclared bit.";
        }
        return null;
    }

    private static bool IsVisible(V2UiVisibility visibility, V2UiMode mode) => visibility == V2UiVisibility.Both ||
        (visibility == V2UiVisibility.Simple && mode == V2UiMode.Simple) ||
        (visibility == V2UiVisibility.Advanced && mode == V2UiMode.Advanced);

    private static bool Matches(string query, string text, IReadOnlyList<string>? keywords) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (keywords?.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false);
}

public static class V2SafetyRailFormatter
{
    public static V2SafetyRailPresentation Format(V2UiSafetyStatus status, V2UiMode mode)
    {
        ArgumentNullException.ThrowIfNull(status);
        var blocked = !StringComparer.OrdinalIgnoreCase.Equals(status.WriteReadiness, "Ready") ||
            !string.IsNullOrWhiteSpace(status.BlockingReason);
        var summary = mode == V2UiMode.Simple
            ? blocked ? "書き込みは停止されています。" : "安全確認済み。書き込み準備ができています。"
            : $"Trust: {status.PackTrust}; Compatibility: {status.Compatibility}; Validation: {status.Validation}; Backup: {status.BackupReadiness}; Write: {status.WriteReadiness}";
        var happened = blocked
            ? status.BlockingReason ?? "A required safety check is not ready."
            : status.PendingChanges == 0 ? "No unapplied changes are pending." : $"{status.PendingChanges} pending change(s) are tracked.";
        var saveStatus = status.SaveChanged ? "Save changed: yes." : "Save changed: no.";
        var next = status.NextAction ?? (blocked ? "Resolve the displayed blocking condition before writing." : "Review changes, validate, and export as a new file.");
        return new(summary, blocked, happened, saveStatus, next, "status");
    }
}

public static class V2UiDefinitionValidator
{
    public static V2UiDefinition Validate(V2LogicalSaveModel model, V2UiDefinition definition)
    {
        V2ModelValidator.Validate(model);
        Unique(definition.Groups.Select(group => group.GroupRef), "UI group");
        Unique(definition.Fields.Select(field => field.FieldRef), "UI field");
        Unique(definition.Tables.Select(table => table.TableRef), "UI table");
        Unique(definition.UnsupportedComponents.Select(component => component.Id), "unsupported UI component");
        Unique((definition.Records ?? []).Select(record => record.RecordRef), "UI record");
        var groups = model.Data.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var fields = model.Data.Fields.Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
        var records = model.Data.Records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        var tables = model.Data.Tables.Select(table => table.Id).ToHashSet(StringComparer.Ordinal);
        if (definition.Groups.Any(group => !groups.Contains(group.GroupRef))) throw new InvalidDataException("UI metadata references an unknown group.");
        if (definition.Fields.Any(field => !fields.Contains(field.FieldRef) || field.Step < 1 ||
            field.Options is not null && field.Options.Select(option => option.Value).Distinct().Count() != field.Options.Count))
            throw new InvalidDataException("UI metadata references an invalid field.");
        if ((definition.Records ?? []).Any(record => !records.Contains(record.RecordRef)))
            throw new InvalidDataException("UI metadata references an unknown record.");
        foreach (var table in definition.Tables)
            if (!tables.Contains(table.TableRef) || table.RecordRefs.Count > 4_096 ||
                table.RecordRefs.Distinct(StringComparer.Ordinal).Count() != table.RecordRefs.Count ||
                table.ColumnFieldRefs.Distinct(StringComparer.Ordinal).Count() != table.ColumnFieldRefs.Count ||
                table.InspectorFieldRefs.Distinct(StringComparer.Ordinal).Count() != table.InspectorFieldRefs.Count ||
                table.RecordRefs.Any(record => !records.Contains(record)) ||
                table.ColumnFieldRefs.Concat(table.InspectorFieldRefs).Any(field => !fields.Contains(field)) ||
                table.RecordRefs.Select(record => model.Data.Records.Single(item => item.Id == record))
                    .Any(record => table.ColumnFieldRefs.Concat(table.InspectorFieldRefs).Any(field => !record.FieldRefs.Contains(field, StringComparer.Ordinal))))
                throw new InvalidDataException("UI table metadata contains an unresolved reference.");
        return definition;
    }

    private static void Unique(IEnumerable<string> values, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || !seen.Add(value))) throw new InvalidDataException($"Duplicate or invalid {kind}.");
    }
}

public static class V2LegacyUiMetadataAdapter
{
    public static V2UiDefinition Adapt(GameDefinition definition, V2FoundationDocument foundation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(foundation);
        var groupId = foundation.LogicalSave.Data.Groups.Single().Id;
        var fields = definition.Fields.Select(field => new V2FieldUiMetadata(field.Id, field.Label.Ja, field.Description.Ja,
            field.AllowedValues is { Count: > 0 } ? V2UiControl.Enum : V2UiControl.Number,
            field.Visibility == "advanced" ? V2UiVisibility.Advanced : V2UiVisibility.Both, 1,
            field.AllowedValues?.Select(value => new V2UiOption(value, value.ToString())).ToArray(),
            [field.Id, field.Label.En])).ToArray();
        return V2UiDefinitionValidator.Validate(foundation.LogicalSave,
            new([new(groupId, definition.Title.Ja, 0, V2UiVisibility.Both)], fields, [], []));
    }
}
