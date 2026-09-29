using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.Application;

public interface ISaveFileGateway
{
    Task<OriginalFile> ReadOriginalAsync(string path, CancellationToken cancellationToken = default);
    Task<FileFingerprint> FingerprintAsync(string path, CancellationToken cancellationToken = default);
    Task<FileFingerprint> WriteNewAtomicallyAsync(string outputPath, ReadOnlyMemory<byte> content, IReadOnlyCollection<FileFingerprint> protectedFiles, CancellationToken cancellationToken = default);
}

public interface IBackupStore
{
    Task<string> CreateAsync(string kind, OriginalFile file, CancellationToken cancellationToken = default);
    Task<string> CreateAsync(string kind, OriginalFile file, FileFingerprint source, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>> ReadVerifiedAsync(string backupId, CancellationToken cancellationToken = default);
}

public interface IGameDefinitionRegistry
{
    IReadOnlyList<GameDefinition> ActiveDefinitions { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    GameDefinition? Detect(string romSha256, long romSize, long saveSize);
}

public enum VerificationOutcome { Pass, Fail, Blocked, NotImplemented, NotRun }
public sealed record VerificationRequest(string DefinitionHash, string RomHash, string SaveHash, string Level);
public sealed record VerificationReport(VerificationOutcome Outcome, string Adapter, string EmulatorVersion, string Message);
public interface IVerificationAdapter { Task<VerificationReport> VerifyAsync(VerificationRequest request, CancellationToken cancellationToken = default); }

public interface IDiagnosticLog { void Write(string eventName, string message, Exception? error = null); }

public sealed record OpenSessionResult(WorkingCopySession Session, FileFingerprint Rom, string BaselineBackupId);
public sealed record ExportResult(FileFingerprint Output, string VerifiedBackupId, ChangePreview Preview);

public sealed class SaveWorkflowService(ISaveFileGateway files, IBackupStore backups, IGameDefinitionRegistry definitions, IDiagnosticLog log)
{
    public async Task<OpenSessionResult> OpenAsync(string savePath, string romPath, CancellationToken cancellationToken = default)
    {
        var original = await files.ReadOriginalAsync(savePath, cancellationToken);
        var rom = await files.FingerprintAsync(romPath, cancellationToken);
        var definition = definitions.Detect(rom.Sha256, rom.Length, original.Fingerprint.Length)
            ?? throw new InvalidDataException("ROM のハッシュ、版、またはセーブ形式に一致する定義がありません。");
        var backupId = await backups.CreateAsync("baseline", original, original.Fingerprint, cancellationToken);
        var afterBackup = await files.FingerprintAsync(original.Fingerprint.FullPath, cancellationToken);
        if (afterBackup.Length != original.Fingerprint.Length || afterBackup.Sha256 != original.Fingerprint.Sha256)
            throw new IOException("バックアップ作成中に Original が変更されたためセッション開始を中止しました。");
        log.Write("session.opened", $"definition={definition.DefinitionId}; saveHash={original.Fingerprint.Sha256}");
        return new(new WorkingCopySession(original, definition), rom, backupId);
    }

    public async Task<ExportResult> ExportAsync(WorkingCopySession session, string outputPath, CancellationToken cancellationToken = default)
    {
        var before = await files.FingerprintAsync(session.Original.Fingerprint.FullPath, cancellationToken);
        if (before.Sha256 != session.Original.Fingerprint.Sha256) throw new IOException("編集中に Original が変更されたため書き出しを中止しました。");
        var validation = session.Validate();
        if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Issues.Select(i => i.Message)));
        var bytes = session.WorkingBytes;
        // Back up the validated output snapshot before invoking the writer. A backup
        // failure must not leave an output behind. A failed write may retain this
        // recoverable pre-export snapshot, but must never report export success.
        var snapshot = new OriginalFile(new(outputPath, bytes.Length, FileFingerprint.Hash(bytes.Span)), bytes.Span);
        var backupId = await backups.CreateAsync("pre-export", snapshot, session.Original.Fingerprint, cancellationToken);
        var backedUp = await backups.ReadVerifiedAsync(backupId, cancellationToken);
        if (!backedUp.Span.SequenceEqual(bytes.Span)) throw new IOException("書き出し前バックアップの検証に失敗しました。");
        var beforeWrite = await files.FingerprintAsync(session.Original.Fingerprint.FullPath, cancellationToken);
        if (beforeWrite.Sha256 != before.Sha256 || beforeWrite.Length != before.Length)
            throw new IOException("バックアップ作成中に Original が変更されたため書き出しを中止しました。");
        var output = await files.WriteNewAtomicallyAsync(outputPath, bytes, [session.Original.Fingerprint], cancellationToken);
        var after = await files.FingerprintAsync(session.Original.Fingerprint.FullPath, cancellationToken);
        if (after.Sha256 != session.Original.Fingerprint.Sha256) throw new IOException("Original の不変性確認に失敗しました。");
        log.Write("session.exported", $"outputHash={output.Sha256}; changes={session.Preview().Fields.Count}");
        return new(output, backupId, session.Preview());
    }

    public async Task<FileFingerprint> RestoreAsNewAsync(string backupId, string outputPath, IReadOnlyCollection<FileFingerprint> protectedFiles, CancellationToken cancellationToken = default)
    {
        var bytes = await backups.ReadVerifiedAsync(backupId, cancellationToken);
        return await files.WriteNewAtomicallyAsync(outputPath, bytes, protectedFiles, cancellationToken);
    }
}
