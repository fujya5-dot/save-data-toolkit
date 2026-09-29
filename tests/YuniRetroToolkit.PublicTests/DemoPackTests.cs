using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Backup;
using YuniRetroToolkit.Domain;
using YuniRetroToolkit.GameDefinitions;
using YuniRetroToolkit.Infrastructure;

namespace YuniRetroToolkit.Tests;

internal static partial class Program
{
    private static string DemoAssetsDirectory() => Path.Combine(Repo(), "demo", "assets");
    private static string DemoPackArchive() => Path.Combine(Repo(), "release", "official-packs", OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion, OfficialDemoPack.ArchiveFileName);
    private static Task DemoAssetsExactAndIsolated()
    {
        var sourceSave = Path.Combine(DemoAssetsDirectory(), SyntheticDemoAssets.SaveFileName);
        var sourceIdentity = Path.Combine(DemoAssetsDirectory(), SyntheticDemoAssets.IdentityFileName);
        var saveBefore = File.ReadAllBytes(sourceSave);
        var identityBefore = File.ReadAllBytes(sourceIdentity);
        Assert(Hash(saveBefore) == SyntheticDemoAssets.SaveSha256 && Hash(identityBefore) == SyntheticDemoAssets.IdentitySha256);

        var first = SyntheticDemoAssets.Materialize(DemoAssetsDirectory(), Path.Combine(Root, "demo-materialized"));
        var second = SyntheticDemoAssets.Materialize(DemoAssetsDirectory(), Path.Combine(Root, "demo-materialized"));
        Assert(first.SavePath != second.SavePath && first.IdentityPath != second.IdentityPath);
        Assert(Hash(File.ReadAllBytes(first.SavePath)) == SyntheticDemoAssets.SaveSha256);
        Assert(Hash(File.ReadAllBytes(first.IdentityPath)) == SyntheticDemoAssets.IdentitySha256);
        Assert(File.ReadAllBytes(sourceSave).SequenceEqual(saveBefore) && File.ReadAllBytes(sourceIdentity).SequenceEqual(identityBefore));
        return Done();
    }

    private static Task DemoPackSignedDeclarativeOnly()
    {
        using (var archive = ZipFile.OpenRead(DemoPackArchive()))
        {
            var names = archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
            Assert(names.SequenceEqual(new[] { "definitions/yuni.demo.synthetic.yrt-game.json", "manifest.json", "signature.json" }));
            Assert(archive.Entries.All(entry => entry.Length > 0 && Path.GetExtension(entry.FullName) == ".json"));
            using var validator = new SchemaDocumentValidator(SchemaDirectory());
            foreach (var (path, kind) in new[]
            {
                ("manifest.json", SchemaDocumentKind.PackManifest),
                ("signature.json", SchemaDocumentKind.PackSignature),
                ("definitions/yuni.demo.synthetic.yrt-game.json", SchemaDocumentKind.GameDefinition)
            })
            {
                using var stream = archive.GetEntry(path)!.Open();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                var validation = validator.Validate(kind, bytes.ToArray());
                Assert(validation.Accepted, path + ": " + string.Join("; ", validation.Problems));
                if (kind == SchemaDocumentKind.GameDefinition)
                {
                    var unauthorized = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes.ToArray()).Replace("yuni.demo.synthetic", "evil.demo.synthetic", StringComparison.Ordinal));
                    var rejected = validator.Validate(kind, unauthorized);
                    Assert(!rejected.Accepted && rejected.Problems.Any(problem => problem.Contains("reserved", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        var root = Path.Combine(Root, "demo-pack-signed");
        var installer = new PackInstaller(root, SchemaDirectory(), OfficialPackTrustStore.Create(), "0.1.0");
        var installed = installer.Install(new(DemoPackArchive(), OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion));
        Assert(installed.Success && installed.Pack is not null, installed.Code);
        var activePack = installed.Pack ?? throw new InvalidOperationException("Demo Pack status was missing.");
        Assert(activePack.Publisher == "YuniWorks" && activePack.Signature == "Verified");
        Assert(installer.GetActive(OfficialDemoPack.PackageId).Success);

        var mutableAliases = new HashSet<string>(StringComparer.Ordinal) { "YuniWorks" };
        var snapshottedTrust = new PackTrustStore([new TrustedPackKey(
            OfficialPackTrustStore.ProductionKeyId,
            "Yuni",
            Convert.FromHexString(OfficialPackTrustStore.ProductionPublicKeyHex),
            PublisherAliases: mutableAliases)]);
        mutableAliases.Add("Untrusted Publisher");
        Assert(snapshottedTrust.TryGet(OfficialPackTrustStore.ProductionKeyId, out var snapshottedKey));
        Assert(snapshottedKey.AllowsPublisher("YuniWorks") && !snapshottedKey.AllowsPublisher("Untrusted Publisher"));
        return Done();
    }

    private static async Task DemoPackEndToEnd()
    {
        var dir = Path.Combine(Root, "demo-e2e");
        var packRoot = Path.Combine(dir, "pack-registry");
        var installer = new PackInstaller(packRoot, SchemaDirectory(), OfficialPackTrustStore.Create(), "0.1.0");
        var installed = installer.Install(new(DemoPackArchive(), OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion));
        Assert(installed.Success, installed.Code);

        var discovery = installer.DiscoverActiveDefinitionDirectories();
        Assert(discovery.RejectedPackIds.Count == 0 && discovery.DefinitionDirectories.Count == 1);
        var registry = new GameDefinitionRegistry(Path.Combine(dir, "no-builtins"), Path.Combine(SchemaDirectory(), "game-definition-v1.schema.json"));
        registry.SetAdditionalDefinitionDirectories(discovery.DefinitionDirectories);
        await registry.ReloadAsync();
        Assert(registry.Rejections.Count == 0 && registry.ActiveDefinitions.Single().DefinitionId == OfficialDemoPack.DefinitionId);

        var run = SyntheticDemoAssets.Materialize(DemoAssetsDirectory(), Path.Combine(dir, "runs"));
        var originalHash = Hash(File.ReadAllBytes(run.SavePath));
        var backups = new ContentAddressedBackupStore(Path.Combine(dir, "backups"));
        var workflow = new SaveWorkflowService(new LocalSaveFileGateway(), backups, registry, new NullLog());
        var opened = await workflow.OpenAsync(run.SavePath, run.IdentityPath);
        Assert(opened.Session.ReadField(OfficialDemoPack.FieldId) == 1);
        Assert((await backups.ReadVerifiedAsync(opened.BaselineBackupId)).Span.SequenceEqual(File.ReadAllBytes(run.SavePath)));
        Assert(opened.Session.Apply(OfficialDemoPack.FieldId, 7).IsValid && opened.Session.Validate().IsValid);

        var output = Path.Combine(dir, "synthetic-level7.srm");
        var exported = await workflow.ExportAsync(opened.Session, output);
        Assert((await backups.ReadVerifiedAsync(exported.VerifiedBackupId)).Span.SequenceEqual(File.ReadAllBytes(output)));
        Assert(Hash(File.ReadAllBytes(run.SavePath)) == originalHash, "Synthetic Original changed.");
        var reloaded = await workflow.OpenAsync(output, run.IdentityPath);
        Assert(reloaded.Session.ReadField(OfficialDemoPack.FieldId) == 7 && reloaded.Session.Validate().IsValid);
        Assert(File.ReadAllBytes(output)[1] == 7 && File.ReadAllBytes(output)[2] == 7 && File.ReadAllBytes(output)[63] == 68);
    }

    private static async Task DemoInvalidValueRejected()
    {
        var context = await OpenDemoContext("demo-invalid");
        var before = context.Session.WorkingBytes.ToArray();
        Assert(!context.Session.Apply(OfficialDemoPack.FieldId, 2).IsValid);
        Assert(context.Session.WorkingBytes.Span.SequenceEqual(before));
    }

    private static Task DemoPackTamperRejected()
    {
        var tampered = Path.Combine(Root, "demo-pack-tampered.yrtpack");
        using (var source = ZipFile.OpenRead(DemoPackArchive()))
        using (var target = ZipFile.Open(tampered, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var input = entry.Open();
                using var output = created.Open();
                if (entry.FullName == "manifest.json")
                {
                    using var memory = new MemoryStream();
                    input.CopyTo(memory);
                    var bytes = memory.ToArray();
                    bytes[^2] ^= 1;
                    output.Write(bytes);
                }
                else input.CopyTo(output);
            }
        }
        var installer = new PackInstaller(Path.Combine(Root, "demo-pack-tamper-root"), SchemaDirectory(), OfficialPackTrustStore.Create(), "0.1.0");
        var result = installer.Install(new(tampered, OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion));
        Assert(!result.Success && result.Pack is null);
        Assert(installer.DiscoverActiveDefinitionDirectories().DefinitionDirectories.Count == 0);
        return Done();
    }

    private static Task DemoAssetTamperRejected()
    {
        var source = DemoAssetsDirectory();
        var tampered = Path.Combine(Root, "demo-assets-tampered");
        Directory.CreateDirectory(tampered);
        File.Copy(Path.Combine(source, SyntheticDemoAssets.SaveFileName), Path.Combine(tampered, SyntheticDemoAssets.SaveFileName));
        File.Copy(Path.Combine(source, SyntheticDemoAssets.IdentityFileName), Path.Combine(tampered, SyntheticDemoAssets.IdentityFileName));
        var bytes = File.ReadAllBytes(Path.Combine(tampered, SyntheticDemoAssets.SaveFileName));
        bytes[1] ^= 1;
        File.WriteAllBytes(Path.Combine(tampered, SyntheticDemoAssets.SaveFileName), bytes);
        ThrowsSync<InvalidDataException>(() => SyntheticDemoAssets.Materialize(tampered, Path.Combine(Root, "rejected-demo-run")));
        return Done();
    }

    private static Task DemoActivePackTamperRejected()
    {
        var root = Path.Combine(Root, "demo-active-tamper");
        var installer = new PackInstaller(root, SchemaDirectory(), OfficialPackTrustStore.Create(), "0.1.0");
        Assert(installer.Install(new(DemoPackArchive(), OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion)).Success);
        var before = installer.DiscoverActiveDefinitionDirectories();
        Assert(before.DefinitionDirectories.Count == 1 && before.RejectedPackIds.Count == 0);
        var definition = Directory.GetFiles(before.DefinitionDirectories[0], "*.yrt-game.json").Single();
        File.AppendAllText(definition, " ");
        var after = installer.DiscoverActiveDefinitionDirectories();
        Assert(after.DefinitionDirectories.Count == 0 && after.RejectedPackIds.SequenceEqual(new[] { OfficialDemoPack.PackageId }));
        return Done();
    }

    private static async Task<(WorkingCopySession Session, string OriginalPath)> OpenDemoContext(string name)
    {
        var dir = Path.Combine(Root, name);
        var installer = new PackInstaller(Path.Combine(dir, "packs"), SchemaDirectory(), OfficialPackTrustStore.Create(), "0.1.0");
        Assert(installer.Install(new(DemoPackArchive(), OfficialDemoPack.PackageId, OfficialDemoPack.PackageVersion)).Success);
        var registry = new GameDefinitionRegistry(Path.Combine(dir, "no-builtins"), Path.Combine(SchemaDirectory(), "game-definition-v1.schema.json"));
        registry.SetAdditionalDefinitionDirectories(installer.DiscoverActiveDefinitionDirectories().DefinitionDirectories);
        await registry.ReloadAsync();
        var run = SyntheticDemoAssets.Materialize(DemoAssetsDirectory(), Path.Combine(dir, "runs"));
        var flow = new SaveWorkflowService(new LocalSaveFileGateway(), new ContentAddressedBackupStore(Path.Combine(dir, "backups")), registry, new NullLog());
        var opened = await flow.OpenAsync(run.SavePath, run.IdentityPath);
        return (opened.Session, run.SavePath);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
