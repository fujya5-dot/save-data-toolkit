using System.IO;
using System.Text.Json;

namespace YuniRetroToolkit.App;

public sealed record V2TutorialStep(string Id, string Title, string Body, string Target);
public sealed record V2TutorialPresentation(int Index, int Count, V2TutorialStep Step, bool Completed,
    bool IsOpen, string Progress, string AutomationRole);

public sealed class V2TutorialSession
{
    private const int MaximumFileBytes = 4 * 1024;
    private static readonly string[] Ids =
    ["open", "slot", "field", "value", "pending", "backup", "validate", "diff", "apply", "verify"];
    private static readonly string[] Targets =
    ["OpenButton", "SlotSelector", "FieldsGrid", "ValueText", "PreviewText",
     "SafetyStatusText", "ValidationText", "PreviewButton", "ExportButton", "StatusText"];
    private static readonly (string Title, string Body)[] Japanese =
    [
        ("セーブを開く", "左の『Demoを開く』で練習できます。自分のセーブは『開く』から選び、原本は読み取り専用です。"),
        ("スロットを選ぶ", "『編集するセーブ』で対象を確認します。空のスロットは編集できません。"),
        ("項目を選ぶ", "編集項目の一覧から、Packが公開した項目を選びます。必要な場合だけAdvancedで探します。"),
        ("値を変更", "新しい値を入力し『適用』で作業コピーへ反映します。原本ファイルは変わりません。"),
        ("変更を確認", "右側のSafetyと変更プレビューで、作業コピーの変更状態を確認します。"),
        ("バックアップを確認", "右側のSafetyとBackup IDを確認します。開いた時点でbaseline backupが作られます。"),
        ("値を検証", "入力欄の許容値とValidation表示を確認します。不正な値は適用されません。"),
        ("差分を見る", "『プレビュー更新』で変更項目とバイト差分を確認します。"),
        ("新しいファイルに保存", "内容を確認して『新しいファイルとして書き出す』を押します。原本への上書きは行いません。"),
        ("完了を確認", "成功表示と出力の確認結果を見ます。ゲーム内効果の確認は別の検証です。")
    ];
    private static readonly (string Title, string Body)[] English =
    [
        ("Open a save", "Use Open Demo on the left to practice. Open selects your own save; the original stays read-only."),
        ("Choose a slot", "Check the Edit save selector. Empty slots cannot be edited."),
        ("Choose a field", "Select a field exposed by the Pack. Use Advanced only when needed."),
        ("Change a value", "Enter a new value and Apply it to the working copy. The original file stays unchanged."),
        ("Review changes", "Check Safety and the change preview for changes to the working copy."),
        ("Check backup", "Check Safety and the Backup ID. Opening a save creates a baseline backup."),
        ("Validate", "Check the allowed range and validation message. Invalid values are rejected."),
        ("Review the diff", "Refresh Preview to inspect field and byte changes."),
        ("Export a new file", "After review, export a new file. The original is not overwritten."),
        ("Verify completion", "Check the success message and verified output. In-game effect needs separate verification.")
    ];

    private readonly string? path;
    private bool completed;
    private int index;
    public bool IsOpen { get; private set; }
    public bool Completed => completed;
    public int Index => index;

    public V2TutorialSession(string? path = null)
    {
        this.path = path is null ? null : Path.GetFullPath(path);
        if (this.path is not null && File.Exists(this.path))
        {
            CheckPath();
            if (new FileInfo(this.path).Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("Tutorial preference is invalid.");
            using var data = JsonDocument.Parse(File.ReadAllBytes(this.path), new() { MaxDepth = 4 });
            if (data.RootElement.ValueKind != JsonValueKind.Object ||
                !data.RootElement.TryGetProperty("completed", out var flag) ||
                flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Tutorial preference is invalid.");
            completed = flag.GetBoolean();
        }
        IsOpen = !completed;
    }

    public void Start() { index = 0; IsOpen = true; }
    public void Skip() { IsOpen = false; }
    public void Previous() { if (index > 0) index--; }
    public void Next()
    {
        if (!IsOpen) throw new InvalidOperationException("Tutorial is closed.");
        if (index < Ids.Length - 1) { index++; return; }
        completed = true;
        try { Save(); IsOpen = false; }
        catch { completed = false; throw; }
    }

    public V2TutorialPresentation Present(string culture)
    {
        var text = culture.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? Japanese : English;
        return new(index, Ids.Length, new(Ids[index], text[index].Title, text[index].Body, Targets[index]),
            completed, IsOpen, $"{index + 1} / {Ids.Length}", "group");
    }

    private void Save()
    {
        if (path is null) return;
        CheckPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        CheckPath();
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { completed });
                stream.Flush(true);
            }
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
            throw new InvalidDataException("Tutorial preference path cannot be a reparse point.");
    }
}
