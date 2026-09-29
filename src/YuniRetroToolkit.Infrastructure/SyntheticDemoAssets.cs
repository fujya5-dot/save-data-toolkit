using System.Security.Cryptography;

namespace YuniRetroToolkit.Infrastructure;

public sealed record SyntheticDemoRun(string SavePath, string IdentityPath);

public static class SyntheticDemoAssets
{
    public const string SaveFileName = "synthetic-demo.srm";
    public const string IdentityFileName = "synthetic-demo.identity";
    public const string SaveSha256 = "106bd535d0575ab6e52cd87749dce6c7017ad8e6176d43a2e9bd048dd62454e7";
    public const string IdentitySha256 = "bb391415c05e39d77ca17381d3be3f7d0cd5e5332e5a579311adaa0aa62106e9";

    public static SyntheticDemoRun Materialize(string sourceDirectory, string destinationRoot)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationRoot);
        EnsureSafeDirectory(source, false);
        EnsureSafeDirectory(destination, true);
        var save = ReadVerified(Path.Combine(source, SaveFileName), 64, SaveSha256);
        var identity = ReadVerified(Path.Combine(source, IdentityFileName), 32, IdentitySha256);
        var runDirectory = Path.Combine(destination, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        WriteNew(Path.Combine(runDirectory, SaveFileName), save);
        WriteNew(Path.Combine(runDirectory, IdentityFileName), identity);
        return new(Path.Combine(runDirectory, SaveFileName), Path.Combine(runDirectory, IdentityFileName));
    }

    private static byte[] ReadVerified(string path, int expectedLength, string expectedHash)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length != expectedLength || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Synthetic Demo asset is missing or unsafe.");
        var bytes = File.ReadAllBytes(info.FullName);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Synthetic Demo asset integrity check failed.");
        return bytes;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static void EnsureSafeDirectory(string path, bool create)
    {
        if (create) Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Synthetic Demo directory is missing or unsafe.");
    }
}
