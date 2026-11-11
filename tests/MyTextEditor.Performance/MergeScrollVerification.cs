using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using MyTextEditor.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using ScintillaNET;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;

internal static class MergeScrollVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var callbacks = new DiffWindowCallbacks { GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { } };
        var window = new DiffWorkspaceWindow(new(), callbacks, new("Consolas", 11, false));
        var view = window.OpenEmptyTab();
        void Pump() { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        window.Show(); Pump();
        try
        {
            foreach (var side in new[] { "_leftEditor", "_rightEditor" })
            {
                var host = Field<ScintillaEditorHost>(view, side);
                var native = Field<Scintilla>(host, "_editor");
                var bar = Field<Forms.Control>(host, "_horizontalBar");
                host.ReplaceAll(new string('x', 3000) + " END"); native.Refresh(); Pump();
                Check(!native.HScrollBar && bar.Visible && native.ScrollWidth > native.ClientSize.Width, $"Themed horizontal scrollbar has no long-line range: native={native.HScrollBar}, visible={bar.Visible}, range={native.ScrollWidth}, width={native.ClientSize.Width}");
                var maximum = (int)bar.GetType().GetField("_maximum", Hidden)!.GetValue(bar)!;
                Check(maximum > 0, "Themed scrollbar did not track long-line width");
                bar.GetType().GetMethod("MoveTo", Hidden)!.Invoke(bar, [maximum]); Pump();
                Check(native.XOffset > 0, "Horizontal viewport did not move");
                bar.GetType().GetMethod("MoveTo", Hidden)!.Invoke(bar, [0]); Pump();
                Check(native.XOffset == 0, "Horizontal viewport did not return");                host.Undo(); Check(host.GetText() == "", "Scrollbar movement changed Undo");
                host.Redo(); Check(host.GetText().EndsWith(" END"), "Scrollbar movement changed Redo");
            }
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            left.ReplaceAll("cat cats CAT 한글 cat"); right.ReplaceAll("right cat");
            Field<System.Windows.Controls.TextBox>(view, "FindInput").Text = "cat";
            Field<System.Windows.Controls.TextBox>(view, "ReplaceInput").Text = "dog";
            Field<System.Windows.Controls.CheckBox>(view, "FindWholeWord").IsChecked = true;
            Field<System.Windows.Controls.ComboBox>(view, "FindSide").SelectedIndex = 0;
            typeof(DiffTabView).GetMethod("ReplaceAll_Click", Hidden)!.Invoke(view, [view, new RoutedEventArgs()]);
            Check(left.GetText() == "dog cats dog 한글 dog" && right.GetText() == "right cat", "Whole-word replace or target isolation failed");
            left.Undo(); Check(left.GetText() == "cat cats CAT 한글 cat", "Replace all must use one Undo");
            left.Redo(); Check(left.GetText() == "dog cats dog 한글 dog", "Replace all Redo failed");
            VerifySynchronization(view, left, right, Pump);
            VerifyMappedBoundaries(view, left, right, Pump);
            VerifyMergePosition(view, left, right, Pump);
            Console.WriteLine("PASS Merge search whole-word replacement, side isolation, Undo/Redo");
            Console.WriteLine("PASS Merge left/right themed horizontal scrollbar interaction, range and Undo/Redo");
            return 0;
        }
        finally
        {
            typeof(DiffTabView).GetField("_leftDirty", Hidden)!.SetValue(view, false);
            typeof(DiffTabView).GetField("_rightDirty", Hidden)!.SetValue(view, false);
            view.ReleaseResources(); window.Close(); app.Shutdown();
        }
    }

    private static void CompleteComparison(DiffTabView view, Action pump)
    {
        var task = (Task)typeof(DiffTabView).GetMethod("CompareNowAsync", Hidden)!.Invoke(view, [])!;
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("Comparison timeout");
            pump(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult(); pump();
    }

    private static void VerifySynchronization(DiffTabView view, ScintillaEditorHost left, ScintillaEditorHost right, Action pump)
    {
        Check(!view.GetOptions().HorizontalScrollSync, "New horizontal sync must default OFF");
        var oldSettings = JsonSerializer.Deserialize<DiffUserSettings>("{\"ScrollSync\":false}")!;
        Check(!oldSettings.ScrollSync && !oldSettings.HorizontalScrollSync, "Existing settings compatibility failed");
        var roundtrip = JsonSerializer.Deserialize<DiffUserSettings>(JsonSerializer.Serialize(new DiffUserSettings { HorizontalScrollSync = true }))!;
        Check(roundtrip.HorizontalScrollSync, "Horizontal sync setting did not roundtrip");
        var longLine = new string('x', 3000);
        left.ReplaceAll(string.Join('\n', Enumerable.Range(1, 400).Select(i => $"{i} {longLine}")));
        right.ReplaceAll(left.GetText());
        CompleteComparison(view, pump);
        left.SetHorizontalOffset(120); right.SetHorizontalOffset(0); pump();
        Check(right.HorizontalOffset == 0, "Horizontal OFF unexpectedly synchronized");
        var horizontal = Field<System.Windows.Controls.MenuItem>(view, "HorizontalScrollSyncCheck");
        var vertical = Field<System.Windows.Controls.MenuItem>(view, "ScrollSyncCheck");
        horizontal.IsChecked = true;
        vertical.IsChecked = false;
        left.SetHorizontalOffset(240); pump();
        Check(left.HorizontalOffset == 240 && right.HorizontalOffset == 240, "Left-to-right horizontal synchronization failed");
        right.SetHorizontalOffset(480); pump();
        Check(left.HorizontalOffset == 480 && right.HorizontalOffset == 480, "Right-to-left horizontal synchronization failed");
        right.ScrollToLine(0); left.ScrollToLine(90); pump();
        Check(right.FirstVisibleLine == 0, "Horizontal ON changed vertical OFF behavior");
        vertical.IsChecked = true; left.ScrollToLine(110); pump();
        Check(right.FirstVisibleLine == left.FirstVisibleLine, $"Vertical synchronization regressed: left={left.FirstVisibleLine}, right={right.FirstVisibleLine}, ready={view.DifferenceCount}, result={Field<TextDiffResult?>(view, "_result") is not null}");
        horizontal.IsChecked = false; right.SetHorizontalOffset(0); left.SetHorizontalOffset(600); pump();
        Check(right.HorizontalOffset == 0, "Disabling horizontal sync failed");
        horizontal.IsChecked = true;
        right.ReplaceAll("short");
        // ScrollWidthTracking only grows its cached range; give this fixture a short native range.
        Field<Scintilla>(right, "_editor").ScrollWidth = 1;
        CompleteComparison(view, pump);
        left.SetHorizontalOffset(800); pump();
        Check(right.HorizontalOffset == 0 && left.HorizontalOffset == 800, "Short-side clamp fed back into source");
        Check(view.GetOptions().HorizontalScrollSync && view.GetOptions().ScrollSync, "Option state was lost");
        Console.WriteLine("PASS independent horizontal/vertical sync, bidirectionality, unequal widths and settings compatibility");
    }

    private static void VerifyMappedBoundaries(DiffTabView view, ScintillaEditorHost left, ScintillaEditorHost right, Action pump)
    {
        var rows = Enumerable.Range(0, 600).Select(i => $"mapped row {i:D4}").ToList();
        var changed = rows.ToList();
        changed.RemoveRange(220, 60);
        changed.InsertRange(80, Enumerable.Range(0, 40).Select(i => $"inserted row {i:D4}"));
        left.ReplaceAll(string.Join('\n', rows));
        right.ReplaceAll(string.Join('\n', changed));
        Field<System.Windows.Controls.MenuItem>(view, "ScrollSyncCheck").IsChecked = true;
        CompleteComparison(view, pump);
        var map = typeof(DiffTabView).GetMethod("MapScrollLine", Hidden)!;
        foreach (var leftToRight in new[] { true, false })
        {
            var source = leftToRight ? left : right;
            var target = leftToRight ? right : left;
            foreach (var line in new[] { 0, 80, 100, 220, 260, 500, int.MaxValue, 0 })
            {
                source.ScrollToLine(line);
                var expected = Math.Clamp((int)map.Invoke(view, [source.FirstVisibleLine + 1, leftToRight])! - 1,
                    0, Math.Max(0, target.LineCount - target.LinesOnScreen));
                Check(target.FirstVisibleLine == expected,
                    $"Mapped boundary did not synchronize immediately: direction={leftToRight}, source={source.FirstVisibleLine}, target={target.FirstVisibleLine}, expected={expected}");
                pump();
            }
        }
        Console.WriteLine("PASS immediate corresponding-line sync at inserted/deleted ranges and document start/end in both directions");
    }

    private static void VerifyMergePosition(DiffTabView view, ScintillaEditorHost left, ScintillaEditorHost right, Action pump)
    {
        Field<DiffEndpoint>(view, "_left").NewLine = "\n";
        Field<DiffEndpoint>(view, "_right").NewLine = "\n";
        var horizontal = Field<System.Windows.Controls.MenuItem>(view, "HorizontalScrollSyncCheck");
        var vertical = Field<System.Windows.Controls.MenuItem>(view, "ScrollSyncCheck");
        var next = Field<System.Windows.Controls.MenuItem>(view, "MergeNextCheck");
        var merge = typeof(DiffTabView).GetMethod("MergeCurrent", Hidden)!;
        var current = typeof(DiffTabView).GetField("_currentBlockIndex", Hidden)!;
        foreach (var sync in new[] { false, true })
        foreach (var source in new[] { DiffSide.Left, DiffSide.Right })
        foreach (var kind in new[] { "replace", "insert", "delete", "terminal" })
        {
            horizontal.IsChecked = false; vertical.IsChecked = sync; next.IsChecked = false;
            var rows = Enumerable.Range(1, 600).Select(i => $"line {i} " + new string('x', 200)).ToList();
            var changed = rows.ToList();
            if (kind == "replace") changed[119] = "changed " + new string('y', 200);
            if (kind == "insert") changed.Insert(119, "added");
            if (kind == "delete") changed.RemoveAt(119);
            var original = string.Join('\n', rows);
            var different = kind == "terminal" ? original + "\n" : string.Join('\n', changed);
            left.ReplaceAll(source == DiffSide.Left ? original : different);
            right.ReplaceAll(source == DiffSide.Left ? different : original);
            CompleteComparison(view, pump);
            current.SetValue(view, 0);
            left.GoToLine(580, focusEditor: false); right.GoToLine(580, focusEditor: false);
            left.ScrollToLine(105); right.ScrollToLine(105);
            left.SetHorizontalOffset(96); right.SetHorizontalOffset(64); pump();
            horizontal.IsChecked = sync;
            var leftTop = left.FirstVisibleLine; var rightTop = right.FirstVisibleLine;
            var leftX = left.HorizontalOffset; var rightX = right.HorizontalOffset;
            var target = source == DiffSide.Left ? right : left;
            var before = target.GetText();
            merge.Invoke(view, [source]);
            Check(target.GetText() == original, $"{source}/{kind}: wrong merge text");
            CompleteComparison(view, pump);
            Check(left.FirstVisibleLine == leftTop && right.FirstVisibleLine == rightTop,
                $"{source}/{kind}/sync={sync}: merge moved top {leftTop}/{rightTop} -> {left.FirstVisibleLine}/{right.FirstVisibleLine}");
            Check(left.HorizontalOffset == leftX && right.HorizontalOffset == rightX, $"{source}/{kind}: merge moved horizontal offsets");
            target.Undo(); Check(target.GetText() == before, $"{source}/{kind}: merge not single Undo");
            target.Redo(); Check(target.GetText() == original, $"{source}/{kind}: merge Redo failed");
        }
        horizontal.IsChecked = false; vertical.IsChecked = false; next.IsChecked = true;
        var lines = Enumerable.Range(1, 600).Select(i => $"line {i}").ToArray();
        left.ReplaceAll(string.Join('\n', lines));
        lines[119] = "first change"; lines[419] = "second change";
        right.ReplaceAll(string.Join('\n', lines)); CompleteComparison(view, pump);
        current.SetValue(view, 0);
        merge.Invoke(view, [DiffSide.Left]); CompleteComparison(view, pump);
        Check(left.CurrentLine == 419 && right.CurrentLine == 419, "Merge-next did not navigate to next difference");
        // A user scroll during the asynchronous recomparison takes precedence over merge-next.
        right.Undo(); CompleteComparison(view, pump); current.SetValue(view, 0);
        merge.Invoke(view, [DiffSide.Left]); left.ScrollToLine(250); right.ScrollToLine(250);
        CompleteComparison(view, pump);
        Check(left.FirstVisibleLine == 250 && right.FirstVisibleLine == 250, "Recomparison overrode user's later scroll");
        next.IsChecked = false;
        Console.WriteLine("PASS merge viewport preservation across both directions, insert/delete/replace/EOF, sync modes, Undo/Redo and merge-next");
    }
}
