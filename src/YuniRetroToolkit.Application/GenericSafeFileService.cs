using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.Application;

// Deliberately does not create a WorkingCopySession or consult a game definition.
public sealed record GenericSafeFileResult(FileFingerprint Source, string BackupId)
{
    public string FileName => Path.GetFileName(Source.FullPath);
    public bool CanEdit => false;
    public bool CanCreateChangeSet => false;
    public string SupportStatus => "Unknown / Unsupported";
}

public sealed class GenericSafeFileService(ISaveFileGateway files, IBackupStore backups)
{
    public async Task<GenericSafeFileResult> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        // The existing save gateway enforces its bounded, read-only input policy.
        var original = await files.ReadOriginalAsync(path, cancellationToken);
        var backupId = await backups.CreateAsync("baseline", original, original.Fingerprint, cancellationToken);
        var verified = await backups.ReadVerifiedAsync(backupId, cancellationToken);
        if (!verified.Span.SequenceEqual(original.Snapshot().Span))
            throw new InvalidDataException("Generic file backup verification failed.");
        var current = await files.FingerprintAsync(original.Fingerprint.FullPath, cancellationToken);
        if (!SameSource(original.Fingerprint, current))
            throw new IOException("The source changed while its backup was being verified.");
        return new(original.Fingerprint, backupId);
    }

    public async Task<FileFingerprint> RestoreAsNewAsync(GenericSafeFileResult opened, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var verified = await backups.ReadVerifiedAsync(opened.BackupId, cancellationToken);
        if (verified.Length != opened.Source.Length || FileFingerprint.Hash(verified.Span) != opened.Source.Sha256)
            throw new InvalidDataException("Generic file backup no longer matches the opened source.");
        var output = await files.WriteNewAtomicallyAsync(outputPath, verified, [opened.Source], cancellationToken);
        if (output.Length != opened.Source.Length || output.Sha256 != opened.Source.Sha256)
            throw new IOException("Generic file restore verification failed.");
        return output;
    }

    private static bool SameSource(FileFingerprint expected, FileFingerprint actual) =>
        expected.Length == actual.Length && expected.Sha256 == actual.Sha256 &&
        (expected.VolumeSerial is null || expected.VolumeSerial == actual.VolumeSerial) &&
        (expected.FileIndex is null || expected.FileIndex == actual.FileIndex);
}
