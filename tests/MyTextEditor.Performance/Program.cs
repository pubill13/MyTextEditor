using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

internal static class Program
{
    private const int TargetCharacters = 30_000_000;

    [STAThread]
    private static int Main()
    {
        var path = Path.Combine(Path.GetTempPath(), "MyTextEditor-30m-utf8.log");
        EnsureFixture(path);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        VerifyEditorShortcutsAndRelease();
        VerifyEditorRoundTrip();
        VerifyDiffWindowAndMergeUndo();
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
            var closeStopwatch = Stopwatch.StartNew();
            host.ReleaseResources();
            window.Close();
            closeStopwatch.Stop();
            if (closeStopwatch.Elapsed > TimeSpan.FromSeconds(1.5))
                throw new InvalidOperationException($"30MB 편집기 해제가 종료 기준을 초과했습니다: {closeStopwatch.Elapsed.TotalSeconds:F3}s");
            Console.WriteLine($"release {run}: {closeStopwatch.Elapsed.TotalSeconds:F3}s (target <= 1.500s)");
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
        var koreanAndNul = host.GetLineTextRange(0, 2, 2);
        var emoji = host.GetLineTextRange(1, 3, 2);
        if (host.GetTextRange(koreanAndNul) != "줄\0" || host.GetTextRange(emoji) != "😀")
            throw new InvalidOperationException("Diff 인라인 범위가 한글·NUL·이모지 문자 위치를 보존하지 못했습니다.");
        try
        {
            host.GetLineTextRange(1, 4, 1);
            throw new InvalidOperationException("UTF-16 surrogate 중간 범위를 허용했습니다.");
        }
        catch (ArgumentException) { }

        host.ReplaceAll(replacement);
        if (host.GetText() != replacement || !host.IsModified || !host.CanUndo)
            throw new InvalidOperationException("Scintilla 일괄 변경 상태가 올바르지 않습니다.");
        host.Undo();
        if (host.GetText() != original)
            throw new InvalidOperationException("Scintilla 일괄 변경이 한 번의 Undo로 복원되지 않았습니다.");
        host.Redo();
        if (host.GetText() != replacement)
            throw new InvalidOperationException("Scintilla Redo가 변경 내용을 복원하지 않았습니다.");
        host.ReleaseResources();
        window.Close();
        Console.WriteLine("PASS Scintilla UTF-8/NUL round-trip and single-step Undo/Redo");
    }

    private static void VerifyEditorShortcutsAndRelease()
    {
        var host = new ScintillaEditorHost();
        var editor = typeof(ScintillaEditorHost).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host)
            ?? throw new InvalidOperationException("Scintilla editor instance was not found.");
        var processCmdKey = editor.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scintilla command-key hook was not found.");
        var expected = new (Forms.Keys Keys, EditorShortcut Shortcut)[]
        {
            (Forms.Keys.Control | Forms.Keys.N, EditorShortcut.NewDocument),
            (Forms.Keys.Control | Forms.Keys.O, EditorShortcut.OpenDocument),
            (Forms.Keys.Control | Forms.Keys.S, EditorShortcut.SaveDocument),
            (Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.S, EditorShortcut.SaveDocumentAs),
            (Forms.Keys.Control | Forms.Keys.W, EditorShortcut.CloseDocument),
            (Forms.Keys.Control | Forms.Keys.F4, EditorShortcut.CloseDocument),
            (Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.W, EditorShortcut.CloseAllDocuments),
            (Forms.Keys.Control | Forms.Keys.F, EditorShortcut.Find),
            (Forms.Keys.Control | Forms.Keys.H, EditorShortcut.Replace),
            (Forms.Keys.Control | Forms.Keys.Tab, EditorShortcut.NextDocument),
            (Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.Tab, EditorShortcut.PreviousDocument),
            (Forms.Keys.Control | Forms.Keys.PageUp, EditorShortcut.PreviousDocument),
            (Forms.Keys.Control | Forms.Keys.PageDown, EditorShortcut.NextDocument),
            (Forms.Keys.F3, EditorShortcut.FindNext),
            (Forms.Keys.Shift | Forms.Keys.F3, EditorShortcut.FindPrevious),
            (Forms.Keys.Alt | Forms.Keys.Up, EditorShortcut.PreviousDifference),
            (Forms.Keys.Alt | Forms.Keys.Down, EditorShortcut.NextDifference),
            (Forms.Keys.Alt | Forms.Keys.Left, EditorShortcut.MergeRightToLeft),
            (Forms.Keys.Alt | Forms.Keys.Right, EditorShortcut.MergeLeftToRight)
        };
        EditorShortcut? requested = null;
        host.ShortcutRequested += (_, args) => { requested = args.Shortcut; args.Handled = true; };
        foreach (var item in expected)
        {
            requested = null;
            var arguments = new object[] { Forms.Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero), item.Keys };
            if (processCmdKey.Invoke(editor, arguments) is not true || requested != item.Shortcut)
                throw new InvalidOperationException($"Editor shortcut was not forwarded: {item.Keys}.");
        }
        foreach (var key in new[] { Forms.Keys.Control | Forms.Keys.Z, Forms.Keys.Control | Forms.Keys.Y, Forms.Keys.Control | Forms.Keys.X,
                     Forms.Keys.Control | Forms.Keys.C, Forms.Keys.Control | Forms.Keys.V, Forms.Keys.Control | Forms.Keys.A })
        {
            requested = null;
            var arguments = new object[] { Forms.Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero), key };
            processCmdKey.Invoke(editor, arguments);
            if (requested is not null) throw new InvalidOperationException($"Editor command was intercepted: {key}.");
        }
        var releaseStopwatch = Stopwatch.StartNew();
        host.ReleaseResources();
        releaseStopwatch.Stop();
        if (releaseStopwatch.Elapsed > TimeSpan.FromMilliseconds(500))
            throw new InvalidOperationException($"빈 편집기 해제가 종료 기준을 초과했습니다: {releaseStopwatch.Elapsed.TotalMilliseconds:F0}ms");
        host.ReleaseResources();
        Console.WriteLine($"PASS editor shortcut forwarding and idempotent resource release ({releaseStopwatch.Elapsed.TotalMilliseconds:F0}ms)");
    }

    private static void VerifyDiffWindowAndMergeUndo()
    {
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1,
            GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
            SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask,
            SettingsChanged = _ => { }
        };
        var window = new DiffWindow(
            new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "left", Text = "a\nold", IsReadOnly = false, NewLine = "\n" },
            new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "right", Text = "a\nnew", IsReadOnly = false, NewLine = "\n" },
            new DiffWindowOptions(), callbacks, new DiffAppearance("Consolas", 12, false))
        {
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow
        };
        window.Show();

        var resultField = typeof(DiffWindow).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PumpDispatcherUntil(() => resultField.GetValue(window) is TextDiffResult, TimeSpan.FromSeconds(3));
        var merge = typeof(DiffWindow).GetMethod("MergeCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        merge.Invoke(window, [DiffSide.Left]);
        var rightEditor = (ScintillaEditorHost)typeof(DiffWindow)
            .GetField("_rightEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        if (rightEditor.GetText() != "a\nold")
            throw new InvalidOperationException("Diff 블록 병합 결과가 올바르지 않습니다.");
        rightEditor.Undo();
        if (rightEditor.GetText() != "a\nnew")
            throw new InvalidOperationException("Diff 블록 병합이 한 번의 Undo로 복원되지 않았습니다.");

        typeof(DiffWindow).GetField("_closingApproved", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
        Console.WriteLine("PASS modeless Diff window compare, merge, and single-step Undo");
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
                throw new TimeoutException("Diff 창 비교가 제한 시간 안에 완료되지 않았습니다.");
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(10),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => frame.Continue = false,
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }
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

        var legacyAdvanced = new SearchInputState
        {
            Mode = SavedSearchMode.Advanced,
            Options = new SavedSearchOptions { MatchCase = true, WholeWord = true, ContextLines = 2 },
            Condition = new SavedConditionNode
            {
                NodeType = SavedConditionNodeType.Group,
                Operator = SavedConditionOperator.All,
                Children =
                [
                    SavedConditionMapper.FromCore(new TextCondition(TextConditionKind.Contains, "AAA")),
                    new SavedConditionNode
                    {
                        NodeType = SavedConditionNodeType.Group,
                        Operator = SavedConditionOperator.Any,
                        Children =
                        [
                            SavedConditionMapper.FromCore(new TextCondition(TextConditionKind.Contains, "BBB")),
                            SavedConditionMapper.FromCore(new TextCondition(TextConditionKind.Contains, "CCC"))
                        ]
                    },
                    SavedConditionMapper.FromCore(new TextCondition(TextConditionKind.DoesNotContain, "제외"))
                ]
            }
        };
        if (!LegacySearchMigration.TryConvertAdvancedToSimple(legacyAdvanced, out var migrated) ||
            migrated.Mode != SavedSearchMode.Simple || migrated.SimpleAllTerms is not ["AAA"] ||
            migrated.SimpleAnyTerms is not ["BBB", "CCC"] || migrated.SimpleExcludeTerms is not ["제외"] ||
            !migrated.Options.MatchCase || !migrated.Options.WholeWord || migrated.Options.ContextLines != 2)
            throw new InvalidOperationException("표현 가능한 구버전 고급 조건을 간편 조건으로 변환하지 못했습니다.");

        legacyAdvanced.Condition.Children[1].Children.Add(
            SavedConditionMapper.FromCore(new TextCondition(TextConditionKind.DoesNotContain, "Any 제외")));
        if (LegacySearchMigration.TryConvertAdvancedToSimple(legacyAdvanced, out _))
            throw new InvalidOperationException("간편 조건으로 표현할 수 없는 고급 조건을 허용했습니다.");

        var firstSimple = SearchInputState.CreateSimple(["동일"], [], [], new SavedSearchOptions { ContextLines = 1 });
        var secondSimple = SearchInputState.CreateSimple(["동일"], [], [], new SavedSearchOptions { ContextLines = 1 });
        secondSimple.Condition = new SavedConditionNode();
        if (!SavedSearchHistory.HasSameCriteria(firstSimple, secondSimple))
            throw new InvalidOperationException("간편 검색 중복 비교가 legacy 조건 DTO에 의존합니다.");

        Console.WriteLine("PASS saved condition round-trip, v1.4 legacy migration, and recent search history");
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
