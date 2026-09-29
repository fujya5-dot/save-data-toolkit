using System.Text;
using System.Text.Json;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.Backup;

public sealed record BackupRetentionOptions(
    int NewestGenerations = 10,
    int DailyDays = 30,
    int MonthlyMonths = 12,
    long SoftCapBytes = 2L * 1024 * 1024 * 1024,
    TimeSpan LockTimeout = default)
{
    public static BackupRetentionOptions Default { get; } = new(LockTimeout: TimeSpan.FromSeconds(10));

    internal BackupRetentionOptions Validate()
    {
        if (NewestGenerations is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(NewestGenerations));
        if (DailyDays is < 0 or > 365) throw new ArgumentOutOfRangeException(nameof(DailyDays));
        if (MonthlyMonths is < 0 or > 60) throw new ArgumentOutOfRangeException(nameof(MonthlyMonths));
        if (SoftCapBytes < 1) throw new ArgumentOutOfRangeException(nameof(SoftCapBytes));
        var timeout = LockTimeout == default ? TimeSpan.FromSeconds(10) : LockTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(LockTimeout));
        return this with { LockTimeout = timeout };
    }
}

public sealed record BackupMaintenanceResult(
    int ValidGenerations,
    int DeletedGenerations,
    int QuarantinedGenerations,
    int DeletedOrphanBlobs,
    int CleanupFailures,
    long VaultBytes,
    bool CapSatisfied);

public sealed class BackupCapacityException(string message) : IOException(message);

public sealed class ContentAddressedBackupStore : IBackupStore
{
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string root;
    private readonly string blobs;
    private readonly string manifests;
    private readonly string quarantine;
    private readonly string pins;
    private readonly string indexPath;
    private readonly string lockPath;
    private readonly BackupRetentionOptions retention;
    private readonly TimeProvider timeProvider;

    public ContentAddressedBackupStore(string rootDirectory, BackupRetentionOptions? retention = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("バックアップ保存先が空です。", nameof(rootDirectory));
        root = Path.GetFullPath(rootDirectory);
        blobs = Path.Combine(root, "blobs");
        manifests = Path.Combine(root, "manifests");
        quarantine = Path.Combine(root, "quarantine");
        pins = Path.Combine(root, "pins");
        indexPath = Path.Combine(root, "registry.json");
        lockPath = Path.Combine(root, ".backup.lock");
        this.retention = (retention ?? BackupRetentionOptions.Default).Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> CreateAsync(string kind, OriginalFile file, CancellationToken cancellationToken = default)
        => await CreateAsync(kind, file, file.Fingerprint, cancellationToken);

    public async Task<string> CreateAsync(string kind, OriginalFile file, FileFingerprint source, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        var bytes = file.Snapshot();
        if (bytes.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("バックアップ元のsizeが不正です。");
        if (file.Fingerprint.Length != bytes.Length) throw new InvalidDataException("バックアップ元のsizeが一致しません。");
        var hash = FileFingerprint.Hash(bytes.Span);
        if (!string.Equals(hash, file.Fingerprint.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("バックアップ元のハッシュが一致しません。");
        if (!IsLowerHex(source.Sha256, 64) || source.Length <= 0) throw new ArgumentException("Original fingerprint が不正です。", nameof(source));
        var sourceIdentity = ComputeSourceIdentity(source);

        EnsureVaultDirectories();
        await using var operationLock = await AcquireLockAsync(cancellationToken);
        EnsureVaultDirectories();

        var scan = await ScanManifestsAsync(quarantineInvalid: true, cancellationToken);
        await CleanupAsync(scan, cancellationToken);
        var blobPath = ResolveBlobPath(hash);
        if (!File.Exists(blobPath) && checked(CurrentVaultBytes() + bytes.Length) > retention.SoftCapBytes)
            throw new BackupCapacityException("BACKUP_CAP_REACHED: 保護対象を残したまま容量上限を満たせません。");

        if (!File.Exists(blobPath)) await WriteCreateNewAsync(blobPath, bytes, cancellationToken);
        await VerifyBlobAsync(blobPath, hash, bytes.Length, cancellationToken);

        scan = await ScanManifestsAsync(quarantineInvalid: true, cancellationToken);
        var sequence = scan.Valid.Count == 0 ? 1 : checked(scan.Valid.Max(item => item.Manifest.Sequence) + 1);
        var id = Guid.NewGuid().ToString("N");
        var manifest = new BackupManifest(
            id,
            kind,
            hash,
            bytes.Length,
            timeProvider.GetUtcNow(),
            false,
            "1.0.0",
            1,
            sequence,
            source.Sha256,
            sourceIdentity,
            "VERIFIED");
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var manifestPath = ResolveManifestPath(id);
        await WriteCreateNewAsync(manifestPath, payload, cancellationToken);
        var confirmed = await ReadManifestAsync(manifestPath, cancellationToken);
        if (confirmed.Id != id || confirmed.Sha256 != hash || confirmed.Sequence != sequence)
            throw new IOException("バックアップ manifest の確定後検証に失敗しました。");

        scan = await ScanManifestsAsync(quarantineInvalid: true, cancellationToken);
        var maintenance = await CleanupAsync(scan, cancellationToken);
        await WriteIndexAsync(sequence, maintenance.ValidGenerations, cancellationToken);
        if (!maintenance.CapSatisfied)
            throw new BackupCapacityException("BACKUP_CAP_REACHED: 新しい世代は保護されましたが、容量上限を満たせません。");
        return id;
    }

    public async Task<ReadOnlyMemory<byte>> ReadVerifiedAsync(string backupId, CancellationToken cancellationToken = default)
    {
        ValidateBackupId(backupId);
        EnsureVaultDirectories();
        await using var operationLock = await AcquireLockAsync(cancellationToken);
        var manifest = await ReadManifestAsync(ResolveManifestPath(backupId), cancellationToken);
        if (!string.Equals(manifest.Id, backupId, StringComparison.Ordinal)) throw new InvalidDataException("マニフェスト ID が一致しません。");
        var path = ResolveBlobPath(manifest.Sha256);
        RejectReparsePoint(path);
        var bytes = await ReadBoundedAsync(path, checked(manifest.Length + 1L), cancellationToken);
        if (bytes.Length != manifest.Length || FileFingerprint.Hash(bytes) != manifest.Sha256) throw new InvalidDataException("バックアップが破損しています。");
        return bytes;
    }

    public async Task<BackupMaintenanceResult> RunMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        EnsureVaultDirectories();
        await using var operationLock = await AcquireLockAsync(cancellationToken);
        var scan = await ScanManifestsAsync(quarantineInvalid: true, cancellationToken);
        return await CleanupAsync(scan, cancellationToken);
    }

    public async Task SetPinnedAsync(string backupId, bool pinned, CancellationToken cancellationToken = default)
    {
        ValidateBackupId(backupId);
        EnsureVaultDirectories();
        await using var operationLock = await AcquireLockAsync(cancellationToken);
        _ = await ReadManifestAsync(ResolveManifestPath(backupId), cancellationToken);
        var pinPath = ResolvePinPath(backupId);
        if (pinned)
        {
            if (!File.Exists(pinPath)) await WriteCreateNewAsync(pinPath, Encoding.ASCII.GetBytes(backupId), cancellationToken);
        }
        else if (File.Exists(pinPath))
        {
            RejectReparsePoint(pinPath);
            File.Delete(pinPath);
        }
    }

    private async Task<BackupMaintenanceResult> CleanupAsync(ManifestScan scan, CancellationToken cancellationToken)
    {
        var failures = Math.Max(0, scan.InvalidCount - scan.QuarantinedCount);
        var deleted = 0;
        var deletedBlobs = 0;
        var deletedBlobCandidates = new Dictionary<string, int>(StringComparer.Ordinal);
        CleanupStagingFiles(ref failures);

        var keep = SelectProtectedGenerations(scan.Valid);
        foreach (var item in scan.Valid.Where(item => !keep.Contains(item.Manifest.Id))
                     .OrderBy(item => item.Manifest.Sequence).ThenBy(item => item.Manifest.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectReparsePoint(item.Path);
                File.Delete(item.Path);
                deletedBlobCandidates.TryAdd(item.Manifest.Sha256, item.Manifest.Length);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }

        var remaining = await ScanManifestsAsync(quarantineInvalid: false, cancellationToken);
        var anyQuarantined = Directory.EnumerateFiles(quarantine, "*.corrupt", SearchOption.TopDirectoryOnly).Any();
        if (!anyQuarantined && remaining.InvalidCount == 0)
        {
            var referenced = remaining.Valid.Select(item => item.Manifest.Sha256).ToHashSet(StringComparer.Ordinal);
            foreach (var candidate in deletedBlobCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!referenced.Contains(candidate.Key))
                {
                    var path = ResolveBlobPath(candidate.Key);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        RejectReparsePoint(path);
                        await VerifyBlobAsync(path, candidate.Key, candidate.Value, cancellationToken);
                        File.Delete(path);
                        deletedBlobs++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        failures++;
                    }
                }
            }
        }

        var bytes = CurrentVaultBytes();
        return new(remaining.Valid.Count, deleted, scan.QuarantinedCount, deletedBlobs, failures, bytes, bytes <= retention.SoftCapBytes);
    }

    private HashSet<string> SelectProtectedGenerations(IReadOnlyList<ManifestEntry> entries)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        foreach (var group in entries.GroupBy(item => EffectiveSourceIdentity(item.Manifest), StringComparer.Ordinal))
        {
            var newest = group.OrderByDescending(item => item.Manifest.Sequence).ThenByDescending(item => item.Manifest.CreatedAt).ToArray();
            foreach (var item in newest.Take(retention.NewestGenerations)) keep.Add(item.Manifest.Id);
            if (newest.Length > 0) keep.Add(newest[0].Manifest.Id);
            foreach (var item in newest.Where(item => item.Manifest.Pinned || File.Exists(ResolvePinPath(item.Manifest.Id)))) keep.Add(item.Manifest.Id);

            if (retention.DailyDays > 0)
            {
                var dailyCutoff = now.UtcDateTime.Date.AddDays(-(retention.DailyDays - 1));
                foreach (var day in newest.Where(item => item.Manifest.CreatedAt.UtcDateTime.Date >= dailyCutoff)
                             .GroupBy(item => item.Manifest.CreatedAt.UtcDateTime.Date))
                    keep.Add(day.OrderByDescending(item => item.Manifest.Sequence).First().Manifest.Id);
            }

            if (retention.MonthlyMonths > 0)
            {
                var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(retention.MonthlyMonths - 1));
                foreach (var month in newest.Where(item => item.Manifest.CreatedAt.UtcDateTime >= monthStart)
                             .GroupBy(item => (item.Manifest.CreatedAt.UtcDateTime.Year, item.Manifest.CreatedAt.UtcDateTime.Month)))
                    keep.Add(month.OrderByDescending(item => item.Manifest.Sequence).First().Manifest.Id);
            }
        }
        return keep;
    }

    private async Task<ManifestScan> ScanManifestsAsync(bool quarantineInvalid, CancellationToken cancellationToken)
    {
        var valid = new List<ManifestEntry>();
        var invalid = 0;
        var quarantined = 0;
        foreach (var path in Directory.EnumerateFiles(manifests, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectReparsePoint(path);
                var manifest = await ReadManifestAsync(path, cancellationToken);
                await VerifyBlobAsync(ResolveBlobPath(manifest.Sha256), manifest.Sha256, manifest.Length, cancellationToken);
                valid.Add(new(path, manifest));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                invalid++;
                if (quarantineInvalid && TryQuarantine(path)) quarantined++;
            }
        }
        return new(valid, invalid, quarantined);
    }

    private async Task<BackupManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        var bytes = await ReadBoundedAsync(path, MaximumManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(bytes) ?? throw new InvalidDataException("マニフェストを読めません。");
        var fileId = Path.GetFileNameWithoutExtension(path);
        ValidateBackupId(fileId);
        if (manifest.Id != fileId) throw new InvalidDataException("マニフェスト ID が一致しません。");
        if (!IsLowerHex(manifest.Sha256, 64)) throw new InvalidDataException("マニフェスト hash が不正です。");
        if (manifest.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("マニフェスト size が不正です。");
        if (manifest.CreatedAt == default) throw new InvalidDataException("マニフェスト timestamp が不正です。");
        if (manifest.Sequence < 0) throw new InvalidDataException("マニフェスト sequence が不正です。");
        if (manifest.BackupPolicyVersion is not (0 or 1)) throw new InvalidDataException("未対応のbackup policyです。");
        if (manifest.ValidationResult is not (null or "VERIFIED")) throw new InvalidDataException("未検証の世代です。");
        if (manifest.SourceSha256 is not null && !IsLowerHex(manifest.SourceSha256, 64)) throw new InvalidDataException("Original fingerprint が不正です。");
        if (manifest.SourceIdentity is not null && !IsLowerHex(manifest.SourceIdentity, 64)) throw new InvalidDataException("Source identity が不正です。");
        ValidateKind(manifest.Kind);
        return manifest;
    }

    private async Task VerifyBlobAsync(string path, string expectedHash, int expectedLength, CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        var bytes = await ReadBoundedAsync(path, checked(expectedLength + 1L), cancellationToken);
        if (bytes.Length != expectedLength || FileFingerprint.Hash(bytes) != expectedHash) throw new InvalidDataException("バックアップ blob が破損しています。");
    }

    private async Task WriteIndexAsync(long sequence, int generationCount, CancellationToken cancellationToken)
    {
        var index = JsonSerializer.SerializeToUtf8Bytes(new BackupRegistry("1.0.0", 1, sequence, generationCount, timeProvider.GetUtcNow()), JsonOptions);
        await WriteReplaceAtomicAsync(indexPath, index, cancellationToken);
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + retention.LockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectReparsePointIfExists(lockPath);
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough | FileOptions.Asynchronous);
            }
            catch (IOException) when (timeProvider.GetUtcNow() < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private void EnsureVaultDirectories()
    {
        EnsureNoReparseInExistingPath(root);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        Directory.CreateDirectory(blobs);
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(quarantine);
        Directory.CreateDirectory(pins);
        RejectReparsePoint(blobs);
        RejectReparsePoint(manifests);
        RejectReparsePoint(quarantine);
        RejectReparsePoint(pins);
    }

    private void CleanupStagingFiles(ref int failures)
    {
        foreach (var directory in new[] { root, blobs, manifests, pins })
        foreach (var path in Directory.EnumerateFiles(directory, "*.staging", SearchOption.TopDirectoryOnly))
        {
            try
            {
                RejectReparsePoint(path);
                if (File.GetLastWriteTimeUtc(path) <= timeProvider.GetUtcNow().UtcDateTime.AddHours(-24)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; }
        }
    }

    private bool TryQuarantine(string path)
    {
        try
        {
            RejectReparsePoint(path);
            var target = Path.Combine(quarantine, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.corrupt");
            EnsureInsideRoot(target);
            File.Move(path, target, false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private long CurrentVaultBytes()
    {
        long total = 0;
        foreach (var directory in new[] { blobs, manifests, quarantine, pins })
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            RejectReparsePoint(path);
            total = checked(total + new FileInfo(path).Length);
        }
        return total;
    }

    private string ResolveBlobPath(string hash)
    {
        if (!IsLowerHex(hash, 64)) throw new InvalidDataException("バックアップ hash が不正です。");
        var path = Path.Combine(blobs, hash + ".bin");
        EnsureInsideRoot(path);
        return path;
    }

    private string ResolveManifestPath(string id)
    {
        ValidateBackupId(id);
        var path = Path.Combine(manifests, id + ".json");
        EnsureInsideRoot(path);
        return path;
    }

    private string ResolvePinPath(string id)
    {
        ValidateBackupId(id);
        var path = Path.Combine(pins, id + ".pin");
        EnsureInsideRoot(path);
        return path;
    }

    private void EnsureInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("バックアップ保存先の境界外です。");
    }

    private static void EnsureNoReparseInExistingPath(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("reparse point配下はバックアップ保存先にできません。");
        }
    }

    private static void RejectReparsePointIfExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse pointはバックアップに使用できません。");
    }

    private static string EffectiveSourceIdentity(BackupManifest manifest) =>
        manifest.SourceIdentity ?? "legacy:" + manifest.Id;

    private static string ComputeSourceIdentity(FileFingerprint source)
    {
        string material;
        if (source.VolumeSerial is { } volumeSerial && source.FileIndex is { } fileIndex)
        {
            material = $"windows-file-id-v1\0{volumeSerial:x8}\0{fileIndex:x16}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(source.FullPath)) throw new ArgumentException("Original path が不正です。", nameof(source));
            var canonicalPath = Path.GetFullPath(source.FullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            material = "canonical-path-v1\0" + canonicalPath;
        }
        return FileFingerprint.Hash(Encoding.UTF8.GetBytes(material));
    }

    private static void ValidateBackupId(string id)
    {
        if (!IsLowerHex(id, 32)) throw new ArgumentException("バックアップ ID が不正です。", nameof(id));
    }

    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64 || kind.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("バックアップ種別が不正です。", nameof(kind));
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<byte[]> ReadBoundedAsync(string path, long maximum, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 0 || stream.Length > maximum) throw new InvalidDataException("バックアップファイルが許可サイズを超えています。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static async Task WriteCreateNewAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("保存先が不正です。");
        RejectReparsePoint(directory);
        var staging = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.staging");
        try
        {
            await WriteStagingAsync(staging, bytes, cancellationToken);
            File.Move(staging, path, false);
        }
        finally { TryDeleteStaging(staging); }
    }

    private static async Task WriteReplaceAtomicAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("保存先が不正です。");
        RejectReparsePoint(directory);
        var staging = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.staging");
        try
        {
            await WriteStagingAsync(staging, bytes, cancellationToken);
            File.Move(staging, path, true);
        }
        finally { TryDeleteStaging(staging); }
    }

    private static async Task WriteStagingAsync(string staging, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
        var reread = await File.ReadAllBytesAsync(staging, cancellationToken);
        if (!reread.AsSpan().SequenceEqual(bytes.Span)) throw new IOException("バックアップ staging の再検証に失敗しました。");
    }

    private static void TryDeleteStaging(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record BackupManifest(
        string Id,
        string Kind,
        string Sha256,
        int Length,
        DateTimeOffset CreatedAt,
        bool Pinned,
        string? ManifestSchemaVersion = null,
        int BackupPolicyVersion = 0,
        long Sequence = 0,
        string? SourceSha256 = null,
        string? SourceIdentity = null,
        string? ValidationResult = null);

    private sealed record BackupRegistry(string SchemaVersion, int BackupPolicyVersion, long LastSequence, int GenerationCount, DateTimeOffset UpdatedAt);
    private sealed record ManifestEntry(string Path, BackupManifest Manifest);
    private sealed record ManifestScan(IReadOnlyList<ManifestEntry> Valid, int InvalidCount, int QuarantinedCount);
}
