using System.IO;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.App;

// Presentation policy only. The domain and file gateway remain the final authority.
internal sealed class UiOperationGate
{
    public bool IsBusy { get; private set; }
    public bool TryBegin()
    {
        if (IsBusy) return false;
        IsBusy = true;
        return true;
    }
    public void End() => IsBusy = false;
}

internal static class UiSafety
{
    public static bool CanEdit(WorkingCopySession? session) => session?.Definition.SaveEdit == true;
    public static bool CanApply(WorkingCopySession? session, string? fieldId) => CanEdit(session) &&
        session!.Definition.Fields.Any(f => f.Id == fieldId && f.Visibility != "hidden" &&
            f.LocationRefs.All(id => session.Definition.SaveLocations.Any(l => l.Id == id && l.Writable)));

    public static bool HasPendingInput(WorkingCopySession? session, string? fieldId, string text) =>
        session is not null && fieldId is not null &&
        (!long.TryParse(text, out var value) || value != session.ReadField(fieldId));

    public static bool CanExport(WorkingCopySession? session, string? fieldId, string text) =>
        CanEdit(session) && !HasPendingInput(session, fieldId, text) && session!.Validate().IsValid;

    // Never forward arbitrary exception messages, paths, parser excerpts or stack traces.
    public static string FriendlyError(Exception error)
    {
        if (error is IOException && (error.HResult & 0xffff) is 32 or 33)
            return "ファイルは別のアプリで使用中です。ゲームやエミュレーターを閉じて再試行してください。";
        return error switch
        {
            UnauthorizedAccessException => "この場所を読み書きできません。書き込み可能な別のフォルダーを選んでください。",
            FileNotFoundException or DirectoryNotFoundException => "ファイルまたはフォルダーが見つかりません。移動されていないか確認して選び直してください。",
            InvalidDataException => "データ・版・整合性を確認できません。対応するROMとセーブ、または正しいバックアップを選び直してください。",
            ArgumentException => "入力形式が正しくありません。ファイル名やバックアップIDを確認して再試行してください。",
            IOException => "ファイルの安全確認または保存に失敗しました。元ファイルの外部変更、空き容量と保存先を確認し、新しい名前で再試行してください。",
            _ => "処理を完了できませんでした。ファイルを選び直してください。解消しない場合は診断情報のコードをお知らせください。"
        };
    }

    public static string FriendlyError(Exception error, string culture)
    {
        if (culture != "en-US") return FriendlyError(error);
        if (error is IOException && (error.HResult & 0xffff) is 32 or 33)
            return "The file is in use by another app. Close the game or emulator and retry.";
        return error switch
        {
            UnauthorizedAccessException => "This location cannot be read or written. Choose another writable folder.",
            FileNotFoundException or DirectoryNotFoundException => "The file or folder was not found. Check its location and choose it again.",
            InvalidDataException => "Data, revision, or integrity could not be verified. Choose a supported ROM and save or the correct backup.",
            ArgumentException => "The input format is invalid. Check the file name or backup ID and retry.",
            IOException => "File safety verification or saving failed. Check for external changes, free space, and the destination; then retry with a new name.",
            _ => "The operation could not be completed. Choose the file again. If the problem persists, report the diagnostic code."
        };
    }
}
