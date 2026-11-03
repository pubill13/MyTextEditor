using System.Diagnostics;
using System.IO;

namespace MyTextEditor.Services;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTextEditor", "Diagnostics");
    public static void Error(string stage, Exception error)
    {
        // Messages can include user text or paths. Log only type and frames.
        Write(stage + " " + error.GetType().FullName + Environment.NewLine + error.StackTrace);
    }
    public static void Measure(string stage, double elapsedMs, string? detail = null) =>
        Write($"{stage} elapsedMs={elapsedMs:F1} {detail}");
    private static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, $"session-{Environment.ProcessId}.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {text}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Trace.WriteLine($"Diagnostic logging failed: {exception.GetType().Name}"); }
    }
}
