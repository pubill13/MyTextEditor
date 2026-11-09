using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

internal static class TransformAuditVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(MainWindow window)
    {
        var watch = Stopwatch.StartNew();
        while (Field<bool>(window, "_transformRunning"))
        { Check(watch.Elapsed.TotalMinutes < 4, "Transform timed out"); Pump(); Thread.Sleep(1); }
        Pump();
    }
    private static void Isolate(MainWindow window)
    {
        typeof(MainWindow).GetField("_settingsReady", Hidden)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
    }
    private sealed record Case(string Name, int Index, string Input, string Output, string Value = "", string? Quick = null);
    private static readonly Case[] Cases =
    [
        new("before", 0, "aa[값]zz", "[값]zz"),
        new("after", 1, "aa[값]zz", "aa["),
        new("between-delete", 2, "aa[값]zz", "aa[]zz"),
        new("between-keep", 3, "aa[값]zz", "[값]"),
        new("left-characters", 4, "😀한글\nábc", "한글\nbc"),
        new("right-characters", 5, "한글😀\nbá", "한글\nb"),
        new("prefix", 6, "가\n나", ">가\n>나", ">"),
        new("suffix", 7, "가\n나", "가<\n나<", "<"),
        new("split", 8, "a,,b,", "a\n\nb\n", ","),
        new("join", 9, "a\n\nb\n", "a||b|", "|"),
        new("numbers-add", 10, "가\n나", "1: 가\n2: 나"),
        new("numbers-remove", 11, "1: 가\n2026: 원문\nkeep", "가\n원문\nkeep"),
        new("duplicates", -1, "A\na\nb\nA", "A\nb", Quick: "Duplicate"),
        new("blank-delete", -1, "a\n \n\t\nb", "a\nb", Quick: "Blank"),
        new("blank-collapse", -1, "a\n\n\nb", "a\n\nb", Quick: "Collapse"),
        new("whitespace", -1, " a \n\tb\t", " a\n\tb", Quick: "Whitespace"),
        new("contains-delete", 12, "keep\nDROP x\nlast", "keep\nlast", "drop"),
        new("replace", 13, "aAa\nkeep", "bbb\nkeep", "a")
    ];
    public static int Run(bool large = false, bool joinOnly = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
        Isolate(window);
        try
        {
            window.Show(); Call(window, "SetToolsVisible", true, false);
            Field<System.Windows.Controls.TabControl>(window, "ToolTabs").SelectedIndex = 1; Pump();
            if (large) { RunLarge(window, joinOnly); return 0; }
            var combo = Field<ComboBox>(window, "TrimOperationCombo");
            var scroll = Field<System.Windows.Controls.ScrollViewer>(window, "TransformScrollViewer");
            for (var index = 0; index < 12; index++)
            {
                combo.SelectedIndex = index; Pump();
                var tooltip = (System.Windows.Controls.ToolTip)combo.ToolTip;
                Check(((System.Windows.Controls.TextBlock)tooltip.Content).Text.Contains("예시"), "Function tip lacks example");
            }
            Check(Buttons(scroll).Count(b => b.ToolTip is System.Windows.Controls.ToolTip) == 6, "Missing title tooltips");
            Console.WriteLine("PASS 12 dynamic function tips, six title tips");
            var document = window.Documents[0];
            foreach (var test in Cases)
            {
                for (var round = 0; round < 3; round++)
                {
                    Setup(window, document, test);
                    Click(window, test, true);
                    Check(document.Text == test.Input && !document.Editor.CanUndo, test.Name + " preview modified source");
                    Check(Field<string>(window, "_pendingTransformedText") == test.Output, test.Name + " preview mismatch");
                    Field<Button>(window, "ApplyTransformButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Check(document.Text == test.Output, test.Name + " preview apply mismatch");
                    document.Editor.Undo(); Check(document.Text == test.Input, test.Name + " Undo mismatch");
                    document.Editor.Redo(); Check(document.Text == test.Output, test.Name + " Redo mismatch");
                    Setup(window, document, test); Click(window, test, false);
                    Check(document.Text == test.Output, test.Name + " direct apply mismatch");
                    document.Editor.Undo(); Check(document.Text == test.Input, test.Name + " direct Undo mismatch");
                }
                Console.WriteLine($"PASS transform-ui {test.Name} preview/apply/Undo/Redo/direct x3");
            }
            var join = new Case("empty-join", 9, "a\nb", "ab");
            Setup(window, document, join); Click(window, join, false);
            Check(document.Text == "ab", "Empty join delimiter must concatenate without separators");
            Setup(window, document, Cases[4]); Field<TextBox>(window, "CharacterCountBox").Text = "-1";
            Click(window, Cases[4], false);
            Check(document.Text == Cases[4].Input && !document.Editor.CanUndo, "Invalid count changed source");
            Setup(window, document, Cases[8]); Field<TextBox>(window, "ValueInputBox").Text = "";
            Click(window, Cases[8], true);
            Check(document.Text == Cases[8].Input && Field<string?>(window, "_pendingTransformedText") is null, "Invalid split generated preview");
            Setup(window, document, Cases[4]); Field<TextBox>(window, "CharacterCountBox").Text = "0";
            Click(window, Cases[4], false);
            Check(document.Text == Cases[4].Input && !document.Editor.CanUndo, "No-op changed Undo state");
            var blank = new Case("empty-blank", -1, "", "", Quick: "Blank");
            Setup(window, document, blank); Click(window, blank, false);
            Check(document.Text == "" && !document.Editor.CanUndo && !document.IsModified, "Empty cleanup dirtied document or Undo");
            Click(window, blank, true);
            Check(!Field<Button>(window, "ApplyTransformButton").IsEnabled, "Empty cleanup enabled no-op preview apply");
            Setup(window, document, Cases[6]); Click(window, Cases[6], false); Click(window, Cases[6], false);
            Check(document.Text == ">>가\n>>나", "Repeated prefix lost accumulated edit");
            document.Editor.Undo(); Check(document.Text == ">가\n>나", "Consecutive apply Undo crossed operation boundary");
            Field<TextBox>(window, "ValueInputBox").Text = "!"; Click(window, Cases[6], false);
            Check(document.Text == "!>가\n!>나", "Changed option did not apply to next run");
            Setup(window, document, Cases[6]); Click(window, Cases[6], true);
            document.Editor.ReplaceAll("newer input");
            // The native OK-only message box exposes its sole confirmation button as IDCANCEL (2).
            typeof(DocumentDisplayVerification).GetMethod("WithDialogResponse", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [window, 2, (Action)(() => Call(window, "ApplyTransform_Click", window, new RoutedEventArgs()))]);
            Check(document.Text == "newer input", "Stale preview overwrote newer text");
            VerifyMoveAndClose(window, document);
            Console.WriteLine("PASS invalid inputs, no-op, empty join and stale preview protection");
            return 0;
        }
        finally
        {
            foreach (var owner in app.Windows.OfType<MainWindow>().ToArray())
            {
                owner.Hide(); Isolate(owner);
                foreach (var document in owner.Documents) document.Editor.ReleaseResources();
                typeof(MainWindow).GetField("_allowClose", Hidden)!.SetValue(owner, true);
                owner.Close();
            }
            app.Shutdown();
        }
    }
    private static void Setup(MainWindow window, DocumentViewModel document, Case test)
    {
        Call(window, "ClearTransformPreview"); Call(window, "SelectDocument", document);
        document.NewLine = "\n"; document.Editor.LoadUtf8(Encoding.UTF8.GetBytes(test.Input));
        Field<ComboBox>(window, "TrimOperationCombo").SelectedIndex = Math.Clamp(test.Index, 0, 11);
        Field<TextBox>(window, "StartMarkerBox").Text = "["; Field<TextBox>(window, "EndMarkerBox").Text = "]";
        Field<CheckBox>(window, "KeepStartCheck").IsChecked = true; Field<CheckBox>(window, "KeepEndCheck").IsChecked = true;
        Field<CheckBox>(window, "TrimMatchCaseCheck").IsChecked = false;
        Field<CheckBox>(window, "IncludeBlankLinesCheck").IsChecked = false;
        Field<CheckBox>(window, "DeleteContainingMatchCaseCheck").IsChecked = false;
        Field<TextBox>(window, "CharacterCountBox").Text = "1";
        Field<TextBox>(window, "ValueInputBox").Text = test.Value;
        Field<TextBox>(window, "StartNumberBox").Text = "1"; Field<TextBox>(window, "LineNumberSeparatorBox").Text = ": ";
        Field<TextBox>(window, "DeleteContainingBox").Text = test.Value;
        Field<TextBox>(window, "ReplaceFromBox").Text = test.Value; Field<TextBox>(window, "ReplaceToBox").Text = "b";
        window.UpdateLayout(); Pump();
    }
    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        if (node is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static void Click(MainWindow window, Case test, bool preview)
    {
        var content = test.Quick is not null ? preview ? "미리보기" : "바로 적용"
            : test.Index == 12 ? preview ? "삭제 미리보기" : "바로 삭제"
            : test.Index == 13 ? preview ? "치환 미리보기" : "바로 치환"
            : preview ? "미리보기" : "바로 적용";
        var button = Buttons(Field<FrameworkElement>(window, "TransformScrollViewer"))
            .Single(x => x.Content?.ToString() == content && (test.Quick is null ? x.Tag is null || test.Index == 12 : x.Tag?.ToString() == test.Quick));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(window);
    }
    private static void RunLarge(MainWindow window, bool joinOnly)
    {
        var service = new TextTransformService(); var document = window.Documents[0]; document.NewLine = "\n";
        var scenarios = new (string Name, string Unit, string ExpectedUnit, Func<string, TextTransformResult> Transform)[]
        {
            ("trim", " " + new string('x', 997) + " \n", new string('x', 997) + "\n", x => service.TrimWhitespace(x, "\n")),
            ("delete", "DROP" + new string('x', 995) + "\n", "", x => service.RemoveLinesContaining(x, "DROP", true, "\n")),
            ("split", new string('x', 999) + "|", new string('x', 999) + "\n", x => service.SplitByDelimiter(x, "|", "\n")),
            ("join", new string('x', 999) + "\n", new string('x', 999) + "|", x => service.JoinLines(x, "|"))
        };
        foreach (var bytes in new[] { 30_000_000, 300_000_000 }) foreach (var scenario in scenarios)
        {
            if (joinOnly && scenario.Name != "join") continue;
            Console.WriteLine($"START transform-large operation={scenario.Name} bytes={bytes} pid={Environment.ProcessId}");
            var source = string.Concat(Enumerable.Repeat(scenario.Unit, bytes / scenario.Unit.Length));
            var expected = string.Concat(Enumerable.Repeat(scenario.ExpectedUnit, bytes / scenario.Unit.Length));
            document.Editor.LoadUtf8(Encoding.UTF8.GetBytes(source)); document.Editor.SetWordWrap(false); Pump();
            double maxGap = 0, computeMs = 0; var watch = Stopwatch.StartNew(); var previous = watch.Elapsed.TotalMilliseconds;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) => { var now = watch.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - previous); previous = now; };
            timer.Start();
            try
            {
                Func<string, TextTransformResult> measured = input => { var compute = Stopwatch.StartNew(); var result = scenario.Transform(input); computeMs = compute.Elapsed.TotalMilliseconds; return result; };
                var task = (Task)Call(window, "RunTransformAsync", measured, scenario.Name, false)!;
                Wait(window); task.GetAwaiter().GetResult(); Pump();
                maxGap = Math.Max(maxGap, watch.Elapsed.TotalMilliseconds - previous);
            }
            finally { timer.Stop(); watch.Stop(); }
            Check(document.Text == expected, $"{scenario.Name} {bytes} exact output mismatch");
            Console.WriteLine($"PASS transform-large operation={scenario.Name} bytes={bytes} totalMs={watch.Elapsed.TotalMilliseconds:F1} computeMs={computeMs:F1} maxUiTickGapMs={maxGap:F1}");
            if (joinOnly)
            {
                var caretWatch = Stopwatch.StartNew();
                var line = document.Editor.CurrentLine;
                var lineMs = caretWatch.Elapsed.TotalMilliseconds;
                caretWatch.Restart();
                var column = document.Editor.CurrentColumn;
                Console.WriteLine($"caret line={line} lineMs={lineMs:F1} column={column} columnMs={caretWatch.Elapsed.TotalMilliseconds:F1}");
            }
            document.Editor.LoadUtf8(ReadOnlyMemory<byte>.Empty); source = expected = "";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
    }

    private static void VerifyMoveAndClose(MainWindow window, DocumentViewModel document)
    {
        var source = new string('x', 1_000_001);
        var destination = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
        Isolate(destination); destination.Show(); Pump();
        foreach (var close in new[] { false, true })
        {
            Call(window, "ClearTransformPreview"); Call(window, "SelectDocument", document);
            document.Editor.LoadUtf8(Encoding.UTF8.GetBytes(source));
            using var started = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
            Func<string, TextTransformResult> operation = input =>
            {
                started.Set();
                if (!finish.Wait(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Move/close test worker not released");
                return new TextTransformService().AddPrefix(input, ">", false, "\n");
            };
            var task = (Task)Call(window, "RunTransformAsync", operation, "ownership audit", false)!;
            try
            {
                Check(started.Wait(TimeSpan.FromSeconds(5)), "Background operation did not start");
                if (close) Call(window, "RemoveDocument", document);
                else Check((bool)Call(window, "MoveDocumentToWindow", document, destination, false, 0)!, "Moving active transform document failed");
            }
            finally { finish.Set(); }
            Wait(window); task.GetAwaiter().GetResult();
            if (close) Check(!window.Documents.Contains(document), "Closed document reappeared after transform");
            else
            {
                Check(destination.Documents.Contains(document) && document.Text == source, "Transform applied after ownership moved");
                Check((bool)Call(destination, "MoveDocumentToWindow", document, window, false, 0)!, "Return move failed");
            }
        }
        destination.Close(); Pump();
        Console.WriteLine("PASS delayed transform after document move and editor release");
    }
}
