using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Domain;

namespace YuniRetroToolkit.Infrastructure;

public sealed class LocalSaveFileGateway : ISaveFileGateway
{
    private const long MaxInputBytes = 16 * 1024 * 1024;

    public async Task<OriginalFile> ReadOriginalAsync(string path, CancellationToken cancellationToken = default)
    {
        var (fingerprint, bytes) = await ReadAsync(path, 1024 * 1024, cancellationToken);
        if (bytes.Length == 0) throw new InvalidDataException("空のセーブファイルは開けません。");
        return new(fingerprint, bytes);
    }

    public async Task<FileFingerprint> FingerprintAsync(string path, CancellationToken cancellationToken = default)
    {
        var (fingerprint, _) = await ReadAsync(path, MaxInputBytes, cancellationToken);
        return fingerprint;
    }

    public async Task<FileFingerprint> WriteNewAtomicallyAsync(string outputPath, ReadOnlyMemory<byte> content, IReadOnlyCollection<FileFingerprint> protectedFiles, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0 || content.Length > 1024 * 1024) throw new InvalidDataException("書き出しサイズが許可範囲外です。");
        var fullOutput = NormalizeNewPath(outputPath);
        var parent = Path.GetDirectoryName(fullOutput) ?? throw new IOException("出力先フォルダーを特定できません。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("出力先フォルダーがありません。");

        foreach (var item in protectedFiles)
            if (PathsEqual(item.FullPath, fullOutput)) throw new IOException("Original またはバックアップと同じ場所には書き出せません。");

        if (File.Exists(fullOutput))
        {
            var existing = await FingerprintAsync(fullOutput, cancellationToken);
            if (protectedFiles.Any(p => IsSameFile(p, existing))) throw new IOException("Original またはバックアップの別名には書き出せません。");
            throw new IOException("既存ファイルは上書きしません。新しい名前を選んでください。");
        }

        var staging = Path.Combine(parent, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.yrt-staging");
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            var staged = await FingerprintAsync(staging, cancellationToken);
            if (staged.Length != content.Length || staged.Sha256 != FileFingerprint.Hash(content.Span)) throw new IOException("一時ファイルの再検証に失敗しました。");
            File.Move(staging, fullOutput, false);
            var result = await FingerprintAsync(fullOutput, cancellationToken);
            if (result.Sha256 != staged.Sha256) throw new IOException("書き出し後の再検証に失敗しました。");
            return result;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static async Task<(FileFingerprint Fingerprint, byte[] Bytes)> ReadAsync(string path, long maximum, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("ファイル名が空です。", nameof(path));
        var full = Path.GetFullPath(path);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException("ファイルサイズが許可範囲外です。");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        var identity = GetIdentity(stream.SafeFileHandle);
        return (new(full, bytes.Length, FileFingerprint.Hash(bytes), identity.VolumeSerial, identity.FileIndex), bytes);
    }

    private static string NormalizeNewPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("出力ファイル名が空です。", nameof(path));
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new ArgumentException("出力ファイル名が不正です。", nameof(path));
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is not null)
        {
            var info = new DirectoryInfo(parent);
            if (info.Exists && info.ResolveLinkTarget(true) is { } resolved) full = Path.Combine(resolved.FullName, Path.GetFileName(full));
        }
        return full;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool IsSameFile(FileFingerprint left, FileFingerprint right) =>
        left.VolumeSerial is not null && right.VolumeSerial == left.VolumeSerial && left.FileIndex is not null && right.FileIndex == left.FileIndex;

    private static (uint VolumeSerial, ulong FileIndex) GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("ファイル識別情報を取得できません。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
