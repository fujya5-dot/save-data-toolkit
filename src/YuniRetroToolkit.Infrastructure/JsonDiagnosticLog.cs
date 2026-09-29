using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YuniRetroToolkit.Application;

namespace YuniRetroToolkit.Infrastructure;

public sealed class JsonDiagnosticLog(string directory) : IDiagnosticLog
{
    private readonly object gate = new();
    public void Write(string eventName, string message, Exception? error = null)
    {
        Directory.CreateDirectory(directory);
        var sanitized = Redact(message);
        var entry = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, eventName, message = sanitized, error = error?.GetType().Name });
        lock (gate) File.AppendAllText(Path.Combine(directory, $"yuni-{DateTime.UtcNow:yyyyMMdd}.jsonl"), entry + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string Redact(string value)
    {
        if (!value.Contains(':') && !value.Contains('\\')) return value;
        return "redacted:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }
}
