using System.Reflection;
using YuniRetroToolkit.Application;
using YuniRetroToolkit.Domain;
using YuniRetroToolkit.GameDefinitions;
using YuniRetroToolkit.Infrastructure;

namespace YuniRetroToolkit.Tests;

internal static partial class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "yuni-public-tests-" + Guid.NewGuid().ToString("N"));
    private static int passed;
    private static int failed;

    public static async Task<int> Main()
    {
        try
        {
            foreach (var name in new[]
            {
                "YuniRetroToolkit.Domain",
                "YuniRetroToolkit.Application",
                "YuniRetroToolkit.Infrastructure",
                "YuniRetroToolkit.GameDefinitions",
                "YuniRetroToolkit.Backup"
            })
                _ = Assembly.Load(new AssemblyName(name));
        }
        catch (FileLoadException error) when (error.HResult == unchecked((int)0x800711C7))
        {
            Console.WriteLine("BLOCKED PRECHECK: Windows Application Control denied a Core assembly (0x800711C7). No product tests ran.");
            return 2;
        }

        Directory.CreateDirectory(Root);
        try
        {
            await Run("DEMO-01 Synthetic materialization and fixture hashes", DemoAssetsExactAndIsolated);
            await Run("DEMO-02 Signed declarative pack", DemoPackSignedDeclarativeOnly);
            await Run("DEMO-03 Load, validate, export, reload", DemoPackEndToEnd);
            await Run("DEMO-04 Change preview", DemoChangePreview);
            await Run("DEMO-05 Invalid value is rejected", DemoInvalidValueRejected);
            await Run("DEMO-06 Pack tampering is rejected", DemoPackTamperRejected);
            await Run("DEMO-07 Asset tampering is rejected", DemoAssetTamperRejected);
            await Run("DEMO-08 Active pack tampering is rejected", DemoActivePackTamperRejected);
            await Run("GSF-01 Unknown file opens safely", GsfUnknownOpen);
            await Run("GSF-02 File information", GsfFileInfo);
            await Run("GSF-03 Fingerprint", GsfFingerprint);
            await Run("GSF-04 Unsupported state", GsfUnsupportedState);
            await Run("GSF-05 Read-only original", GsfReadOnly);
            await Run("GSF-06 Edit is rejected", GsfEditRejected);
            await Run("GSF-07 Change set is rejected", GsfChangeSetRejected);
            await Run("GSF-08 Verified backup", GsfBackup);
            await Run("GSF-09 Corrupt backup is rejected", GsfBackupHashVerification);
            await Run("GSF-10 Restore as new protects original", GsfRestoreAsNew);
            await Run("GSF-11 Invalid input fails closed", GsfInvalidFile);
        }
        finally
        {
            Directory.Delete(Root, recursive: true);
        }

        Console.WriteLine($"RESULT pass={passed} fail={failed} total={passed + failed}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}");
        }
    }

    private static async Task DemoChangePreview()
    {
        var context = await OpenDemoContext("demo-change-preview");
        Assert(context.Session.Apply(OfficialDemoPack.FieldId, 7).IsValid);
        var preview = context.Session.Preview();
        Assert(preview.Fields.Count == 1 && preview.Fields[0].OriginalValue == 1 && preview.Fields[0].NewValue == 7);
        Assert(preview.Bytes.Any(change => change.Offset == 1) && preview.Bytes.Any(change => change.Offset == 2));
        Assert(context.Session.Validate().IsValid);
        Assert(File.ReadAllBytes(context.OriginalPath)[1] == 1);
    }

    private static string Repo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string SchemaDirectory() => Path.Combine(Repo(), "schemas");
    private static string NewFile(string name, byte[] bytes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Assert(bool condition, string message = "Assertion failed.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Task Done() => Task.CompletedTask;

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void ThrowsSync<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class NullLog : IDiagnosticLog
    {
        public void Write(string eventName, string message, Exception? error = null) { }
    }
}
