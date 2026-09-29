using System.Collections.Frozen;
using System.Security.Cryptography;

namespace YuniRetroToolkit.GameDefinitions;

public sealed record TrustedPackKey(string KeyId, string Publisher, ReadOnlyMemory<byte> PublicKey, bool Revoked = false, IReadOnlySet<string>? PublisherAliases = null)
{
    public bool AllowsPublisher(string publisher)
        => string.Equals(Publisher, publisher, StringComparison.Ordinal) || PublisherAliases?.Contains(publisher) == true;
}

public sealed class PackTrustStore
{
    private readonly IReadOnlyDictionary<string, TrustedPackKey> keys;

    public PackTrustStore(IEnumerable<TrustedPackKey> trustedKeys)
    {
        var validated = new Dictionary<string, TrustedPackKey>(StringComparer.Ordinal);
        foreach (var item in trustedKeys)
        {
            var keyBytes = item.PublicKey.ToArray();
            var aliases = (item.PublisherAliases ?? new HashSet<string>()).ToFrozenSet(StringComparer.Ordinal);
            var expectedId = GetKeyId(keyBytes);
            if (!string.Equals(item.KeyId, expectedId, StringComparison.Ordinal) || !Ed25519SignatureVerifier.IsValidPublicKey(keyBytes) ||
                string.IsNullOrWhiteSpace(item.Publisher) || aliases.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Trusted Ed25519 public key metadata is invalid.", nameof(trustedKeys));
            if (!validated.TryAdd(item.KeyId, item with { PublicKey = keyBytes, PublisherAliases = aliases }))
                throw new ArgumentException("Duplicate trusted key ID.", nameof(trustedKeys));
        }
        keys = validated;
    }

    public bool TryGet(string keyId, out TrustedPackKey key) => keys.TryGetValue(keyId, out key!);
    public int Count => keys.Count;

    public static string GetKeyId(ReadOnlySpan<byte> publicKey)
        => "ed25519:" + Convert.ToHexStringLower(SHA256.HashData(publicKey));
}

public static class OfficialPackTrustStore
{
    public const string ProductionKeyId = "ed25519:b6e7ef865fd164cac6ace80eeb8224c6e635c3a99fff5cb422cd195316848062";
    public const string ProductionPublicKeyHex = "b96a5f3408641d601a668572ec5e7cb981a457b3e8a8fd38d1f9c6bdaf6b6c94";

    // Verification-only trust root. The corresponding private key is DPAPI-protected outside this repository
    // and is never loaded by the client or Pack installer.
    public static PackTrustStore Create() => new([
        new TrustedPackKey(ProductionKeyId, "Yuni", Convert.FromHexString(ProductionPublicKeyHex), PublisherAliases: new HashSet<string>(StringComparer.Ordinal) { "YuniWorks" })
    ]);
}
