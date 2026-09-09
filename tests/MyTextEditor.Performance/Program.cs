using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Models;
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
        VerifySettingsModels();
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

    private static void VerifySettingsModels()
    {
        var condition = new ConditionGroup(ConditionOperator.All,
        [
            new TextCondition(TextConditionKind.Contains, "AAA"),
            new ConditionGroup(ConditionOperator.Any,
            [
                new TextCondition(TextConditionKind.Contains, "BBB"),
                new TextCondition(TextConditionKind.DoesNotContain, "CC")
            ])
        ]);
        var savedCondition = SavedConditionMapper.FromCore(condition);
        var restored = SavedConditionMapper.ToCore(savedCondition);
        var leftState = new SearchInputState { Mode = SavedSearchMode.Advanced, Condition = savedCondition };
        var rightState = new SearchInputState { Mode = SavedSearchMode.Advanced, Condition = SavedConditionMapper.FromCore(restored) };
        if (!SavedSearchHistory.HasSameCriteria(leftState, rightState)) throw new InvalidOperationException("저장 검색 조건이 손실 없이 왕복되지 않았습니다.");

        var history = new List<SavedSearch>();
        for (var index = 0; index < 25; index++)
        {
            SavedSearchHistory.AddOrMoveToFront(history, new SavedSearch
            {
                Summary = $"검색 {index}",
                Search = new SearchInputState { SimpleAllTerms = [$"value-{index}"], Condition = SavedConditionMapper.FromCore(new TextCondition(TextConditionKind.Contains, $"value-{index}")) }
            });
        }
        if (history.Count != 20 || history[0].Summary != "검색 24") throw new InvalidOperationException("최근 검색 20개 제한이 올바르지 않습니다.");
        SavedSearchHistory.AddOrMoveToFront(history, history[^1]);
        if (history.Count != 20 || history[0].Summary != "검색 5") throw new InvalidOperationException("최근 검색 중복 이동이 올바르지 않습니다.");
        Console.WriteLine("PASS saved condition round-trip and recent search history");
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
