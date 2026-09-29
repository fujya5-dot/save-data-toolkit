using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YuniRetroToolkit.GameDefinitions;

public static class PackResultCodes
{
    public const string Verified = "PACK_VERIFIED";
    public const string Active = "PACK_ACTIVE";
    public const string AlreadyActive = "PACK_ALREADY_ACTIVE";
    public const string SignatureInvalid = "PACK_SIGNATURE_INVALID";
    public const string HashMismatch = "PACK_HASH_MISMATCH";
    public const string UntrustedKey = "PACK_UNTRUSTED_KEY";
    public const string SchemaInvalid = "PACK_SCHEMA_INVALID";
    public const string SemanticInvalid = "PACK_SEMANTIC_INVALID";
    public const string UnsafePath = "PACK_UNSAFE_PATH";
    public const string Incompatible = "PACK_INCOMPATIBLE";
    public const string IdentityMismatch = "PACK_IDENTITY_MISMATCH";
    public const string DowngradeRejected = "PACK_DOWNGRADE_REJECTED";
    public const string ActivePointerInvalid = "PACK_ACTIVE_POINTER_INVALID";
    public const string ActivationFailed = "PACK_ACTIVATION_FAILED";
    public const string Busy = "PACK_INSTALL_BUSY";
}

public sealed record ActivePackStatus(
    string PackId,
    string Version,
    string ManifestSha256,
    string DisplayName,
    string Publisher,
    string Status,
    string Signature);

public sealed record PackOperationResult(bool Success, string Code, string UserMessage, ActivePackStatus? Pack = null);

public sealed record ActivePackDiscoveryResult(IReadOnlyList<string> DefinitionDirectories, IReadOnlyList<string> RejectedPackIds);

public sealed record PackInstallRequest(string ArchivePath, string? ExpectedPackId = null, string? ExpectedVersion = null);

internal enum PackInstallFaultPoint { AfterInstallStaging, BeforeActivePointerSwap }

public sealed class PackInstaller
{
    public const long MaximumArchiveBytes = 16 * 1024 * 1024;
    public const long MaximumExpandedBytes = 32 * 1024 * 1024;
    public const int MaximumEntries = 66;
    public const int MaximumDefinitions = 64;
    public const int MaximumPathLength = 160;
    public const int MaximumExpansionRatio = 20;
    private static readonly Regex DefinitionPath = new("^definitions/[a-z0-9]+(?:[._-][a-z0-9]+)*\\.yrt-game\\.json$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly string root;
    private readonly string schemaDirectory;
    private readonly PackTrustStore trustStore;
    private readonly SemanticVersion toolkitVersion;
    private readonly Action<PackInstallFaultPoint>? faultInjector;

    public PackInstaller(string rootDirectory, string schemaDirectory, PackTrustStore trustStore, string currentToolkitVersion)
        : this(rootDirectory, schemaDirectory, trustStore, currentToolkitVersion, null) { }

    internal PackInstaller(string rootDirectory, string schemaDirectory, PackTrustStore trustStore, string currentToolkitVersion, Action<PackInstallFaultPoint>? faultInjector)
    {
        root = Path.GetFullPath(rootDirectory);
        this.schemaDirectory = Path.GetFullPath(schemaDirectory);
        this.trustStore = trustStore;
        toolkitVersion = SemanticVersion.Parse(currentToolkitVersion);
        this.faultInjector = faultInjector;
    }

    public PackOperationResult Install(PackInstallRequest request)
    {
        try
        {
            PrepareRoot();
            using var installLock = AcquireInstallLock();
            var staging = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var stagedArchive = Path.Combine(staging, "source.yrtpack");
                CopyArchive(request.ArchivePath, stagedArchive);
                var verified = VerifyArchive(stagedArchive, request.ExpectedPackId, request.ExpectedVersion);
                var current = ReadActiveLocked(verified.PackageId, allowMissing: true);
                if (current is not null && SemanticVersion.Parse(verified.PackageVersion) < SemanticVersion.Parse(current.Version))
                    throw new PackException(PackResultCodes.DowngradeRejected);

                var payload = Path.Combine(staging, "payload");
                WritePayload(payload, verified.Files);
                var stagedVerification = VerifyDirectory(payload, verified.PackageId, verified.PackageVersion);
                if (!string.Equals(stagedVerification.ManifestSha256, verified.ManifestSha256, StringComparison.Ordinal))
                    throw new PackException(PackResultCodes.HashMismatch);
                faultInjector?.Invoke(PackInstallFaultPoint.AfterInstallStaging);

                var packageDirectory = Path.Combine(root, "packs", verified.PackageId);
                EnsureDirectory(packageDirectory);
                var versionDirectory = VersionDirectory(verified.PackageId, verified.PackageVersion);
                var alreadyInstalled = Directory.Exists(versionDirectory);
                if (alreadyInstalled)
                {
                    var installed = VerifyDirectory(versionDirectory, verified.PackageId, verified.PackageVersion);
                    if (!string.Equals(installed.ManifestSha256, verified.ManifestSha256, StringComparison.Ordinal))
                        throw new PackException(PackResultCodes.IdentityMismatch);
                }
                else
                {
                    Directory.Move(payload, versionDirectory);
                }

                if (current is not null && string.Equals(current.Version, verified.PackageVersion, StringComparison.Ordinal) &&
                    string.Equals(current.ManifestSha256, verified.ManifestSha256, StringComparison.Ordinal))
                    return Success(PackResultCodes.AlreadyActive, "このPackはすでに安全に有効化されています。", verified);

                faultInjector?.Invoke(PackInstallFaultPoint.BeforeActivePointerSwap);
                WriteActivePointer(verified);
                var active = ReadActiveLocked(verified.PackageId, allowMissing: false)!;
                return Success(PackResultCodes.Active, "Packを検証し、安全に有効化しました。", active);
            }
            finally
            {
                DeleteStaging(staging);
            }
        }
        catch (PackException ex) { return Failure(ex.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OverflowException or CryptographicException or NotSupportedException)
        {
            return Failure(PackResultCodes.ActivationFailed);
        }
    }

    public PackOperationResult Rollback(string packId, string version)
    {
        try
        {
            PrepareRoot();
            ValidateIdentity(packId, version);
            using var installLock = AcquireInstallLock();
            var current = ReadActiveLocked(packId, allowMissing: false)!;
            var requested = SemanticVersion.Parse(version);
            if (requested >= SemanticVersion.Parse(current.Version)) throw new PackException(PackResultCodes.DowngradeRejected);
            var verified = VerifyDirectory(VersionDirectory(packId, version), packId, version);
            faultInjector?.Invoke(PackInstallFaultPoint.BeforeActivePointerSwap);
            WriteActivePointer(verified);
            var active = ReadActiveLocked(packId, allowMissing: false)!;
            return Success(PackResultCodes.Active, "以前の検証済みPackへ安全に戻しました。", active);
        }
        catch (PackException ex) { return Failure(ex.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OverflowException or CryptographicException or NotSupportedException)
        {
            return Failure(PackResultCodes.ActivationFailed);
        }
    }

    public PackOperationResult GetActive(string packId)
    {
        try
        {
            PrepareRoot();
            ValidatePackId(packId);
            using var installLock = AcquireInstallLock();
            var active = ReadActiveLocked(packId, allowMissing: false)!;
            return Success(PackResultCodes.Active, "有効なPackを確認しました。", active);
        }
        catch (PackException ex) { return Failure(ex.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OverflowException or CryptographicException or NotSupportedException)
        {
            return Failure(PackResultCodes.ActivePointerInvalid);
        }
    }

    public ActivePackDiscoveryResult DiscoverActiveDefinitionDirectories()
    {
        PrepareRoot();
        using var installLock = AcquireInstallLock();
        var directories = new List<string>();
        var rejected = new List<string>();
        foreach (var pointer in Directory.EnumerateFiles(Path.Combine(root, "active"), "*.json", SearchOption.TopDirectoryOnly))
        {
            var packId = Path.GetFileNameWithoutExtension(pointer);
            try
            {
                ValidatePackId(packId);
                var active = ReadActiveLocked(packId, allowMissing: false)!;
                directories.Add(Path.Combine(VersionDirectory(active.PackId, active.Version), "definitions"));
            }
            catch (Exception ex) when (ex is PackException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OverflowException or CryptographicException or NotSupportedException)
            {
                rejected.Add(packId);
            }
        }
        return new(directories, rejected);
    }

    private VerifiedPack VerifyArchive(string path, string? expectedPackId, string? expectedVersion)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumArchiveBytes) throw new PackException(PackResultCodes.UnsafePath);
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is 0 or > MaximumEntries) throw new PackException(PackResultCodes.UnsafePath);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry, normalized);
            if (entry.Length > MaximumExpandedBytes || expanded > MaximumExpandedBytes - entry.Length) throw new PackException(PackResultCodes.UnsafePath);
            expanded += entry.Length;
            if (entry.Length > 0 && entry.CompressedLength > 0 && entry.Length > entry.CompressedLength * MaximumExpansionRatio)
                throw new PackException(PackResultCodes.UnsafePath);
            files.Add(entry.FullName, ReadEntry(entry));
        }
        if (expanded > info.Length * MaximumExpansionRatio) throw new PackException(PackResultCodes.UnsafePath);
        return VerifyFiles(files, expectedPackId, expectedVersion);
    }

    private VerifiedPack VerifyDirectory(string directory, string expectedPackId, string expectedVersion)
    {
        if (!Directory.Exists(directory)) throw new PackException(PackResultCodes.ActivePointerInvalid);
        var rootInfo = new DirectoryInfo(directory);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackException(PackResultCodes.UnsafePath);
        var directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (directories.Length != 1 || !string.Equals(Path.GetFileName(directories[0]), "definitions", StringComparison.Ordinal) ||
            (new DirectoryInfo(directories[0]).Attributes & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateDirectories(directories[0], "*", SearchOption.TopDirectoryOnly).Any())
            throw new PackException(PackResultCodes.UnsafePath);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Concat(Directory.EnumerateFiles(directories[0], "*", SearchOption.TopDirectoryOnly)))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackException(PackResultCodes.UnsafePath);
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            ValidateEntryPath(relative);
            if (!files.TryAdd(relative, ReadBoundedFile(file))) throw new PackException(PackResultCodes.UnsafePath);
        }
        return VerifyFiles(files, expectedPackId, expectedVersion);
    }

    private VerifiedPack VerifyFiles(IReadOnlyDictionary<string, byte[]> files, string? expectedPackId, string? expectedVersion)
    {
        if (!files.TryGetValue("manifest.json", out var manifestBytes) || !files.TryGetValue("signature.json", out var signatureBytes))
            throw new PackException(PackResultCodes.SignatureInvalid);
        using var validator = new SchemaDocumentValidator(schemaDirectory);
        var manifestResult = validator.Validate(SchemaDocumentKind.PackManifest, manifestBytes);
        if (!manifestResult.SyntaxValid || !manifestResult.SchemaValid) throw new PackException(PackResultCodes.SchemaInvalid);
        var signatureResult = validator.Validate(SchemaDocumentKind.PackSignature, signatureBytes);
        if (!signatureResult.Accepted) throw new PackException(PackResultCodes.SignatureInvalid);

        var canonicalManifest = JsonCanonicalizer.Canonicalize(manifestBytes);
        if (!manifestBytes.AsSpan().SequenceEqual(canonicalManifest)) throw new PackException(PackResultCodes.SignatureInvalid);
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(canonicalManifest));
        using var manifest = StrictJson.Parse(manifestBytes);
        using var signature = StrictJson.Parse(signatureBytes);
        var manifestRoot = manifest.RootElement;
        var signatureRoot = signature.RootElement;
        if (!manifestRoot.TryGetProperty("signing", out var signing)) throw new PackException(PackResultCodes.SignatureInvalid);
        var keyId = signing.GetProperty("keyId").GetString()!;
        if (!string.Equals(keyId, signatureRoot.GetProperty("keyId").GetString(), StringComparison.Ordinal) ||
            !string.Equals(keyId, manifestRoot.GetProperty("publisher").GetProperty("keyId").GetString(), StringComparison.Ordinal) ||
            !string.Equals(manifestHash, signatureRoot.GetProperty("manifestSha256").GetString(), StringComparison.Ordinal))
            throw new PackException(PackResultCodes.SignatureInvalid);
        if (!trustStore.TryGet(keyId, out var trusted) || trusted.Revoked) throw new PackException(PackResultCodes.UntrustedKey);
        var publisher = manifestRoot.GetProperty("publisher").GetProperty("displayName").GetString()!;
        if (!trusted.AllowsPublisher(publisher))
            throw new PackException(PackResultCodes.UntrustedKey);
        var detached = DecodeBase64Url(signatureRoot.GetProperty("signatureBase64Url").GetString()!);
        if (!Ed25519SignatureVerifier.Verify(trusted.PublicKey.Span, canonicalManifest, detached)) throw new PackException(PackResultCodes.SignatureInvalid);

        var packageId = manifestRoot.GetProperty("packageId").GetString()!;
        var packageVersion = manifestRoot.GetProperty("packageVersion").GetString()!;
        if (expectedPackId is not null && !string.Equals(expectedPackId, packageId, StringComparison.Ordinal) ||
            expectedVersion is not null && !string.Equals(expectedVersion, packageVersion, StringComparison.Ordinal))
            throw new PackException(PackResultCodes.IdentityMismatch);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var declaredContent = new List<(JsonElement Metadata, byte[] Bytes)>();
        foreach (var content in manifestRoot.GetProperty("contents").EnumerateArray())
        {
            var contentPath = content.GetProperty("path").GetString()!;
            if (!declared.Add(contentPath) || !files.TryGetValue(contentPath, out var bytes)) throw new PackException(PackResultCodes.HashMismatch);
            if (bytes.LongLength != content.GetProperty("size").GetInt64() ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), content.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                throw new PackException(PackResultCodes.HashMismatch);
            declaredContent.Add((content.Clone(), bytes));
        }
        if (files.Keys.Any(path => path is not "manifest.json" and not "signature.json" && !declared.Contains(path)) || files.Count != declared.Count + 2)
            throw new PackException(PackResultCodes.HashMismatch);

        if (!manifestResult.SemanticValid) throw new PackException(PackResultCodes.SemanticInvalid);
        ValidateCompatibility(manifestRoot);
        string? displayName = null;
        foreach (var (content, bytes) in declaredContent)
        {
            var definitionResult = validator.Validate(SchemaDocumentKind.GameDefinition, bytes);
            if (!definitionResult.SyntaxValid || !definitionResult.SchemaValid) throw new PackException(PackResultCodes.SchemaInvalid);
            if (!definitionResult.SemanticValid) throw new PackException(PackResultCodes.SemanticInvalid);
            using var definition = StrictJson.Parse(bytes);
            displayName ??= definition.RootElement.GetProperty("title").GetProperty("ja").GetString();
            if (!string.Equals(definition.RootElement.GetProperty("definitionId").GetString(), content.GetProperty("definitionId").GetString(), StringComparison.Ordinal) ||
                !string.Equals(definition.RootElement.GetProperty("definitionVersion").GetString(), content.GetProperty("definitionVersion").GetString(), StringComparison.Ordinal))
                throw new PackException(PackResultCodes.IdentityMismatch);
        }
        return new(packageId, packageVersion, manifestHash, displayName ?? packageId, publisher, files);
    }

    private void ValidateCompatibility(JsonElement manifest)
    {
        var minimum = SemanticVersion.Parse(manifest.GetProperty("minimumAppVersion").GetString()!);
        if (toolkitVersion < minimum)
            throw new PackException(PackResultCodes.Incompatible);
        if (manifest.TryGetProperty("maximumAppVersionExclusive", out var maximum))
        {
            var exclusive = SemanticVersion.Parse(maximum.GetString()!);
            if (exclusive <= minimum || toolkitVersion >= exclusive) throw new PackException(PackResultCodes.Incompatible);
        }
    }

    private static void ValidateEntry(ZipArchiveEntry entry, HashSet<string> normalized)
    {
        ValidateEntryPath(entry.FullName);
        var collisionKey = entry.FullName.Normalize(NormalizationForm.FormC);
        if (!normalized.Add(collisionKey)) throw new PackException(PackResultCodes.UnsafePath);
        var unixMode = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixMode == 0xa000 || (entry.ExternalAttributes & 0x400) != 0) throw new PackException(PackResultCodes.UnsafePath);
    }

    internal static void ValidateEntryPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathLength || path.Any(character => character > 0x7f || char.IsControl(character)) ||
            path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.EndsWith('/') ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new PackException(PackResultCodes.UnsafePath);
        if (path is not "manifest.json" and not "signature.json" && !DefinitionPath.IsMatch(path))
            throw new PackException(PackResultCodes.UnsafePath);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        var limit = entry.FullName is "manifest.json" or "signature.json" ? StrictJson.MaximumDefinitionBytes : StrictJson.MaximumDefinitionBytes;
        if (entry.Length is <= 0 || entry.Length > limit) throw new PackException(PackResultCodes.UnsafePath);
        using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        CopyBounded(input, output, limit);
        if (output.Length != entry.Length) throw new PackException(PackResultCodes.HashMismatch);
        return output.ToArray();
    }

    private static byte[] ReadBoundedFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > StrictJson.MaximumDefinitionBytes) throw new PackException(PackResultCodes.HashMismatch);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(checked((int)info.Length));
        CopyBounded(input, output, StrictJson.MaximumDefinitionBytes);
        if (output.Length != info.Length) throw new PackException(PackResultCodes.HashMismatch);
        return output.ToArray();
    }

    private static void CopyBounded(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length > limit - read) throw new PackException(PackResultCodes.UnsafePath);
            output.Write(buffer, 0, read);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/') + "==";
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != Ed25519SignatureVerifier.SignatureSize) throw new FormatException();
            return bytes;
        }
        catch (FormatException) { throw new PackException(PackResultCodes.SignatureInvalid); }
    }

    private void PrepareRoot()
    {
        EnsureDirectory(root);
        EnsureDirectory(Path.Combine(root, "packs"));
        EnsureDirectory(Path.Combine(root, "active"));
        EnsureDirectory(Path.Combine(root, ".staging"));
    }

    private FileStream AcquireInstallLock()
    {
        var path = Path.Combine(root, ".install.lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return stream;
                stream.Dispose();
                throw new PackException(PackResultCodes.UnsafePath);
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
            catch (IOException) { throw new PackException(PackResultCodes.Busy); }
        }
    }

    private void CopyArchive(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath)) throw new PackException(PackResultCodes.UnsafePath);
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists || source.Length is <= 0 or > MaximumArchiveBytes || (source.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new PackException(PackResultCodes.UnsafePath);
        using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        CopyBounded(input, output, MaximumArchiveBytes);
        output.Flush(true);
    }

    private static void WritePayload(string directory, IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(directory);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (var item in files)
        {
            ValidateEntryPath(item.Key);
            var destination = Path.GetFullPath(Path.Combine(directory, item.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PackException(PackResultCodes.UnsafePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
            stream.Write(item.Value);
            stream.Flush(true);
        }
    }

    private void WriteActivePointer(VerifiedPack pack)
    {
        var activeDirectory = Path.Combine(root, "active");
        var destination = Path.Combine(activeDirectory, pack.PackageId + ".json");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonCanonicalizer.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifestSha256 = pack.ManifestSha256,
            packageId = pack.PackageId,
            packageVersion = pack.PackageVersion,
            schemaVersion = "1.0.0"
        }));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private ActivePackStatus? ReadActiveLocked(string packId, bool allowMissing)
    {
        var pointerPath = Path.Combine(root, "active", packId + ".json");
        if (!File.Exists(pointerPath))
        {
            if (allowMissing) return null;
            throw new PackException(PackResultCodes.ActivePointerInvalid);
        }
        if ((File.GetAttributes(pointerPath) & FileAttributes.ReparsePoint) != 0) throw new PackException(PackResultCodes.ActivePointerInvalid);
        var pointerBytes = ReadBoundedFile(pointerPath);
        using var pointer = StrictJson.Parse(pointerBytes);
        var properties = pointer.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        var expected = new[] { "manifestSha256", "packageId", "packageVersion", "schemaVersion" };
        if (!properties.SequenceEqual(expected, StringComparer.Ordinal) || pointer.RootElement.GetProperty("schemaVersion").GetString() != "1.0.0" ||
            pointer.RootElement.GetProperty("packageId").GetString() != packId)
            throw new PackException(PackResultCodes.ActivePointerInvalid);
        var version = pointer.RootElement.GetProperty("packageVersion").GetString()!;
        var expectedHash = pointer.RootElement.GetProperty("manifestSha256").GetString()!;
        ValidateIdentity(packId, version);
        var packageDirectory = Path.Combine(root, "packs", packId);
        if (!Directory.Exists(packageDirectory) || (new DirectoryInfo(packageDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new PackException(PackResultCodes.ActivePointerInvalid);
        if (!Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new PackException(PackResultCodes.ActivePointerInvalid);
        var verified = VerifyDirectory(VersionDirectory(packId, version), packId, version);
        if (!string.Equals(verified.ManifestSha256, expectedHash, StringComparison.Ordinal)) throw new PackException(PackResultCodes.ActivePointerInvalid);
        return Status(verified);
    }

    private string VersionDirectory(string packId, string version) => Path.Combine(root, "packs", packId, version);
    private static void ValidateIdentity(string packId, string version) { ValidatePackId(packId); _ = SemanticVersion.Parse(version); }
    private static void ValidatePackId(string packId)
    {
        if (!Regex.IsMatch(packId, "^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new PackException(PackResultCodes.IdentityMismatch);
    }

    private static void DeleteStaging(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new PackException(PackResultCodes.UnsafePath);
    }

    private static PackOperationResult Success(string code, string message, VerifiedPack pack) => new(true, code, message, Status(pack));
    private static PackOperationResult Success(string code, string message, ActivePackStatus pack) => new(true, code, message, pack);
    private static ActivePackStatus Status(VerifiedPack pack) => new(pack.PackageId, pack.PackageVersion, pack.ManifestSha256, pack.DisplayName, pack.Publisher, "Active", "Verified");
    private static PackOperationResult Failure(string code) => new(false, code, "安全確認に失敗したため、このPackは有効化されませんでした。");

    private sealed record VerifiedPack(string PackageId, string PackageVersion, string ManifestSha256, string DisplayName, string Publisher, IReadOnlyDictionary<string, byte[]> Files);
    internal sealed class PackException(string code) : Exception { public string Code { get; } = code; }
}

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] PreRelease) : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static SemanticVersion Parse(string value)
    {
        var match = Pattern.Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            throw new PackInstaller.PackException(PackResultCodes.Incompatible);
        var preRelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (preRelease.Any(identifier => identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)))
            throw new PackInstaller.PackException(PackResultCodes.Incompatible);
        return new(major, minor, patch, preRelease);
    }

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (PreRelease.Length == 0 || other.PreRelease.Length == 0) return PreRelease.Length == other.PreRelease.Length ? 0 : PreRelease.Length == 0 ? 1 : -1;
        for (var index = 0; index < Math.Min(PreRelease.Length, other.PreRelease.Length); index++)
        {
            var leftNumeric = int.TryParse(PreRelease[index], out var left);
            var rightNumeric = int.TryParse(other.PreRelease[index], out var right);
            var comparison = leftNumeric && rightNumeric ? left.CompareTo(right) : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(PreRelease[index], other.PreRelease[index]);
            if (comparison != 0) return comparison;
        }
        return PreRelease.Length.CompareTo(other.PreRelease.Length);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}
