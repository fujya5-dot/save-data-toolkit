using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YuniRetroToolkit.GameDefinitions;

namespace YuniRetroToolkit.Security;

public static partial class SecurityResultCodes
{
    public const string Verified = "VERIFIED";
    public const string Malformed = "MALFORMED";
    public const string UntrustedKey = "UNTRUSTED_KEY";
    public const string SignatureInvalid = "SIGNATURE_INVALID";
    public const string PurposeMismatch = "PURPOSE_MISMATCH";
    public const string PayloadInvalid = "PAYLOAD_INVALID";
    public const string Expired = "EXPIRED";
    public const string Incompatible = "INCOMPATIBLE";
    public const string ArtifactMismatch = "ARTIFACT_MISMATCH";
    public const string ComponentMismatch = "COMPONENT_MISMATCH";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex IdentifierPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex HashPattern();
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ErrorCodePattern();
}

public sealed record TrustedPolicyKey(string KeyId, string Purpose, ReadOnlyMemory<byte> PublicKey, bool Revoked = false);

public sealed class PolicyTrustStore(IEnumerable<TrustedPolicyKey> keys)
{
    private readonly IReadOnlyDictionary<(string KeyId, string Purpose), TrustedPolicyKey> entries = keys.ToDictionary(item => (item.KeyId, item.Purpose));
    public bool TryGet(string keyId, string purpose, out TrustedPolicyKey key) => entries.TryGetValue((keyId, purpose), out key!);
    public int Count => entries.Count;
    public static PolicyTrustStore Production() => new([]); // Fail closed until separate License/Update/Integrity roots are approved.
}

public sealed record SignedPolicyResult(bool Success, string Code, JsonElement? Payload = null, string? KeyId = null);

public sealed class SignedPolicyVerifier(PolicyTrustStore trust)
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal) { "schemaVersion", "purpose", "keyId", "payload", "signatureBase64Url" };

    public SignedPolicyResult Verify(ReadOnlyMemory<byte> envelopeUtf8, string expectedPurpose)
    {
        try
        {
            if (!SecurityResultCodes.IdentifierPattern().IsMatch(expectedPurpose)) return new(false, SecurityResultCodes.PurposeMismatch);
            using var document = StrictJson.Parse(envelopeUtf8, 64 * 1024);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactProperties(root, RootProperties)) return new(false, SecurityResultCodes.Malformed);
            if (root.GetProperty("schemaVersion").GetString() != "1.0.0") return new(false, SecurityResultCodes.Malformed);
            var purpose = root.GetProperty("purpose").GetString();
            var keyId = root.GetProperty("keyId").GetString();
            if (purpose != expectedPurpose || keyId is null) return new(false, SecurityResultCodes.PurposeMismatch);
            if (!trust.TryGet(keyId, purpose, out var key) || key.Revoked || !Ed25519SignatureVerifier.IsValidPublicKey(key.PublicKey.Span)) return new(false, SecurityResultCodes.UntrustedKey);
            var signature = DecodeBase64Url(root.GetProperty("signatureBase64Url").GetString(), Ed25519SignatureVerifier.SignatureSize);
            var unsigned = new JsonObject
            {
                ["schemaVersion"] = "1.0.0",
                ["purpose"] = purpose,
                ["keyId"] = keyId,
                ["payload"] = JsonNode.Parse(root.GetProperty("payload").GetRawText())
            };
            var canonical = JsonCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(unsigned.ToJsonString()));
            if (!Ed25519SignatureVerifier.Verify(key.PublicKey.Span, canonical, signature)) return new(false, SecurityResultCodes.SignatureInvalid);
            return new(true, SecurityResultCodes.Verified, root.GetProperty("payload").Clone(), keyId);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return new(false, SecurityResultCodes.Malformed);
        }
    }

    private static bool HasExactProperties(JsonElement value, IReadOnlySet<string> expected)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return actual.SetEquals(expected);
    }

    internal static byte[] DecodeBase64Url(string? text, int expectedLength)
    {
        if (text is null || text.Contains('=') || text.Any(char.IsWhiteSpace)) throw new FormatException();
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        var bytes = Convert.FromBase64String(normalized);
        if (bytes.Length != expectedLength) throw new FormatException();
        return bytes;
    }
}

public sealed record LicenseEntitlements(string LicenseId, string Tier, IReadOnlyList<string> Features, DateTimeOffset NotAfter);
public sealed record LicenseVerificationResult(bool Success, string Code, LicenseEntitlements? Entitlements = null);

public sealed class SignedLicenseVerifier(SignedPolicyVerifier verifier, TimeProvider? timeProvider = null)
{
    private static readonly HashSet<string> PayloadProperties = new(StringComparer.Ordinal) { "licenseId", "productId", "tier", "features", "issuedAt", "notAfter" };
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public LicenseVerificationResult Verify(ReadOnlyMemory<byte> envelopeUtf8)
    {
        var signed = verifier.Verify(envelopeUtf8, "license");
        if (!signed.Success || signed.Payload is not { } payload) return new(false, signed.Code);
        try
        {
            if (payload.ValueKind != JsonValueKind.Object || !Exact(payload, PayloadProperties)) return new(false, SecurityResultCodes.PayloadInvalid);
            var licenseId = payload.GetProperty("licenseId").GetString();
            var productId = payload.GetProperty("productId").GetString();
            var tier = payload.GetProperty("tier").GetString();
            if (licenseId is null || !SecurityResultCodes.IdentifierPattern().IsMatch(licenseId) || productId != "yuni-retro-toolkit" || tier is not ("standard" or "expert")) return new(false, SecurityResultCodes.PayloadInvalid);
            var issued = payload.GetProperty("issuedAt").GetDateTimeOffset();
            var notAfter = payload.GetProperty("notAfter").GetDateTimeOffset();
            if (issued >= notAfter || notAfter <= clock.GetUtcNow()) return new(false, SecurityResultCodes.Expired);
            var featureValues = payload.GetProperty("features").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (featureValues.Any(value => value is null || !SecurityResultCodes.IdentifierPattern().IsMatch(value)) || featureValues.Distinct(StringComparer.Ordinal).Count() != featureValues.Length) return new(false, SecurityResultCodes.PayloadInvalid);
            var features = featureValues.Select(value => value!).ToArray();
            return new(true, SecurityResultCodes.Verified, new(licenseId, tier, features, notAfter));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException) { return new(false, SecurityResultCodes.PayloadInvalid); }
    }

    private static bool Exact(JsonElement value, IReadOnlySet<string> expected) => value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
}

public sealed record UpdateArtifact(string Version, string Sha256, long Size, string RollbackVersion);
public sealed record UpdateVerificationResult(bool Success, string Code, UpdateArtifact? Artifact = null);

public sealed class SignedUpdateVerifier(SignedPolicyVerifier verifier)
{
    private static readonly HashSet<string> PayloadProperties = new(StringComparer.Ordinal) { "productId", "channel", "version", "minimumCurrentVersion", "artifactSha256", "artifactSize", "rollbackVersion" };

    public UpdateVerificationResult VerifyManifest(ReadOnlyMemory<byte> envelopeUtf8, Version currentVersion)
    {
        var signed = verifier.Verify(envelopeUtf8, "update");
        if (!signed.Success || signed.Payload is not { } payload) return new(false, signed.Code);
        try
        {
            if (payload.ValueKind != JsonValueKind.Object || !payload.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(PayloadProperties)) return new(false, SecurityResultCodes.PayloadInvalid);
            if (payload.GetProperty("productId").GetString() != "yuni-retro-toolkit" || payload.GetProperty("channel").GetString() != "stable") return new(false, SecurityResultCodes.PayloadInvalid);
            var versionText = payload.GetProperty("version").GetString();
            var minimumText = payload.GetProperty("minimumCurrentVersion").GetString();
            var rollbackText = payload.GetProperty("rollbackVersion").GetString();
            var hash = payload.GetProperty("artifactSha256").GetString();
            var size = payload.GetProperty("artifactSize").GetInt64();
            if (!Version.TryParse(versionText, out var version) || !Version.TryParse(minimumText, out var minimum) || !Version.TryParse(rollbackText, out _) || hash is null || !SecurityResultCodes.HashPattern().IsMatch(hash) || size <= 0) return new(false, SecurityResultCodes.PayloadInvalid);
            if (currentVersion < minimum || version <= currentVersion) return new(false, SecurityResultCodes.Incompatible);
            return new(true, SecurityResultCodes.Verified, new(versionText!, hash, size, rollbackText!));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException) { return new(false, SecurityResultCodes.PayloadInvalid); }
    }

    public static string VerifyArtifact(UpdateArtifact artifact, ReadOnlySpan<byte> bytes) =>
        bytes.Length == artifact.Size && Convert.ToHexStringLower(SHA256.HashData(bytes)) == artifact.Sha256 ? SecurityResultCodes.Verified : SecurityResultCodes.ArtifactMismatch;
}

public sealed record ExpectedComponent(string Name, string RelativePath, string Sha256, string? FileVersion = null);

public static class ExternalComponentVerifier
{
    public static string Verify(string root, ExpectedComponent expected)
    {
        try
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var path = Path.GetFullPath(Path.Combine(canonicalRoot, expected.RelativePath));
            if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(expected.RelativePath) || !File.Exists(path)) return SecurityResultCodes.ComponentMismatch;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return SecurityResultCodes.ComponentMismatch;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (hash != expected.Sha256) return SecurityResultCodes.ComponentMismatch;
            if (expected.FileVersion is not null && FileVersionInfo.GetVersionInfo(path).FileVersion != expected.FileVersion) return SecurityResultCodes.ComponentMismatch;
            return SecurityResultCodes.Verified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return SecurityResultCodes.ComponentMismatch; }
    }
}

public sealed record DiagnosticSnapshot(string AppVersion, string OsVersion, string PackVersion, string SignatureStatus, string IntegrityStatus, string ErrorCode, string ComponentVersion);

public static class DiagnosticReportService
{
    public static async Task SaveNewAsync(string outputPath, DiagnosticSnapshot snapshot, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        var finalPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(finalPath) ?? throw new ArgumentException("A destination directory is required.", nameof(outputPath));
        if (!Directory.Exists(directory) || File.Exists(finalPath)) throw new IOException("Choose a new diagnostic report filename.");
        var stagingPath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Create(snapshot, createdAt), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(stagingPath, finalPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch (IOException) { }
        }
    }

    public static byte[] Create(DiagnosticSnapshot snapshot, DateTimeOffset createdAt)
    {
        var safe = new
        {
            schemaVersion = "1.0.0",
            createdAt,
            appVersion = Token(snapshot.AppVersion, 32),
            osVersion = Token(snapshot.OsVersion, 96),
            packVersion = Token(snapshot.PackVersion, 32),
            signatureStatus = Status(snapshot.SignatureStatus),
            integrityStatus = Status(snapshot.IntegrityStatus),
            errorCode = ErrorCode(snapshot.ErrorCode),
            componentVersion = Token(snapshot.ComponentVersion, 96),
            privacy = new { automaticUpload = false, romIncluded = false, saveIncluded = false, pathsIncluded = false, userNameIncluded = false, secretsIncluded = false }
        };
        return JsonSerializer.SerializeToUtf8Bytes(safe, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Token(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(c => c is '\\' or '/' or ':' or '\r' or '\n' or '\0')) return "REDACTED";
        return value;
    }
    private static string Status(string value) => value is "VERIFIED" or "NOT_VERIFIED" or "FAILED" or "NOT_INSTALLED" ? value : "NOT_VERIFIED";
    private static string ErrorCode(string value) => SecurityResultCodes.ErrorCodePattern().IsMatch(value) ? value : "REDACTED";
}
