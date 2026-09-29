using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Backup;
using YuniRetroToolkit.Domain;
using YuniRetroToolkit.GameDefinitions;
using YuniRetroToolkit.Infrastructure;
using YuniRetroToolkit.Security;

namespace YuniRetroToolkit.App;

public partial class MainWindow : Window
{
    private readonly string basePath;
    private readonly string localData;
    private readonly GameDefinitionRegistry definitions;
    private readonly PackInstaller packInstaller;
    private readonly SaveWorkflowService workflow;
    private readonly GenericSafeFileService genericFiles;
    private GenericSafeFileResult? genericFile;
    private WorkingCopySession? session;
    private V2FoundationDocument? v2Foundation;
    private V2GenericUiSession? v2UiSession;
    private readonly V2FavoriteStore favoriteStore;
    private readonly V2QuickEditStore quickEditStore;
    private bool quickEditPreferenceUnavailable;
    private readonly V2TutorialSession tutorial;
    private string uiCulture = "ja-JP";
    private V2GuideCatalog? guide;
    private int guideStepIndex;
    private bool dark;
    private readonly UiOperationGate operations = new();
    private readonly Dictionary<TextBlock, (string Japanese, string English)> localizedMessages = new();
    public ObservableCollection<FieldRow> Fields { get; } = [];
    public FieldRow? SelectedField { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        ShowLocalized(StatusText, "1. セーブと ROM を選んでください。完全一致する定義で作業コピーを開きます。",
            "1. Choose a save and ROM. An exact matching definition opens a safe working copy.");
        ShowLocalized(ValidationText, "READ ONLY: 検証済みの作業コピーを開いてください。",
            "READ ONLY: Open a verified working copy before editing.");
        ShowLocalized(QuickEditHint, "Advancedで項目を選び、クイック編集に追加できます。",
            "Select a field in Advanced to add it to Quick Edit.");
        DataContext = this;
        basePath = AppContext.BaseDirectory;
        localData = Path.Combine(basePath, "Data");
        favoriteStore = OpenFavoriteStore(Path.Combine(localData, "preferences", "v2-favorites.json"));
        quickEditStore = OpenQuickEditStore(Path.Combine(localData, "preferences", "v2-quick-edit.json"));
        tutorial = OpenTutorial(Path.Combine(localData, "preferences", "v2-tutorial.json"));
        LoadGuide("ja-JP");
        ShowTutorial();
        var schemaDirectory = Path.Combine(basePath, "schemas");
        packInstaller = new(Path.Combine(localData, "pack-registry"), schemaDirectory, OfficialPackTrustStore.Create(), "0.1.0");
        definitions = new(Path.Combine(basePath, "game-definitions"), Path.Combine(schemaDirectory, "game-definition-v1.schema.json"));
        RefreshPackDefinitionDirectories();
        var backups = new ContentAddressedBackupStore(Path.Combine(localData, "backups"));
        var log = new JsonDiagnosticLog(Path.Combine(localData, "logs"));
        var files = new LocalSaveFileGateway();
        workflow = new(files, backups, definitions, log);
        genericFiles = new(files, backups);
        UpdateControls();
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => OpenButton.Focus()),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
        Closing += (_, e) =>
        {
            if (!operations.IsBusy) return;
            e.Cancel = true;
            ShowLocalized(StatusText, "処理中です。完了してから閉じてください。", "An operation is in progress. Wait for it to finish before closing.");
        };
    }

    private void InstallPack_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            var dialog = new OpenFileDialog { Title = uiCulture == "ja-JP" ? "公式Packを選択" : "Choose an official Pack", Filter = "Save Data Toolkit Pack|*.yrtpack", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            ClearSession();
            var result = packInstaller.Install(new(dialog.FileName));
            if (!result.Success || result.Pack is null)
            {
                ShowLocalized(StatusText, "ERROR: Packの安全確認に失敗したため利用を拒否しました。正しい署名済みPackを選び直してください。",
                    "ERROR: Pack safety verification failed. Choose a valid signed Pack.");
                PackStatusText.Text = "Rejected";
                PackSignatureText.Text = "Not verified";
                return;
            }
            RefreshPackDefinitionDirectories();
            PackNameText.Text = result.Pack.DisplayName;
            PackVersionText.Text = result.Pack.Version;
            PackStatusText.Text = "Verified / Installed / Active";
            PackPublisherText.Text = result.Pack.Publisher;
            PackSignatureText.Text = result.Pack.Signature;
            ShowLocalized(StatusText, "SUCCESS: 署名済みPackをインストールしました。セーブを開き直してください。",
                "SUCCESS: The signed Pack was installed. Reopen the save.");
        }
        catch (Exception ex) { ClearSession(); ShowError(ex); }
        finally { EndOperation(); }
    }

    private void RefreshPackDefinitionDirectories()
    {
        var discovery = packInstaller.DiscoverActiveDefinitionDirectories();
        definitions.SetAdditionalDefinitionDirectories(discovery.DefinitionDirectories);
        if (discovery.RejectedPackIds.Count > 0)
        {
            ShowLocalized(HelpText, "安全確認に失敗したActive Packは読み込まれませんでした。",
                "An active Pack that failed safety verification was not loaded.");
            PackStatusText.Text = "Rejected / Active Pack invalid";
            PackSignatureText.Text = "Not verified";
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            var save = new OpenFileDialog { Title = uiCulture == "ja-JP" ? "セーブファイルを選択" : "Choose a save file", Filter = "Save files|*.srm;*.sav|All files|*.*", CheckFileExists = true };
            if (save.ShowDialog(this) != true) return;
            var rom = new OpenFileDialog { Title = uiCulture == "ja-JP" ? "ROM を選択（識別のため読み取りのみ）" : "Choose a ROM (read only for identification)", Filter = "SFC ROM|*.sfc;*.smc|All files|*.*", CheckFileExists = true };
            if (rom.ShowDialog(this) != true) return;
            ClearSession();
            RefreshPackDefinitionDirectories();
            await definitions.ReloadAsync();
            var result = await workflow.OpenAsync(save.FileName, rom.FileName);
            ShowOpenedSession(result, save.FileName, "ROM");
        }
        catch (Exception ex) { ClearSession(); ShowError(ex); }
        finally { EndOperation(); }
    }

    private void ShowLocalized(TextBlock target, string japanese, string english)
    {
        localizedMessages[target] = (japanese, english);
        target.Text = uiCulture == "ja-JP" ? japanese : english;
    }

    private void RefreshLocalizedMessages()
    {
        foreach (var (target, text) in localizedMessages)
            target.Text = uiCulture == "ja-JP" ? text.Japanese : text.English;
    }

    private async void OpenGeneric_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            var dialog = new OpenFileDialog { Title = uiCulture == "ja-JP" ? "ファイルを読み取り専用で開く" : "Open a file read-only", Filter = "All files|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            ClearSession();
            var opened = await genericFiles.OpenAsync(dialog.FileName);
            genericFile = opened;
            SavePathText.Text = opened.FileName;
            ShowLocalized(DefinitionText, "不明 / 非対応形式 — 読み取り専用の安全モード", "Unknown / Unsupported format — Read-only safe mode");
            ShowLocalized(MetadataText,
                $"ファイル: {opened.FileName}\nサイズ: {opened.Source.Length} bytes\nSHA-256: {opened.Source.Sha256}\n対応: 不明 / 非対応\nバックアップID: {opened.BackupId}",
                $"File: {opened.FileName}\nSize: {opened.Source.Length} bytes\nSHA-256: {opened.Source.Sha256}\nSupport: Unknown / Unsupported\nBackup ID: {opened.BackupId}");
            BackupIdText.Text = opened.BackupId;
            BackupIdText.IsReadOnly = true;
            ShowLocalized(SafetyStatusText,
                "読み取り専用の安全モード: バックアップを検証済みです。項目編集、クイック編集、Change Set、適用、セーブ固有の検証は利用できません。復元先は新しいファイルのみです。",
                "READ ONLY SAFE MODE: Backup verified. Field edit, Quick Edit, Change Set, Apply, and save-specific validation are unavailable. Restore creates a new file only.");
            ShowLocalized(ValidationText, "非対応: このファイルは編集できません。形式・ゲーム・checksumを推測しません。",
                "UNSUPPORTED: This file cannot be edited. Format, game, and checksum are not guessed.");
            ShowLocalized(StatusText, "非対応形式 / 読み取り専用の安全モード: ファイル情報と検証済みバックアップを表示しています。",
                "UNSUPPORTED FORMAT / READ-ONLY SAFE MODE: File information and a verified backup are shown.");
        }
        catch (Exception ex) { ClearSession(); ShowError(ex); }
        finally { EndOperation(); }
    }

    private async void OpenDemo_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            ClearSession();
            var archive = Path.Combine(basePath, "demo", OfficialDemoPack.ArchiveFileName);
            var installed = packInstaller.Install(new(archive, OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion));
            if (!installed.Success || installed.Pack is null || installed.Pack.Publisher != "YuniWorks")
                throw new InvalidDataException("Official Synthetic Demo Packの署名を確認できません。");
            RefreshPackDefinitionDirectories();
            await definitions.ReloadAsync();
            var run = SyntheticDemoAssets.Materialize(Path.Combine(basePath, "demo", "assets"), Path.Combine(localData, "demo-runs"));
            var opened = await workflow.OpenAsync(run.SavePath, run.IdentityPath);
            if (opened.Session.Definition.DefinitionId != OfficialDemoPack.DefinitionId)
                throw new InvalidDataException("Synthetic Demo definitionの一致を確認できません。");
            PackNameText.Text = installed.Pack.DisplayName;
            PackVersionText.Text = installed.Pack.Version;
            PackStatusText.Text = "Verified / Installed / Active";
            PackPublisherText.Text = installed.Pack.Publisher;
            PackSignatureText.Text = installed.Pack.Signature;
            ShowOpenedSession(opened, run.SavePath, "Synthetic identity");
            ShowLocalized(StatusText, "DEMO READY: 署名済み宣言Pack、Synthetic Save、baseline backupを検証しました。Demo Levelを1から7へ変更できます。",
                "DEMO READY: The signed declarative Pack, Synthetic Save, and baseline backup were verified. You can change Demo Level from 1 to 7.");
        }
        catch (Exception ex) { ClearSession(); ShowError(ex); }
        finally { EndOperation(); }
    }

    private void ShowOpenedSession(OpenSessionResult result, string savePath, string identityLabel)
    {
        session = result.Session;
        v2Foundation = V2LegacyDefinitionAdapter.Adapt(session.Definition);
        var v2Ui = V2LegacyUiMetadataAdapter.Adapt(session.Definition, v2Foundation);
        var initialValues = v2Foundation.LogicalSave.Data.Fields.ToDictionary(field =>
        {
            var address = v2Foundation.LogicalSave.Addresses.Single(item => item.FieldRef == field.Id);
            return new V2UiValueKey(address.SlotRef, address.RecordRef, field.Id);
        }, field => session.ReadField(field.Id));
        var editable = UiSafety.CanEdit(session);
        v2UiSession = new(v2Foundation, v2Ui, initialValues, new(
            PackStatusText.Text.StartsWith("Verified", StringComparison.Ordinal) ? "Official Signed" : "Not Verified",
            "Compatible", session.Validate().IsValid ? "Valid" : "Invalid", "Ready", session.Preview().Fields.Count,
            editable ? "Ready" : "Blocked", editable ? null : "The active definition is not editable.",
            editable ? "Review, validate, and export a new file." : "Open a compatible signed Pack and save.", false), favoriteStore, quickEditStore);
        SavePathText.Text = Path.GetFileName(savePath);
        ShowLocalized(DefinitionText, $"{session.Definition.Title.Ja} / {session.Definition.Revision}",
            $"{session.Definition.Title.En} / {session.Definition.Revision}");
        ShowLocalized(MetadataText,
            $"セーブサイズ: {session.Original.Fingerprint.Length}\nOriginal SHA-256: {session.Original.Fingerprint.Sha256}\n{identityLabel} SHA-256: {result.Rom.Sha256}\n定義ID: {session.Definition.DefinitionId}\nschemaVersion: {session.Definition.SchemaVersion}\n基準バックアップ: {result.BaselineBackupId}",
            $"Save size: {session.Original.Fingerprint.Length}\nOriginal SHA-256: {session.Original.Fingerprint.Sha256}\n{identityLabel} SHA-256: {result.Rom.Sha256}\nDefinition ID: {session.Definition.DefinitionId}\nschemaVersion: {session.Definition.SchemaVersion}\nBaseline backup: {result.BaselineBackupId}");
        BackupIdText.Text = result.BaselineBackupId;
        RefreshFields();
        if (UiSafety.CanEdit(session))
            ShowLocalized(StatusText, "READY: バックアップを検証し、作業コピーを開きました。Original は変更されていません。",
                "READY: The backup was verified and a working copy was opened. The Original is unchanged.");
        else
            ShowLocalized(StatusText, "READ ONLY: この定義では編集が許可されていません。",
                "READ ONLY: This definition does not allow editing.");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (operations.IsBusy || !UiSafety.CanApply(session, SelectedField?.Id)) return;
        if (!long.TryParse(ValueText.Text, out var value))
        {
            ShowLocalized(ValidationText, "ERROR: 整数を入力してください。入力内容は保持しています。",
                "ERROR: Enter an integer. Your input has been retained.");
            return;
        }
        try
        {
            if (v2UiSession is not null)
            {
                var proposed = v2UiSession.Propose(SelectedField!.Id, value);
                _ = v2UiSession.ApplyPending(session!, proposed.Target.Id);
            }
            else
            {
                var validation = session!.Apply(SelectedField!.Id, value);
                if (!validation.IsValid)
                {
                    ShowLocalized(ValidationText,
                        "ERROR: " + string.Join(" ", validation.Issues.Select(i => i.Message)) + " 許容値: " + SelectedField.SafeRange,
                        "ERROR: The value failed validation. Safe range: " + SelectedField.SafeRange);
                    return;
                }
            }
            ShowLocalized(StatusText, "APPLIED: 作業コピーに適用しました。まだファイルには保存していません。",
                "APPLIED: The working copy was changed. No file has been saved yet.");
            RefreshFields();
            UpdatePreview();
        }
        catch (InvalidDataException ex) { ShowLocalized(ValidationText, "ERROR: 値・対象・許容範囲を確認してください。",
            "ERROR: Check the value, target, and safe range."); ShowError(ex); }
        catch (Exception ex) { ShowError(ex); }
        finally { UpdateControls(); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (!operations.IsBusy && UiSafety.CanEdit(session) && session!.Undo())
        { v2UiSession?.SynchronizeFrom(session); RefreshFields(); UpdatePreview(); ShowLocalized(StatusText, "直前の変更を元に戻しました。", "The last change was undone."); UpdateControls(); }
    }
    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (!operations.IsBusy && UiSafety.CanEdit(session) && session!.Redo())
        { v2UiSession?.SynchronizeFrom(session); RefreshFields(); UpdatePreview(); ShowLocalized(StatusText, "変更をやり直しました。", "The change was redone."); UpdateControls(); }
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => UpdatePreview();
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!UiSafety.CanExport(session, SelectedField?.Id, ValueText.Text) || !BeginOperation()) return;
        try
        {
            var dialog = new SaveFileDialog { Title = uiCulture == "ja-JP" ? "新しいファイルとして書き出す" : "Export as a new file", Filter = "SRM file|*.srm|All files|*.*", OverwritePrompt = true, AddExtension = true, DefaultExt = ".srm" };
            if (dialog.ShowDialog(this) != true) return;
            var result = await workflow.ExportAsync(session!, dialog.FileName);
            BackupIdText.Text = result.VerifiedBackupId;
            ShowLocalized(StatusText, $"SUCCESS: バックアップと出力を検証しました。SHA-256: {result.Output.Sha256[..16]}…",
                $"SUCCESS: Backup and export verified. SHA-256: {result.Output.Sha256[..16]}…");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BackupIdText.Text) || !BeginOperation()) return;
        try
        {
            var dialog = genericFile is { } generic
                ? new SaveFileDialog { Title = uiCulture == "ja-JP" ? "バックアップを別名で復元" : "Restore backup as a new file", Filter = "All files|*.*", AddExtension = false,
                    FileName = Path.GetFileNameWithoutExtension(generic.FileName) + ".restored" + Path.GetExtension(generic.FileName) }
                : new SaveFileDialog { Title = uiCulture == "ja-JP" ? "バックアップを別名で復元" : "Restore backup as a new file", Filter = "SRM file|*.srm|All files|*.*", AddExtension = true, DefaultExt = ".srm" };
            if (dialog.ShowDialog(this) != true) return;
            FileFingerprint output;
            if (genericFile is { } opened)
            {
                if (!StringComparer.Ordinal.Equals(BackupIdText.Text.Trim(), opened.BackupId))
                    throw new InvalidDataException("Generic file backup ID changed.");
                output = await genericFiles.RestoreAsNewAsync(opened, dialog.FileName);
            }
            else
            {
                var protectedFiles = session is null ? Array.Empty<FileFingerprint>() : new[] { session.Original.Fingerprint };
                output = await workflow.RestoreAsNewAsync(BackupIdText.Text.Trim(), dialog.FileName, protectedFiles);
            }
            ShowLocalized(StatusText, $"SUCCESS: バックアップを新しいファイルへ復元しました。SHA-256: {output.Sha256[..16]}…",
                $"SUCCESS: Backup restored to a new file. SHA-256: {output.Sha256[..16]}…");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); }
    }

    private void FieldsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedField = FieldsGrid.SelectedItem as FieldRow;
        if (SelectedField is not null)
        {
            ValueText.Text = SelectedField.CurrentValue.ToString();
            HelpText.Text = uiCulture == "ja-JP" || session is null
                ? SelectedField.Description
                : session.Definition.Fields.FirstOrDefault(field => field.Id == SelectedField.Id)?.Description.En ?? SelectedField.Description;
            ShowLocalized(ValidationText, $"現在値: {SelectedField.CurrentValue} / 許容値: {SelectedField.SafeRange}",
                $"Current value: {SelectedField.CurrentValue} / Safe range: {SelectedField.SafeRange}");
            System.Windows.Automation.AutomationProperties.SetHelpText(ValueText, ValidationText.Text);
        }
        else { ValueText.Clear(); ShowLocalized(ValidationText, "項目を選ぶと現在値と許容値を表示します。",
            "Select a field to see its current value and safe range."); }
        var favorite = SelectedField is not null && v2UiSession?.GetFavorites().Any(item => item.FieldId == SelectedField.Id) == true;
        FavoriteButton.Content = favorite
            ? uiCulture == "ja-JP" ? "★ お気に入りから削除" : "★ Remove favorite"
            : uiCulture == "ja-JP" ? "☆ お気に入りに追加" : "☆ Add favorite";
        var quick = SelectedField is not null && v2UiSession?.GetQuickEdits().Any(item => item.Shortcut.FieldId == SelectedField.Id) == true;
        QuickEditAddButton.Content = quick ? (uiCulture == "ja-JP" ? "★ クイック編集に追加済み" : "★ Added to Quick Edit") : (uiCulture == "ja-JP" ? "☆ クイック編集に追加" : "☆ Add to Quick Edit");
        System.Windows.Automation.AutomationProperties.SetName(QuickEditAddButton,
            quick ? uiCulture == "ja-JP" ? "クイック編集に追加済み" : "Already added to Quick Edit"
                : uiCulture == "ja-JP" ? "選択項目をクイック編集に追加" : "Add selected field to Quick Edit");
        System.Windows.Automation.AutomationProperties.SetName(FavoriteButton,
            favorite ? uiCulture == "ja-JP" ? "選択項目をお気に入りから削除" : "Remove selected field from favorites"
                : uiCulture == "ja-JP" ? "選択項目をお気に入りに追加" : "Add selected field to favorites");
        UpdateControls();
    }

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        if (genericFile is null)
            ShowLocalized(HelpText,
                "Original は読み取り専用です。ROM は正確なハッシュによる識別だけに使います。変更は作業コピーへ適用され、既存ファイルを上書きしません。",
                "The Original is read-only. The ROM is used only for exact-hash identification. Changes go to a working copy and never overwrite an existing file.");
        else
            ShowLocalized(HelpText,
                "Generic Safe File Modeは読み取り専用です。形式やゲームを推測せず、編集・Change Setは作成しません。復元は別名の新しいファイルだけです。",
                "Generic Safe File Mode is read-only. No format or game is guessed and no edit or Change Set is created. Restore creates a separately named new file only.");
        HelpText.Focus();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        foreach (var item in new[] { NavEditButton, NavPreviewButton, NavBackupButton, NavVerifyButton, NavGuideButton }) item.ClearValue(BackgroundProperty);
        button.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
        if (button.Tag?.ToString() == "guide")
        {
            GuidePanel.IsExpanded = true;
            ShowGuideStep();
            GuideCategorySelector.Focus();
        }
        var (titleJa, titleEn, subtitleJa, subtitleEn, statusJa, statusEn) = button.Tag?.ToString() switch
        {
            "preview" => ("変更プレビュー", "Preview changes", "作業コピーとOriginalの差を確認します。", "Review the difference between the working copy and Original.", "下のプレビュー更新を選んでください。", "Choose Refresh preview below."),
            "backup" => ("バックアップ", "Backups", "安全に検証した世代から、別名の新しいファイルへ復元します。", "Restore a verified backup to a separately named new file.", "右側のBackup restoreを使用してください。", "Use Backup restore on the right."),
            "verify" => ("検証", "Verification", "現在の定義とファイル状態を確認します。", "Review the current definition and file status.", "右側のSafetyとPackの状態を確認してください。", "Check Safety and Pack status on the right."),
            "guide" => ("ガイド", "Guide", "現在実装されている安全な操作と用語を確認します。", "Review the available safe operations and terms.", "右側のガイドからカテゴリと手順を選べます。", "Choose a category and step in the guide on the right."),
            _ => ("セーブ編集", "Edit save", "5つの手順で、安全な作業コピーだけを編集します。", "Edit only a safe working copy in five clear steps.", "セーブ編集モードです。", "Edit save mode is active.")
        };
        ShowLocalized(WorkflowTitle, titleJa, titleEn);
        ShowLocalized(WorkflowSubtitle, subtitleJa, subtitleEn);
        ShowLocalized(StatusText, statusJa, statusEn);
        if (genericFile is not null)
            ShowLocalized(StatusText, "非対応形式 / 読み取り専用の安全モード: 編集・変更適用は利用できません。",
                "UNSUPPORTED FORMAT / READ-ONLY SAFE MODE: Editing and applying changes are unavailable.");
    }

    private void RefreshFields()
    {
        var selectedId = SelectedField?.Id;
        Fields.Clear();
        if (session is null) return;
        if (v2UiSession is not null)
        {
            var presentation = v2UiSession.Render();
            var search = v2UiSession.Search(SearchBox.Text);
            var matchingFields = search.Results.Where(result => result.Kind == V2SearchResultKind.Field).Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
            var matchingGroups = search.Results.Where(result => result.Kind == V2SearchResultKind.Group).Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var group in presentation.Groups)
                foreach (var field in group.Fields)
                {
                    if (!string.IsNullOrWhiteSpace(search.Query) && !matchingFields.Contains(field.FieldId) && !matchingGroups.Contains(group.Id)) continue;
                    var definitionField = session.Definition.Fields.Single(item => item.Id == field.FieldId);
                    var range = field.Options.Count > 0 ? string.Join(" / ", field.Options.Select(option => option.Value)) : $"{field.Minimum}–{field.Maximum}";
                    Fields.Add(new(field.FieldId, group.Label, field.Label, field.Help, field.Value, range, definitionField.VerificationStatus));
                }
            SimpleModeButton.IsChecked = presentation.Mode == V2UiMode.Simple;
            AdvancedModeButton.IsChecked = presentation.Mode == V2UiMode.Advanced;
            SlotSelector.ItemsSource = presentation.Slots.Options;
            SlotSelector.SelectedValue = presentation.Slots.SelectedSlotId;
            SlotSelector.IsEnabled = presentation.Slots.Options.Count > 0 && !operations.IsBusy;
            SearchEmptyText.Text = string.IsNullOrWhiteSpace(search.Query) || Fields.Count > 0 ? ""
                : uiCulture == "ja-JP" ? search.EmptyMessage ?? "一致する項目はありません。" : "No matching fields.";
            FavoriteList.ItemsSource = presentation.Favorites.Where(item => item.IsVisibleInCurrentMode).ToArray();
            QuickEditList.ItemsSource = presentation.Mode == V2UiMode.Simple ? v2UiSession.GetQuickEdits().Select(item => new QuickEditRow(item, uiCulture)).ToArray() : null;
            QuickEditPanel.Visibility = presentation.Mode == V2UiMode.Simple ? Visibility.Visible : Visibility.Collapsed;
            if (quickEditPreferenceUnavailable)
                ShowLocalized(QuickEditHint, "クイック編集設定を安全に読み込めません。編集を停止しました。",
                    "Quick Edit preferences could not be loaded safely. Editing is blocked.");
            else if (v2UiSession.GetQuickEdits().Count == 0)
                ShowLocalized(QuickEditHint, "Advancedで項目を選び、クイック編集に追加できます。",
                    "Select a field in Advanced to add it to Quick Edit.");
            else
                ShowLocalized(QuickEditHint, "既存項目へのローカル参照です。変更は通常の検証とChange Setを通ります。",
                    "Local shortcuts to existing fields. Edits use the normal validation and Change Set.");
            var safetyDetails = $"\nWhat happened: {presentation.SafetyRail.WhatHappened}\n{presentation.SafetyRail.SaveChangeStatus}\nNext: {presentation.SafetyRail.NextAction}\nCurrent: {presentation.Slots.CurrentSummary}" +
                (string.IsNullOrWhiteSpace(presentation.Slots.LastChangeNotice) ? "" : $"\n{presentation.Slots.LastChangeNotice}");
            var englishSummary = presentation.Mode == V2UiMode.Simple
                ? presentation.SafetyRail.IsBlocked ? "Writing is blocked by a safety check." : "Safety checks passed. Ready to write."
                : presentation.SafetyRail.Summary;
            ShowLocalized(SafetyStatusText, presentation.SafetyRail.Summary + safetyDetails, englishSummary + safetyDetails);
        }
        else foreach (var field in session.Definition.Fields.Where(f => f.Visibility != "hidden"))
            Fields.Add(new(field.Id, "", field.Label.Ja, field.Description.Ja, session.ReadField(field.Id), field.AllowedValues is null ? $"{field.ValueSpec.SafeMinimum}–{field.ValueSpec.SafeMaximum}" : string.Join(" / ", field.AllowedValues), field.VerificationStatus));
        FieldsGrid.SelectedItem = Fields.FirstOrDefault(f => f.Id == selectedId);
        if (FieldsGrid.SelectedItem is null)
            if (UiSafety.CanEdit(session))
                ShowLocalized(ValidationText, "編集項目を選ぶと現在値と許容値を表示します。",
                    "Select a field to see its current value and safe range.");
            else
                ShowLocalized(ValidationText, "READ ONLY: この定義では編集が許可されていません。",
                    "READ ONLY: This definition does not allow editing.");
    }

    private void UpdatePreview()
    {
        if (session is null) { PreviewText.Text = uiCulture == "ja-JP" ? "変更はありません。" : "No changes."; return; }
        var preview = session.Preview();
        PreviewText.Text = preview.Fields.Count == 0 ? uiCulture == "ja-JP" ? "変更はありません。" : "No changes."
            : string.Join(Environment.NewLine, preview.Fields.Select(c => $"{c.FieldId}: {c.OriginalValue} → {c.NewValue}")) +
                (uiCulture == "ja-JP" ? $"{Environment.NewLine}変更バイト数: {preview.Bytes.Count}{Environment.NewLine}出力 SHA-256: {preview.ResultSha256}"
                    : $"{Environment.NewLine}Changed bytes: {preview.Bytes.Count}{Environment.NewLine}Output SHA-256: {preview.ResultSha256}");
    }

    private void Theme_Click(object sender, RoutedEventArgs e) { dark = !dark; ReplaceDictionary("Resources/Themes/", dark ? "Resources/Themes/Dark.xaml" : "Resources/Themes/Light.xaml"); }
    private void Japanese_Click(object sender, RoutedEventArgs e) => SwitchLanguage("ja-JP");
    private void English_Click(object sender, RoutedEventArgs e) => SwitchLanguage("en-US");
    private void SwitchLanguage(string culture)
    {
        uiCulture = culture;
        ReplaceDictionary("Resources/Strings/", $"Resources/Strings/{culture}.xaml");
        LoadGuide(culture);
        ShowTutorial();
        RefreshFields();
        RefreshLocalizedMessages();
        if (session is null)
            ShowLocalized(QuickEditHint, "Advancedで項目を選び、クイック編集に追加できます。",
                "Select a field in Advanced to add it to Quick Edit.");
        if (session is not null) UpdatePreview();
    }
    private static void ReplaceDictionary(string prefix, string source)
    {
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.StartsWith(prefix, StringComparison.Ordinal) == true);
        if (old is not null) dictionaries.Remove(old);
        dictionaries.Add(new() { Source = new Uri(source, UriKind.Relative) });
    }

    private async void SaveDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation()) return;
        try
        {
            var dialog = new SaveFileDialog { Title = uiCulture == "ja-JP" ? "診断レポートを新規保存" : "Save diagnostic report as new", Filter = "JSON file|*.json", AddExtension = true, DefaultExt = ".json", OverwritePrompt = false };
            if (dialog.ShowDialog(this) != true) return;
            var snapshot = new DiagnosticSnapshot("0.1.0", Environment.OSVersion.VersionString, PackVersionText.Text, PackStatusText.Text.StartsWith("Verified", StringComparison.Ordinal) ? "VERIFIED" : "NOT_VERIFIED", "NOT_VERIFIED", "NONE", "none");
            await DiagnosticReportService.SaveNewAsync(dialog.FileName, snapshot, DateTimeOffset.UtcNow);
            ShowLocalized(StatusText, "SUCCESS: 診断レポートを新規保存しました。自動送信はしません。",
                "SUCCESS: Diagnostic report saved as a new file. Nothing was uploaded.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); }
    }

    private void ShowError(Exception ex)
    {
        ShowLocalized(StatusText, "ERROR: " + UiSafety.FriendlyError(ex),
            "ERROR: " + UiSafety.FriendlyError(ex, "en-US"));
        DiagnosticText.Text = $"Code: 0x{ex.HResult:X8}";
        StatusText.Focus();
    }

    private bool BeginOperation()
    {
        if (!operations.TryBegin()) return false;
        UpdateControls();
        return true;
    }
    private void EndOperation() { operations.End(); UpdateControls(); }
    private void ClearSession()
    {
        genericFile = null;
        session = null;
        v2Foundation = null;
        v2UiSession = null;
        SelectedField = null;
        Fields.Clear();
        SavePathText.SetResourceReference(TextBlock.TextProperty, "NotSelected");
        localizedMessages.Remove(DefinitionText);
        localizedMessages.Remove(MetadataText);
        localizedMessages.Remove(SafetyStatusText);
        localizedMessages.Remove(HelpText);
        DefinitionText.SetResourceReference(TextBlock.TextProperty, "NoDefinition");
        MetadataText.SetResourceReference(TextBlock.TextProperty, "MetadataHint");
        HelpText.SetResourceReference(TextBlock.TextProperty, "HelpHint");
        ValueText.Clear();
        PreviewText.Clear();
        ShowLocalized(ValidationText, "READ ONLY: 検証済みの作業コピーを開いてください。",
            "READ ONLY: Open a verified working copy before editing.");
        SlotSelector.ItemsSource = null;
        FavoriteList.ItemsSource = null;
        QuickEditList.ItemsSource = null;
        SearchBox.Clear();
        SearchEmptyText.Text = "";
        SafetyStatusText.SetResourceReference(TextBlock.TextProperty, "SafetyText");
        BackupIdText.IsReadOnly = false;
        UpdateControls();
    }

    private void V2Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || v2UiSession is null) return;
        v2UiSession.SetMode(AdvancedModeButton.IsChecked == true ? V2UiMode.Advanced : V2UiMode.Simple);
        RefreshFields();
    }

    private void SlotSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || v2UiSession is null || SlotSelector.SelectedValue is not string slotId || slotId == v2UiSession.SelectedSlotId) return;
        v2UiSession.SelectSlot(slotId);
        SelectedField = null;
        RefreshFields();
    }
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && v2UiSession is not null) RefreshFields();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (v2UiSession is null || SelectedField is null) return;
        if (v2UiSession.GetFavorites().Any(item => item.FieldId == SelectedField.Id)) v2UiSession.RemoveFavorite(SelectedField.Id);
        else v2UiSession.AddFavorite(SelectedField.Id);
        RefreshFields();
    }

    private void QuickEditAdd_Click(object sender, RoutedEventArgs e)
    {
        if (quickEditPreferenceUnavailable || v2UiSession is null || SelectedField is null || AdvancedModeButton.IsChecked != true) return;
        try { v2UiSession.AddQuickEdit(SelectedField.Id); RefreshFields(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void QuickEditRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickEditRow row } && v2UiSession is not null)
        {
            v2UiSession.RemoveQuickEdit(row.Item.Shortcut.FieldId, row.Item.Shortcut.SlotScope, row.Item.Shortcut.RecordId);
            RefreshFields();
        }
    }
    private void QuickEditApply_Click(object sender, RoutedEventArgs e)
    {
        if (quickEditPreferenceUnavailable || sender is not Button { Tag: QuickEditRow row } || v2UiSession is null || session is null || operations.IsBusy) return;
        try
        {
            var value = row.Item.Field!.Control switch
            {
                V2UiControl.Enum => row.SelectedOption,
                V2UiControl.Boolean => row.CheckedValue ? 1L : 0L,
                _ => long.Parse(row.Input, System.Globalization.CultureInfo.InvariantCulture)
            };
            var change = v2UiSession.ProposeQuickEdit(row.Item.Shortcut, value);
            _ = v2UiSession.ApplyPending(session, change.Target.Id);
            ShowLocalized(StatusText, "APPLIED: クイック編集を作業コピーに適用しました。原本は変更されていません。",
                "APPLIED: Quick Edit changed the working copy. The original is unchanged.");
            RefreshFields();
            UpdatePreview();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { UpdateControls(); }
    }

    private void FavoriteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FavoriteList.SelectedValue is not string fieldId) return;
        SearchBox.Clear();
        FieldsGrid.SelectedItem = Fields.FirstOrDefault(field => field.Id == fieldId);
        if (FieldsGrid.SelectedItem is not null) FieldsGrid.ScrollIntoView(FieldsGrid.SelectedItem);
    }

    private void GuideCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        guideStepIndex = 0;
        ShowGuideStep();
    }
    private void GuidePrevious_Click(object sender, RoutedEventArgs e) { if (guideStepIndex > 0) guideStepIndex--; ShowGuideStep(); }
    private void GuideNext_Click(object sender, RoutedEventArgs e)
    {
        if (guide is not null && GuideCategorySelector.SelectedValue is string category &&
            guideStepIndex + 1 < guide.Categories.Single(item => item.Id == category).Steps.Count) guideStepIndex++;
        ShowGuideStep();
    }

    private void ShowGuideStep()
    {
        if (guide is null || GuideCategorySelector.SelectedValue is not string category) return;
        var item = guide.Navigate(category, guideStepIndex);
        GuideTitleText.Text = item.Step.Title;
        GuideBodyText.Text = item.Step.Body;
        GuidePreviousButton.IsEnabled = item.StepIndex > 0;
        GuideNextButton.IsEnabled = item.StepIndex + 1 < item.StepCount;
    }

    private void LoadGuide(string culture)
    {
        try
        {
            guide = V2GuideCatalog.Load(Path.Combine(basePath, "Resources", "Guide", culture + ".json"));
            if (GuideCategorySelector is not null)
            {
                GuideCategorySelector.ItemsSource = guide.Categories;
                GuideCategorySelector.SelectedValue = "beginner";
                guideStepIndex = 0;
                ShowGuideStep();
            }
        }
        catch (Exception)
        {
            guide = null;
            if (GuideBodyText is not null) GuideBodyText.Text = "Guide unavailable. Core safety gates remain active.";
        }
    }

    private static V2FavoriteStore OpenFavoriteStore(string path)
    {
        try { return new(path); }
        catch (Exception) { return new(); }
    }
    private V2QuickEditStore OpenQuickEditStore(string path)
    {
        try { return new(path); }
        catch (Exception) { quickEditPreferenceUnavailable = true; return new(); }
    }
    private static V2TutorialSession OpenTutorial(string path)
    {
        try { return new(path); }
        catch (Exception) { return new(); }
    }
    private void TutorialDemo_Click(object sender, RoutedEventArgs e) => OpenDemo_Click(sender, e);
    private void TutorialPrevious_Click(object sender, RoutedEventArgs e) { tutorial.Previous(); ShowTutorial(true); }
    private void TutorialNext_Click(object sender, RoutedEventArgs e) { tutorial.Next(); ShowTutorial(true); }
    private void TutorialSkip_Click(object sender, RoutedEventArgs e) { tutorial.Skip(); ShowTutorial(); }
    private void TutorialRerun_Click(object sender, RoutedEventArgs e) { tutorial.Start(); ShowTutorial(true); }
    private void ShowTutorial(bool focus = false)
    {
        if (TutorialPanel is null) return;
        var state = tutorial.Present(uiCulture);
        TutorialPanel.IsExpanded = state.IsOpen;
        TutorialProgressText.Text = state.Progress;
        TutorialTitleText.Text = state.Step.Title;
        TutorialBodyText.Text = state.Step.Body;
        TutorialPreviousButton.IsEnabled = state.IsOpen && state.Index > 0;
        TutorialNextButton.IsEnabled = state.IsOpen;
        TutorialNextButton.Content = state.Index + 1 == state.Count
            ? uiCulture == "ja-JP" ? "完了" : "Finish"
            : uiCulture == "ja-JP" ? "次へ" : "Next";
        if (focus) TutorialTitleText.Focus();
    }
    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateControls();
        if (ReferenceEquals(sender, ValueText) && UiSafety.HasPendingInput(session, SelectedField?.Id, ValueText.Text))
            ShowLocalized(ValidationText, "未適用の入力があります。許容値を確認して適用してください: " + SelectedField?.SafeRange,
                "Input has not been applied. Check the safe range and apply: " + SelectedField?.SafeRange);
    }
    private void UpdateControls()
    {
        if (RestoreButton is null) return;
        var idle = !operations.IsBusy;
        OpenButton.IsEnabled = OpenGenericButton.IsEnabled = OpenDemoButton.IsEnabled = InstallPackButton.IsEnabled = DiagnosticButton.IsEnabled = idle;
        NavEditButton.IsEnabled = NavPreviewButton.IsEnabled = NavBackupButton.IsEnabled = NavVerifyButton.IsEnabled = NavGuideButton.IsEnabled = idle;
        FieldsGrid.IsEnabled = idle && session is not null;
        ValueText.IsEnabled = ApplyButton.IsEnabled = idle && UiSafety.CanApply(session, SelectedField?.Id);
        UndoButton.IsEnabled = idle && UiSafety.CanEdit(session) && session!.CanUndo;
        RedoButton.IsEnabled = idle && UiSafety.CanEdit(session) && session!.CanRedo;
        PreviewButton.IsEnabled = idle && session is not null;
        ExportButton.IsEnabled = idle && UiSafety.CanExport(session, SelectedField?.Id, ValueText.Text);
        BackupIdText.IsEnabled = idle;
        RestoreButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(BackupIdText.Text);
        SimpleModeButton.IsEnabled = AdvancedModeButton.IsEnabled = idle && v2UiSession is not null;
        SlotSelector.IsEnabled = idle && v2UiSession is not null && v2UiSession.Render().Slots.Options.Count > 0;
        SearchBox.IsEnabled = FavoriteButton.IsEnabled = idle && v2UiSession is not null;
        QuickEditAddButton.IsEnabled = idle && !quickEditPreferenceUnavailable && v2UiSession is not null && SelectedField is not null && v2UiSession.CanAddQuickEdit(SelectedField.Id);
    }

    public sealed class QuickEditRow
    {
        public V2QuickEditPresentation Item { get; }
        public string Label => Item.Shortcut.Label;
        private readonly string culture;
        public string Detail => (culture == "ja-JP" ? Item.Status switch
        {
            "Ready" => "編集可能", "Pending" => "未適用の変更あり", "Read only" => "読み取り専用",
            "Blocked by safety status" => "安全確認により編集不可",
            "Unavailable in this slot" => "このスロットでは利用不可",
            _ => "項目の参照が変更または消失しました"
        } : Item.Status) + (Item.Field is null ? "" : $" · {Item.Field.Value} · {Item.Field.Minimum}–{Item.Field.Maximum}");
        public bool CanEdit => Item.Field is { IsReadOnly: false } && !Item.IsStale && Item.Status is "Ready" or "Pending";
        public string RemoveName => Label + (culture == "ja-JP" ? " クイック編集から削除" : " Remove from Quick Edit");
        public string ApplyName => Label + (culture == "ja-JP" ? " クイック編集の値を適用" : " Apply Quick Edit value");
        public Visibility NumberVisibility => Item.Field?.Control is V2UiControl.Enum or V2UiControl.Boolean ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EnumVisibility => Item.Field?.Control == V2UiControl.Enum ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BooleanVisibility => Item.Field?.Control == V2UiControl.Boolean ? Visibility.Visible : Visibility.Collapsed;
        public IReadOnlyList<V2UiOption> Options => Item.Field?.Options ?? [];
        public string Input { get; set; }
        public long SelectedOption { get; set; }
        public bool CheckedValue { get; set; }
        public QuickEditRow(V2QuickEditPresentation item, string culture)
        {
            Item = item;
            this.culture = culture;
            Input = item.Field?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            SelectedOption = item.Field?.Value ?? 0;
            CheckedValue = item.Field?.Value == 1;
        }
    }
    public sealed record FieldRow(string Id, string Group, string Label, string Description, long CurrentValue, string SafeRange, string Verification);
}
