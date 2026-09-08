using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using Application = System.Windows.Application;

internal static class Program
{
    private const int TargetCharacters = 30_000_000;

    [STAThread]
    private static int Main()
    {
        var path = Path.Combine(Path.GetTempPath(), "MyTextEditor-30m-utf8.log");
        EnsureFixture(path);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        VerifyEditorRoundTrip();
        var times = new List<double>();
        for (var run = 1; run <= 3; run++)
        {
            var host = new ScintillaEditorHost();
            var window = new Window { Width = 800, Height = 500, Content = host, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
            window.Show();
            var stopwatch = Stopwatch.StartNew();
            var buffer = new DocumentFileService().LoadBufferAsync(path).GetAwaiter().GetResult();
            host.LoadUtf8(buffer.Utf8Buffer);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            stopwatch.Stop();
            times.Add(stopwatch.Elapsed.TotalSeconds);
            Console.WriteLine($"run {run}: {stopwatch.Elapsed.TotalSeconds:F3}s ({buffer.Utf8Buffer.Length:N0} bytes, {host.LineCount:N0} lines)");
            window.Close();
        }
        application.Shutdown();
        times.Sort();
        var median = times[1];
        Console.WriteLine($"median: {median:F3}s; target: <= 2.000s");
        return median <= 2 ? 0 : 1;
    }

    private static void VerifyEditorRoundTrip()
    {
        const string original = "첫 줄\0값\r\n둘째 😀";
        const string replacement = "변경된 한 줄\n다음 줄";
        var host = new ScintillaEditorHost();
        var window = new Window { Width = 400, Height = 240, Content = host, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
        window.Show();
        host.LoadUtf8(new UTF8Encoding(false, true).GetBytes(original));
        if (host.GetText() != original || host.IsModified || host.CanUndo)
            throw new InvalidOperationException("Scintilla UTF-8 초기 로드 상태가 올바르지 않습니다.");

        host.ReplaceAll(replacement);
        if (host.GetText() != replacement || !host.IsModified || !host.CanUndo)
            throw new InvalidOperationException("Scintilla 일괄 변경 상태가 올바르지 않습니다.");
        host.Undo();
        if (host.GetText() != original)
            throw new InvalidOperationException("Scintilla 일괄 변경이 한 번의 Undo로 복원되지 않았습니다.");
        host.Redo();
        if (host.GetText() != replacement)
            throw new InvalidOperationException("Scintilla Redo가 변경 내용을 복원하지 않았습니다.");
        window.Close();
        Console.WriteLine("PASS Scintilla UTF-8/NUL round-trip and single-step Undo/Redo");
    }

    private static void EnsureFixture(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length >= TargetCharacters) return;
        var line = Encoding.UTF8.GetBytes("2026-09-08 INFO AAA BBB 요청 처리 완료 한글 log entry 0123456789\r\n");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        var written = 0L;
        while (written < TargetCharacters) { stream.Write(line); written += line.Length; }
    }
}
