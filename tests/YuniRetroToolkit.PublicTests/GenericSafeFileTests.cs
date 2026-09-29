using YuniRetroToolkit.App;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Backup;
using YuniRetroToolkit.Domain;
using YuniRetroToolkit.Infrastructure;

namespace YuniRetroToolkit.Tests;

internal static partial class Program
{
    private static (GenericSafeFileService Service, IBackupStore Store, string Path, byte[] Bytes)
        GenericFixture(string name)
    {
        byte[] bytes = [3, 17, 44, 0, 255, 9];
        var path = NewFile($"gsf-{name}.dat", bytes);
        IBackupStore store = new InMemoryGenericBackupStore();
        return (new(new LocalSaveFileGateway(), store), store, path, bytes);
    }

    private static (GenericSafeFileService Service, IBackupStore Store, string Path, byte[] Bytes)
        RealGenericFixture(string name)
    {
        byte[] bytes = [3, 17, 44, 0, 255, 9];
        var path = NewFile($"gsf-{name}.dat", bytes);
        IBackupStore store = new ContentAddressedBackupStore(System.IO.Path.Combine(Root, $"gsf-{name}-backup"));
        return (new(new LocalSaveFileGateway(), store), store, path, bytes);
    }

    private static async Task GsfUnknownOpen()
    {
        var fixture = GenericFixture("open");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(result.SupportStatus == "Unknown / Unsupported" && result.BackupId.Length > 0);
    }
    private static async Task GsfFileInfo()
    {
        var fixture = GenericFixture("info");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(result.FileName == "gsf-info.dat" && result.Source.Length == fixture.Bytes.Length);
    }
    private static async Task GsfFingerprint()
    {
        var fixture = GenericFixture("fingerprint");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(result.Source.Sha256 == FileFingerprint.Hash(fixture.Bytes));
    }
    private static async Task GsfUnsupportedState()
    {
        var fixture = GenericFixture("unsupported");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(result.SupportStatus == "Unknown / Unsupported");
    }
    private static async Task GsfReadOnly()
    {
        var fixture = GenericFixture("readonly");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(!result.CanEdit && !result.CanCreateChangeSet && File.ReadAllBytes(fixture.Path).SequenceEqual(fixture.Bytes));
    }
    private static async Task GsfEditRejected()
    {
        var fixture = GenericFixture("no-edit");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(!result.CanEdit && !UiSafety.CanEdit(null) && !UiSafety.CanApply(null, "unknown.field"));
    }
    private static async Task GsfChangeSetRejected()
    {
        var fixture = GenericFixture("no-changeset");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert(!result.CanCreateChangeSet && !UiSafety.CanExport(null, null, ""));
    }
    private static async Task GsfBackup()
    {
        var fixture = RealGenericFixture("backup");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert((await fixture.Store.ReadVerifiedAsync(result.BackupId)).Span.SequenceEqual(fixture.Bytes));
    }
    private static async Task GsfBackupHashVerification()
    {
        var fixture = RealGenericFixture("hash-backup");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        Assert((await fixture.Store.ReadVerifiedAsync(result.BackupId)).Span.SequenceEqual(fixture.Bytes));
        var blob = Directory.GetFiles(System.IO.Path.Combine(Root, "gsf-hash-backup-backup", "blobs")).Single();
        File.WriteAllBytes(blob, [0, 0, 0, 0, 0, 0]);
        await Throws<InvalidDataException>(() => fixture.Service.RestoreAsNewAsync(result,
            System.IO.Path.Combine(Root, "gsf-corrupt-restored.dat")));
        Assert(!File.Exists(System.IO.Path.Combine(Root, "gsf-corrupt-restored.dat")));
    }
    private static async Task GsfRestoreAsNew()
    {
        var fixture = RealGenericFixture("restore");
        var result = await fixture.Service.OpenAsync(fixture.Path);
        var output = System.IO.Path.Combine(Root, "gsf-restore-copy.dat");
        var restored = await fixture.Service.RestoreAsNewAsync(result, output);
        Assert(restored.Sha256 == result.Source.Sha256 && File.ReadAllBytes(output).SequenceEqual(fixture.Bytes));
        Assert(File.ReadAllBytes(fixture.Path).SequenceEqual(fixture.Bytes));
        await Throws<IOException>(() => fixture.Service.RestoreAsNewAsync(result, fixture.Path));
    }
    private static async Task GsfInvalidFile()
    {
        var store = new InMemoryGenericBackupStore();
        var service = new GenericSafeFileService(new LocalSaveFileGateway(), store);
        await Throws<InvalidDataException>(() => service.OpenAsync(NewFile("gsf-empty.dat", [])));
        await Throws<InvalidDataException>(() => service.OpenAsync(NewFile("gsf-oversize.dat", new byte[1024 * 1024 + 1])));
        Assert(store.Count == 0);
    }
    private sealed class InMemoryGenericBackupStore : IBackupStore
    {
        private readonly Dictionary<string, byte[]> items = new(StringComparer.Ordinal);
        public int Count => items.Count;
        public Task<string> CreateAsync(string kind, OriginalFile file, CancellationToken cancellationToken = default) =>
            CreateAsync(kind, file, file.Fingerprint, cancellationToken);
        public Task<string> CreateAsync(string kind, OriginalFile file, FileFingerprint source, CancellationToken cancellationToken = default)
        {
            var bytes = file.Snapshot().ToArray();
            Assert(source.Length == bytes.Length && source.Sha256 == FileFingerprint.Hash(bytes));
            var id = Guid.NewGuid().ToString("N");
            items.Add(id, bytes);
            return Task.FromResult(id);
        }
        public Task<ReadOnlyMemory<byte>> ReadVerifiedAsync(string backupId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<byte>>(items[backupId].ToArray());
    }
}
