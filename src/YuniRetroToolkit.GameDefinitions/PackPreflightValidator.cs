using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace YuniRetroToolkit.GameDefinitions;

public sealed record PackPreflightResult(bool SafeToInspect, bool Active, string Status, IReadOnlyList<string> Problems);

public static class PackPreflightValidator
{
    private const long MaxArchiveBytes = PackInstaller.MaximumArchiveBytes;
    private const long MaxExpandedBytes = PackInstaller.MaximumExpandedBytes;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".json" };
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".js", ".vbs", ".msi", ".scr", ".zip", ".7z", ".rar" };

    public static PackPreflightResult Inspect(string path, string? schemaDirectory = null)
    {
        var problems = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return new(false, false, "REJECTED", ["Only an absolute local archive path is allowed."]);
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxArchiveBytes) return new(false, false, "QUARANTINED", ["Archive size is outside policy."]);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return new(false, false, "REJECTED", ["Linked archive paths are forbidden."]);
            using var archive = ZipFile.OpenRead(fullPath);
            if (archive.Entries.Count is 0 or > PackInstaller.MaximumEntries) problems.Add("Entry count is outside policy.");
            long expanded = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > MaxExpandedBytes || expanded > MaxExpandedBytes - entry.Length) problems.Add("Expanded archive is too large.");
                else expanded += entry.Length;
                var normalized = entry.FullName.Replace('\\', '/');
                try { PackInstaller.ValidateEntryPath(entry.FullName); }
                catch (PackInstaller.PackException) { problems.Add("Unsafe archive entry path."); }
                if (!names.Add(normalized)) problems.Add("Duplicate or case-conflicting archive entry.");
                if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(segment => segment == "..")) problems.Add("Path traversal entry.");
                var extension = Path.GetExtension(normalized);
                if (ExecutableExtensions.Contains(extension) || (!string.IsNullOrEmpty(extension) && !AllowedExtensions.Contains(extension))) problems.Add($"Forbidden entry type: {extension}");
                if (entry.Length > StrictJson.MaximumDefinitionBytes && extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) problems.Add("JSON entry is too large.");
            }
            if (expanded > MaxExpandedBytes) problems.Add("Expanded archive is too large.");
            if (problems.Count == 0 && schemaDirectory is not null) ValidateDocuments(archive, schemaDirectory, problems);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            problems.Add("Invalid archive: " + (message.Length <= 512 ? message : message[..512]));
        }
        return new(problems.Count == 0, false, problems.Count == 0 ? "QUARANTINED_PREFLIGHT_ONLY" : "REJECTED", problems);
    }

    private static void ValidateDocuments(ZipArchive archive, string schemaDirectory, List<string> problems)
    {
        var entries = archive.Entries.ToDictionary(entry => entry.FullName.Replace('\\', '/'), StringComparer.Ordinal);
        if (!entries.TryGetValue("manifest.json", out var manifestEntry)) { problems.Add("Required manifest.json is missing."); return; }
        using var validator = new SchemaDocumentValidator(schemaDirectory);
        var manifestBytes = ReadJson(manifestEntry);
        var manifestResult = validator.Validate(SchemaDocumentKind.PackManifest, manifestBytes);
        if (!manifestResult.Accepted)
        {
            problems.AddRange(manifestResult.Problems.Select(problem => "Manifest: " + problem));
            return;
        }

        using var manifest = StrictJson.Parse(manifestBytes);
        var declaresSigning = manifest.RootElement.TryGetProperty("signing", out _);
        var hasSignature = entries.ContainsKey("signature.json");
        if (declaresSigning != hasSignature) problems.Add("Manifest signing declaration and signature.json presence conflict.");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in manifest.RootElement.GetProperty("contents").EnumerateArray())
        {
            var contentPath = content.GetProperty("path").GetString()!;
            declared.Add(contentPath);
            if (!entries.TryGetValue(contentPath, out var definitionEntry)) { problems.Add("Declared definition is missing: " + contentPath); continue; }
            var bytes = ReadJson(definitionEntry);
            if (definitionEntry.Length != content.GetProperty("size").GetInt64()) problems.Add("Declared size mismatch: " + contentPath);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!hash.Equals(content.GetProperty("sha256").GetString(), StringComparison.Ordinal)) problems.Add("Declared hash mismatch: " + contentPath);
            var definitionResult = validator.Validate(SchemaDocumentKind.GameDefinition, bytes);
            if (!definitionResult.Accepted) problems.AddRange(definitionResult.Problems.Select(problem => contentPath + ": " + problem));
            else
            {
                using var definition = StrictJson.Parse(bytes);
                if (!string.Equals(definition.RootElement.GetProperty("definitionId").GetString(), content.GetProperty("definitionId").GetString(), StringComparison.Ordinal) ||
                    !string.Equals(definition.RootElement.GetProperty("definitionVersion").GetString(), content.GetProperty("definitionVersion").GetString(), StringComparison.Ordinal))
                    problems.Add("Declared definition identity mismatch: " + contentPath);
            }
        }

        foreach (var entry in entries.Keys.Where(name => name.EndsWith(".yrt-game.json", StringComparison.OrdinalIgnoreCase)))
            if (!declared.Contains(entry)) problems.Add("Undeclared definition entry: " + entry);

        if (hasSignature)
        {
            var signatureEntry = entries["signature.json"];
            var signatureResult = validator.Validate(SchemaDocumentKind.PackSignature, ReadJson(signatureEntry));
            if (!signatureResult.Accepted) problems.AddRange(signatureResult.Problems.Select(problem => "Signature: " + problem));
        }
    }

    private static byte[] ReadJson(ZipArchiveEntry entry)
    {
        if (entry.Length is <= 0 or > StrictJson.MaximumDefinitionBytes) throw new InvalidDataException("JSON entry size is outside policy.");
        using var source = entry.Open();
        using var destination = new MemoryStream(checked((int)entry.Length));
        var buffer = new byte[81920];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (destination.Length + read > StrictJson.MaximumDefinitionBytes) throw new InvalidDataException("JSON entry exceeds the decompression limit.");
            destination.Write(buffer, 0, read);
        }
        if (destination.Length != entry.Length) throw new InvalidDataException("ZIP entry length mismatch.");
        return destination.ToArray();
    }
}
