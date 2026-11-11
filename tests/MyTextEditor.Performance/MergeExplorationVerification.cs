using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using Point = System.Windows.Point;

// Exploratory audit: failures are retained as findings, never weakened into passing assertions.
internal static class MergeExplorationVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Observation(string Id, string Scenario, string Result, string Detail, double Milliseconds);
    private static readonly List<Observation> Results = [];
    private static HashSet<string>? Filter;
    private static readonly string Output = Path.GetFullPath("artifacts/merge-exploration-fixed");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Check(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("UI/comparison timeout"); Pump(); Thread.Sleep(5); }
    }
    private static void Complete(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Compare(DiffTabView view)
    {
        Complete((Task)Call(view, "CompareNowAsync")!);
        if (view.IsReady)
            Wait(() => Field<TextDiffResult?>(view, "_result") is not null && !Field<bool>(view, "_needsComparison") &&
                Field<CancellationTokenSource?>(view, "_comparisonCancellation") is null);
    }
    private static DiffEndpoint Endpoint(string title, string text, string newline = "\n") => new()
    { Kind = DiffEndpointKind.Scratch, DisplayName = title, Text = text, NewLine = newline };
    private static readonly DiffWindowCallbacks Callbacks = new()
    {
        GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
        ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
        CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
    };
    private static void Case(string id, string title, Action action)
    {
        if (Filter is not null && !Filter.Contains(id)) return;
        var watch = Stopwatch.StartNew();
        try { action(); Results.Add(new(id, title, "PASS", "", watch.Elapsed.TotalMilliseconds)); Console.WriteLine($"PASS {id} {title}"); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException is not null) error = error.InnerException;
            Results.Add(new(id, title, "FAIL", error.Message, watch.Elapsed.TotalMilliseconds)); Console.WriteLine($"FAIL {id} {title}: {error.Message}");
        }
    }
    private static void View(DiffEndpoint left, DiffEndpoint right, Action<DiffWorkspaceWindow, DiffTabView, ScintillaEditorHost, ScintillaEditorHost> action)
    {
        var window = new DiffWorkspaceWindow(new(), Callbacks, new("Consolas", 11, false))
        { Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Width = 1200, Height = 760 };
        var view = window.OpenComparison(left, right);
        try
        {
            window.Show(); window.Activate(); Wait(() => Field<bool>(view, "_initialized")); Compare(view);
            action(window, view, Field<ScintillaEditorHost>(view, "_leftEditor"), Field<ScintillaEditorHost>(view, "_rightEditor"));
        }
        finally
        {
            foreach (TabItem tab in Field<TabControl>(window, "ComparisonTabs").Items)
            {
                var child = (DiffTabView)tab.Content;
                child.GetType().GetField("_leftDirty", Hidden)!.SetValue(child, false);
                child.GetType().GetField("_rightDirty", Hidden)!.SetValue(child, false);
                child.ReleaseResources();
            }
            window.Close(); Pump();
        }
    }
    private static void Click(FrameworkElement element)
    {
        Check(element.IsVisible && element.IsEnabled && element.ActualWidth > 0, "Control unavailable for mouse click");
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)point.X, (int)point.Y); Pump();
        mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(25); Pump();
        mouse_event(4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(40); Pump();
    }
    private static void Key(byte key, byte modifier = 0)
    {
        try { if (modifier != 0) keybd_event(modifier, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 0, UIntPtr.Zero); Thread.Sleep(25); Pump(); }
        finally { keybd_event(key, 0, 2, UIntPtr.Zero); if (modifier != 0) keybd_event(modifier, 0, 2, UIntPtr.Zero); Thread.Sleep(35); Pump(); }
    }
    private static void Shot(DiffWorkspaceWindow window, string name)
    {
        window.UpdateLayout(); Pump(); var p = window.PointToScreen(new Point(0, 0));
        using var bitmap = new System.Drawing.Bitmap((int)window.ActualWidth, (int)window.ActualHeight);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)p.X, (int)p.Y, 0, 0, bitmap.Size);
        bitmap.Save(Path.Combine(Output, name + ".png"));
    }
    public static int Run(string? filter = null)
    {
        Directory.CreateDirectory(Output); Results.Clear();
        Filter = filter?.Split(',').ToHashSet(StringComparer.Ordinal);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        try
        {
            ComparisonMatrix(); SearchAndKeyboard(); SourceAndSaving(); LayoutAndTabs(); ImprovementRegressions();
        }
        finally { app.Shutdown(); File.WriteAllText(Path.Combine(Output, Filter is null ? "results.json" : "focused-results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true })); }
        Console.WriteLine($"Exploration: {Results.Count(x => x.Result == "PASS")}/{Results.Count} passed; findings={Results.Count(x => x.Result == "FAIL")}; {Output}");
        return Results.Any(x => x.Result == "FAIL") ? 1 : 0;
    }
    private static void ComparisonMatrix()
    {
        (string Name, string Left, string Right)[] fixtures =
        [
            ("both-empty", "", ""), ("empty-left", "", "new\ntext"), ("empty-right", "old\ntext", ""),
            ("equal", "동일 😀\nend\n", "동일 😀\nend\n"), ("replace", "a\nold\nz", "a\nnew\nz"),
            ("insert-top", "a\nz", "head\na\nz"), ("insert-bottom", "a\nz", "a\nz\ntail"),
            ("remove-blank", "a\n\nz", "a\nz"), ("multiple-blocks", "a\nold\nb\nold2\nc", "a\nnew\nb\nnew2\nc"),
            ("unicode", "한글 😀 e\u0301\n끝", "漢字 🚀 é\n끝"), ("eol-only", "a\r\nb\r\n", "a\nb\n"),
            ("terminal-newline", "a\nb", "a\nb\n"), ("tabs", "\tindent\n a ", "    indent\na"),
            ("repeated-lines", "x\nx\na\nx\nx", "x\nx\nb\nx"),
            ("long-line", new string('a', 20000) + "LEFT", new string('a', 20000) + "RIGHT"),
            ("all-different", "one\ntwo\nthree", "4\n5\n6\n7")
        ];
        var index = 0;
        foreach (var fixture in fixtures)
        foreach (var direction in new[] { DiffSide.Left, DiffSide.Right })
        {
            var id = $"M{++index:00}";
            Case(id, $"block merge {fixture.Name} source={direction}; exact Undo/Redo", () =>
                View(Endpoint("left", fixture.Left), Endpoint("right", fixture.Right), (_, view, left, right) =>
                {
                    var target = direction == DiffSide.Left ? right : left;
                    var source = direction == DiffSide.Left ? fixture.Left : fixture.Right;
                    var merges = 0;
                    while (view.DifferenceCount > 0)
                    {
                        Check(++merges <= 12, "Block merge did not converge");
                        var before = target.GetText(); Call(view, "MergeCurrent", direction); Compare(view);
                        var after = target.GetText(); Check(before != after, "Selected block merge made no progress");
                        target.Undo(); Compare(view); Check(target.GetText() == before, "Undo did not restore exact text");
                        target.Redo(); Compare(view); Check(target.GetText() == after, "Redo changed merged text");
                    }
                    Check(target.GetText().Replace("\r\n", "\n") == source.Replace("\r\n", "\n"),
                        $"Final merged text differs from source: expected={JsonSerializer.Serialize(source)}, actual={JsonSerializer.Serialize(target.GetText())}, revision={target.ContentRevision}, resultCount={view.DifferenceCount}");
                }));
        }
        foreach (var option in new[] { "IgnoreWhitespaceCheck", "IgnoreCaseCheck", "IgnoreEmptyLinesCheck" })
            Case("O-" + option, "comparison option changes result without changing either document", () =>
                View(Endpoint("left", option == "IgnoreCaseCheck" ? "Alpha" : option == "IgnoreEmptyLinesCheck" ? "a\n\nb" : " a\tb "),
                     Endpoint("right", option == "IgnoreCaseCheck" ? "alpha" : option == "IgnoreEmptyLinesCheck" ? "a\nb" : "a b"), (_, view, left, right) =>
                     {
                         var beforeLeft = left.GetText(); var beforeRight = right.GetText(); Check(view.DifferenceCount > 0, "Missing initial difference");
                         Field<MenuItem>(view, option).IsChecked = true; Compare(view); Check(view.DifferenceCount == 0, "Option did not suppress expected difference");
                         Check(left.GetText() == beforeLeft && right.GetText() == beforeRight && !view.HasUnsavedChanges, "Option changed content/dirty state");
                         Field<MenuItem>(view, option).IsChecked = false; Compare(view); Check(view.DifferenceCount > 0, "Turning option off did not restore difference");
                     }));
    }
    private static void SearchAndKeyboard()
    {
        Case("K01", "physical Ctrl+H, replacement button, Ctrl+Z/Redo", () =>
            View(Endpoint("left", "cat cats CAT 한글 cat"), Endpoint("right", "right cat"), (window, view, left, right) =>
            {
                Click(left); Pump(); Key(0x48, 0x11);
                Check(Field<Border>(view, "FindBar").IsVisible && Field<StackPanel>(view, "ReplaceInputs").IsVisible, "Ctrl+H did not open replacement");
                Field<TextBox>(view, "FindInput").Text = "cat"; Field<TextBox>(view, "ReplaceInput").Text = "dog";
                Click(Field<CheckBox>(view, "FindWholeWord"));
                var button = Descendants<Button>(Field<StackPanel>(view, "ReplaceInputs")).Single(b => Equals(b.Content, "모두 바꾸기")); Click(button);
                Check(left.GetText() == "dog cats dog 한글 dog" && right.GetText() == "right cat", "Mouse replace changed wrong text/side");
                left.FocusEditor(); Pump(); Key(0x5A, 0x11); Check(left.GetText() == "cat cats CAT 한글 cat", "Physical Ctrl+Z failed");
                Key(0x59, 0x11); Check(left.GetText().StartsWith("dog"), "Physical Ctrl+Y failed");
                Shot(window, "keyboard-replace");
            }));
        Case("K02", "physical F8/F7, Alt+Right merge, Ctrl+Z", () =>
            View(Endpoint("left", "a\nold\nb\nold2\nc"), Endpoint("right", "a\nnew\nb\nnew2\nc"), (_, view, left, right) =>
            {
                left.FocusEditor(); Pump(); var start = Field<int>(view, "_currentBlockIndex");
                Key(0x77); Check(Field<int>(view, "_currentBlockIndex") == (start + 1) % 2, "F8 navigation failed");
                Key(0x76); Check(Field<int>(view, "_currentBlockIndex") == start, "F7 navigation failed");
                var before = right.GetText(); Key(0x27, 0x12); Compare(view); Check(right.GetText() != before && view.DifferenceCount == 1, "Alt+Right merge failed");
                right.FocusEditor(); Pump(); Key(0x5A, 0x11); Compare(view); Check(right.GetText() == before && view.DifferenceCount == 2, "Merged block Undo failed");
            }));
        Case("K03", "locked target rejects typing, merge and replacement; unlock restores editing", () =>
            View(Endpoint("left", "left cat"), Endpoint("right", "right cat"), (_, view, left, right) =>
            {
                Click(Field<CheckBox>(view, "RightReadOnlyCheck")); Check(right.IsReadOnly, "Lock mouse click did not lock target");
                Click(right); Pump(); Key(0x58); Check(right.GetText() == "right cat", "Typing bypassed lock");
                Call(view, "MergeCurrent", DiffSide.Left); Check(right.GetText() == "right cat", "Merge bypassed lock");
                Field<TextBox>(view, "FindInput").Text = "cat"; Field<TextBox>(view, "ReplaceInput").Text = "dog";
                Field<System.Windows.Controls.ComboBox>(view, "FindSide").SelectedIndex = 1; Call(view, "ReplaceAll_Click", view, new RoutedEventArgs());
                Check(right.GetText() == "right cat", "Replace bypassed lock");
                Click(Field<CheckBox>(view, "RightReadOnlyCheck")); Click(right); Pump(); Key(0x58);
                Check(!right.IsReadOnly && right.GetText() != "right cat", $"Unlock did not restore typing: readOnly={right.IsReadOnly}, text={right.GetText()}, nativeFocus={right.IsEditorFocused}, focus={System.Windows.Input.Keyboard.FocusedElement}");
            }));
        (string Name, string Text, string Find, string Replace, bool CaseSensitive, bool Whole, string Expected)[] searches =
        [
            ("substring", "banana", "ana", "X", false, false, "bXna"),
            ("case-sensitive", "Cat cat CAT", "cat", "x", true, false, "Cat x CAT"),
            ("whole-word", "cat cat2 _cat cat-cat", "cat", "x", false, true, "x cat2 _cat x-x"),
            ("unicode", "한글😀한글", "한글", "字", false, false, "字😀字"),
            ("emoji", "a😀b😀", "😀", "🚀", false, false, "a🚀b🚀"),
            ("delete", "xx cat xx", "xx", "", false, false, " cat "),
            ("no-match", "abc", "z", "q", false, false, "abc"),
            ("identical-replacement", "cat cat", "cat", "cat", false, false, "cat cat")
        ];
        foreach (var test in searches)
            Case("S-" + test.Name, "replace all " + test.Name + "; other side unaffected", () =>
                View(Endpoint("left", test.Text), Endpoint("right", "untouched"), (_, view, left, right) =>
                {
                    Field<TextBox>(view, "FindInput").Text = test.Find; Field<TextBox>(view, "ReplaceInput").Text = test.Replace;
                    Field<CheckBox>(view, "FindMatchCase").IsChecked = test.CaseSensitive; Field<CheckBox>(view, "FindWholeWord").IsChecked = test.Whole;
                    Call(view, "ReplaceAll_Click", view, new RoutedEventArgs()); Check(left.GetText() == test.Expected && right.GetText() == "untouched", $"Unexpected replacement: {left.GetText()}");
                    if (test.Text != test.Expected) { left.Undo(); Check(left.GetText() == test.Text, "Replace all is not one Undo"); }
                    else Check(!view.HasUnsavedChanges, "No-op replacement dirtied content");
                }));
        Case("S-enter", "Enter in replacement field should apply the replacement", () =>
            View(Endpoint("left", "cat cat"), Endpoint("right", "cat"), (window, view, left, _) =>
            {
                Click(left); Pump(); Key(0x48, 0x11); Check(Field<StackPanel>(view, "ReplaceInputs").IsVisible, "Replacement bar did not open");
                Field<TextBox>(view, "FindInput").Text = "cat"; Field<TextBox>(view, "ReplaceInput").Text = "dog";
                Call(view, "FindNext_Click", view, new RoutedEventArgs()); Field<TextBox>(view, "ReplaceInput").Focus(); Pump();
                Check(Field<TextBox>(view, "ReplaceInput").IsKeyboardFocused, "Replacement input did not receive focus"); Key(0x0D);
                Shot(window, "replacement-enter"); Check(left.GetText().Contains("dog"), $"Enter did not replace; text={left.GetText()}; status={Field<TextBlock>(view, "FindStatus").Text}");
            }));
        Case("S-button-enter", "Enter on focused replacement button should activate that button", () =>
            View(Endpoint("left", "cat cat"), Endpoint("right", "cat"), (window, view, left, _) =>
            {
                Click(left); Pump(); Key(0x48, 0x11); Field<TextBox>(view, "FindInput").Text = "cat"; Field<TextBox>(view, "ReplaceInput").Text = "dog";
                Call(view, "FindNext_Click", view, new RoutedEventArgs());
                var replace = Descendants<Button>(Field<StackPanel>(view, "ReplaceInputs")).Single(b => Equals(b.Content, "바꾸기"));
                replace.Focus(); Pump(); Check(replace.IsKeyboardFocused, "Could not focus replace button"); Key(0x0D);
                Shot(window, "replacement-button-enter"); Check(left.GetText().Contains("dog"), $"Enter on focused button did not replace: text={left.GetText()}; status={Field<TextBlock>(view, "FindStatus").Text}");
            }));
    }
    private static void SourceAndSaving()
    {
        Case("U-empty-target", "copy populated side into untouched empty side", () =>
            View(Endpoint("left", "copy me"), DiffEndpoint.Empty("right"), (window, view, left, right) =>
            {
                Call(view, "CopyAll", DiffSide.Left); Pump(); Shot(window, "empty-target-copy");
                Check(right.GetText() == left.GetText(), $"Cannot populate empty side: ready={view.IsReady}, copyEnabled={Field<MenuItem>(view, "CopyAllRightButton").IsEnabled}");
            }));
        var folder = Path.Combine(Output, "fixtures"); Directory.CreateDirectory(folder);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        (string Name, Encoding Encoding, bool Bom)[] encodings = [("utf8", new UTF8Encoding(false), false), ("utf8-bom", new UTF8Encoding(true), true),
            ("utf16-le", new UnicodeEncoding(false, true), true), ("utf16-be", new UnicodeEncoding(true, true), true), ("cp949", Encoding.GetEncoding(949), false)];
        foreach (var format in encodings)
        foreach (var eol in new[] { "\n", "\r\n" })
            Case("F-" + format.Name + (eol == "\n" ? "-LF" : "-CRLF"), "load/edit/save/reload encoding and line endings", () =>
            {
                var path = Path.Combine(folder, format.Name + (eol == "\n" ? "-LF" : "-CRLF") + ".txt");
                File.WriteAllText(path, "한글" + eol + "original" + eol, format.Encoding);
                var load = DiffTabView.LoadFileEndpointAsync(path); Complete(load);
                View(load.Result, Endpoint("right", "other"), (_, view, left, _) =>
                {
                    var text = "수정" + eol + "saved" + eol; left.ReplaceAll(text);
                    var save = (Task<bool>)Call(view, "SaveEndpointAsync", true)!; Complete(save); Check(save.Result, "Save returned false");
                    var reload = DiffTabView.LoadFileEndpointAsync(path); Complete(reload);
                    Check(reload.Result.Text == text && reload.Result.Encoding.CodePage == format.Encoding.CodePage && reload.Result.HasByteOrderMark == format.Bom,
                        $"Round-trip changed encoding/BOM/text: codepage={reload.Result.Encoding.CodePage}, bom={reload.Result.HasByteOrderMark}");
                    Check(!view.HasUnsavedChanges && !left.IsModified, "Saved content remains dirty");
                });
            });
    }
    private static void LayoutAndTabs()
    {
        foreach (var size in new[] { (900, 560), (1200, 760), (1600, 900) })
            Case($"L-{size.Item1}", "search/replace and editor visibility at window size", () =>
                View(Endpoint("left", "left\n" + new string('x', 400)), Endpoint("right", "right\n" + new string('y', 400)), (window, view, left, _) =>
                {
                    window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout(); left.FocusEditor(); Pump(); Key(0x48, 0x11); window.UpdateLayout(); Pump();
                    Shot(window, $"layout-{size.Item1}");
                    var replace = Descendants<Button>(Field<StackPanel>(view, "ReplaceInputs")).Single(b => Equals(b.Content, "모두 바꾸기"));
                    var bounds = replace.TransformToAncestor(window).TransformBounds(new Rect(replace.RenderSize));
                    Check(bounds.Right <= window.ActualWidth && bounds.Bottom < window.ActualHeight - 100 && replace.IsVisible, "Replacement control clipped");
                    Check(left.ActualHeight >= 150, $"Search bar leaves too little editor height: {left.ActualHeight}");
                }));
        Case("T01", "two consecutive physical Ctrl+Tab presses and independent tab state", () =>
            View(Endpoint("A-left", "tab A"), Endpoint("A-right", "A"), (window, first, left, _) =>
            {
                Field<TextBox>(first, "FindInput").Text = "first query"; left.SelectUtf16Range(1, 3); var selection = left.SelectionRange;
                var second = window.OpenComparison(Endpoint("B-left", "tab B"), Endpoint("B-right", "B")); Compare(second);
                var other = Field<ScintillaEditorHost>(second, "_leftEditor"); other.FocusEditor(); Pump();
                Key(0x09, 0x11); var tabs = Field<TabControl>(window, "ComparisonTabs");
                Check(ReferenceEquals(((TabItem)tabs.SelectedItem).Content, first), "Ctrl+Tab failed");
                Check(left.GetText() == "tab A" && left.SelectionRange == selection && Field<TextBox>(first, "FindInput").Text == "first query", "Tab switch lost state");
                Key(0x09, 0x11);
                var continuous = ReferenceEquals(((TabItem)tabs.SelectedItem).Content, second);
                var selectedAfter = tabs.SelectedIndex;
                var oldFocus = other.IsEditorFocused; var currentFocus = left.IsEditorFocused;
                if (!continuous) { Click(left); Key(0x09, 0x11); }
                var afterClick = tabs.SelectedIndex;
                Check(continuous, $"Second Ctrl+Tab failed: selected={selectedAfter}, A-nativeFocus={currentFocus}, B-nativeFocus={oldFocus}; click current editor then Ctrl+Tab selects={afterClick}");
                Check(other.GetText() == "tab B", "Other tab text changed");
            }));
        Case("T-tab-type", "typing after Ctrl+Tab edits visible tab and leaves previous tab unchanged", () =>
            View(Endpoint("A-left", "visible A"), Endpoint("A-right", "A"), (window, first, left, _) =>
            {
                var second = window.OpenComparison(Endpoint("B-left", "previous B"), Endpoint("B-right", "B")); Compare(second);
                var other = Field<ScintillaEditorHost>(second, "_leftEditor"); Click(other); Pump(); Key(0x09, 0x11);
                var tabs = Field<TabControl>(window, "ComparisonTabs"); Check(ReferenceEquals(((TabItem)tabs.SelectedItem).Content, first), "Could not switch to visible A");
                Key(0x58); Shot(window, "typing-after-tab-switch");
                Check(left.GetText() != "visible A" && other.GetText() == "previous B",
                    $"After switch to A and typing x: A='{left.GetText()}', B='{other.GetText()}', A-focus={left.IsEditorFocused}, B-focus={other.IsEditorFocused}");
            }));
    }
    private static void ImprovementRegressions()
    {
        foreach (var side in new[] { DiffSide.Left, DiffSide.Right })
            Case("U-copy-empty-" + side, "whole copy fills either empty side with one Undo", () =>
            {
                var source = Endpoint("source", "copied\n한글\n");
                View(side == DiffSide.Left ? source : DiffEndpoint.Empty("left"), side == DiffSide.Right ? source : DiffEndpoint.Empty("right"), (_, view, left, right) =>
                {
                    var target = side == DiffSide.Left ? right : left;
                    Check(Field<MenuItem>(view, side == DiffSide.Left ? "CopyAllRightButton" : "CopyAllLeftButton").IsEnabled, "Copy into empty side is disabled");
                    Call(view, "CopyAll", side); Compare(view);
                    Check(view.IsReady && target.GetText().Replace("\r\n", "\n") == source.Text && view.DifferenceCount == 0, "Empty side did not become a matching editable buffer");
                    target.Undo(); Check(target.GetText() == "", "Copy into empty side is not one Undo");
                    target.Redo(); Check(target.GetText().Replace("\r\n", "\n") == source.Text, "Redo lost copied content");
                });
            });
        Case("U-copy-empty-string", "copying empty source prepares empty target without dirty or Undo", () =>
            View(Endpoint("source", ""), DiffEndpoint.Empty("target"), (_, view, _, right) =>
            {
                Call(view, "CopyAll", DiffSide.Left); Compare(view);
                Check(view.IsReady && right.GetText() == "" && !view.HasUnsavedChanges && !right.CanUndo, "Empty copy dirtied content or did not prepare target");
            }));
        Case("U-copy-locked-empty", "whole copy respects a locked empty target", () =>
        {
            var target = DiffEndpoint.Empty("locked"); target.IsReadOnly = true;
            View(Endpoint("source", "keep source"), target, (_, view, left, right) =>
            {
                Check(!Field<MenuItem>(view, "CopyAllRightButton").IsEnabled, "Locked target command enabled");
                Call(view, "CopyAll", DiffSide.Left);
                Check(!view.RightIsReady && right.GetText() == "" && !view.HasUnsavedChanges && left.GetText() == "keep source", "Copy bypassed empty target lock");
            });
        });
        Case("U-noop-dirty", "identical copy preserves existing dirty state and Undo history", () =>
            View(Endpoint("left", "same"), Endpoint("right", "original"), (_, view, _, right) =>
            {
                right.ReplaceAll("same"); Compare(view); Call(view, "CopyAll", DiffSide.Left);
                Check(right.IsModified && view.HasUnsavedChanges, "No-op copy cleared an existing unsaved edit");
                right.Undo(); Check(right.GetText() == "original", "No-op copy added an extra Undo operation");
            }));
        Case("S-repeat-enter", "repeated replacement Enter stays in input and never inserts document newlines", () =>
            View(Endpoint("left", "cat cat"), Endpoint("right", "right"), (_, view, left, right) =>
            {
                Click(left); Key(0x48, 0x11); Field<TextBox>(view, "FindInput").Text = "cat"; var input = Field<TextBox>(view, "ReplaceInput"); input.Text = "dog";
                Call(view, "FindNext_Click", view, new RoutedEventArgs()); input.Focus(); Pump();
                for (var i = 0; i < 3; i++) { Key(0x0D); Check(input.IsKeyboardFocused, "Replacement Enter moved focus into document"); }
                Check(left.GetText() == "dog dog" && right.GetText() == "right", "Repeated replacement Enter changed wrong text or inserted newline");
            }));
        Case("S-find-enter-focus", "find Enter/Shift+Enter wrap and retain focus without editing", () =>
            View(Endpoint("left", "cat cat"), Endpoint("right", "right"), (_, view, left, _) =>
            {
                Click(left); Key(0x46, 0x11); var input = Field<TextBox>(view, "FindInput"); input.Text = "cat";
                for (var i = 0; i < 3; i++) { Key(0x0D); Check(input.IsKeyboardFocused, "Find Enter moved focus into document"); }
                Key(0x0D, 0x10); Check(input.IsKeyboardFocused && left.SelectedText == "cat" && left.GetText() == "cat cat" && !view.HasUnsavedChanges, "Find navigation edited text or lost focus");
            }));
        Case("S-combo-keys", "search-side dropdown Enter/Escape keeps default selection behavior", () =>
            View(Endpoint("left", "cat"), Endpoint("right", "dog"), (_, view, left, _) =>
            {
                Call(view, "OpenFind_Click", view, new RoutedEventArgs()); view.UpdateLayout();
                Field<TextBox>(view, "FindInput").Focus(); Pump(); Key(0x09, 0x10);
                var side = Field<System.Windows.Controls.ComboBox>(view, "FindSide");
                Check(side.IsKeyboardFocusWithin, "Search-side dropdown did not receive keyboard focus");
                var trace = new List<string>();
                left.ShortcutRequested += (_, e) => trace.Add($"left shortcut {e.Shortcut} handled={e.Handled}");
                var right = Field<ScintillaEditorHost>(view, "_rightEditor");
                right.ShortcutRequested += (_, e) => trace.Add($"right shortcut {e.Shortcut} handled={e.Handled}");
                side.DropDownOpened += (_, _) => trace.Add("opened"); side.DropDownClosed += (_, _) => trace.Add("closed");
                side.AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
                    trace.Add($"preview {e.Key}/{e.SystemKey} modifiers={System.Windows.Input.Keyboard.Modifiers} handled={e.Handled}")), true);
                side.AddHandler(System.Windows.Input.Keyboard.KeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
                    trace.Add($"bubble {e.Key}/{e.SystemKey} handled={e.Handled}")), true);
                Key(0x28, 0x12); Check(side.IsDropDownOpen, $"Alt+Down did not open search-side dropdown: keyboardFocus={System.Windows.Input.Keyboard.FocusedElement}, leftNativeFocus={left.IsEditorFocused}, rightNativeFocus={right.IsEditorFocused}, trace={string.Join("; ", trace)}");
                Key(0x28); Key(0x0D); Check(!side.IsDropDownOpen && side.SelectedIndex == 1, "Dropdown Enter was intercepted by find command");
                side.Focus(); Pump(); Key(0x28, 0x12); Key(0x1B);
                Check(!side.IsDropDownOpen && Field<Border>(view, "FindBar").IsVisible, "Dropdown Escape closed entire search bar");
            }));
        Case("T-three-tabs", "Ctrl+Tab/Shift+Tab/PageUp/PageDown restore the correct native editor", () =>
            View(Endpoint("A", "A"), Endpoint("A-right", "A right"), (window, first, left, _) =>
            {
                var second = window.OpenComparison(Endpoint("B", "B"), Endpoint("B-right", "B right")); Compare(second);
                var third = window.OpenComparison(Endpoint("C", "C"), Endpoint("C-right", "C right")); Compare(third);
                var right = Field<ScintillaEditorHost>(third, "_rightEditor"); Click(right); var tabs = Field<TabControl>(window, "ComparisonTabs");
                for (var i = 0; i < 6; i++) { Key(0x09, 0x11); Check(tabs.SelectedIndex == i % 3, "Forward tab cycle skipped a tab"); }
                Check(right.IsEditorFocused && !left.IsEditorFocused, "Returning to C did not restore its right editor");
                keybd_event(0x10, 0, 0, UIntPtr.Zero); try { Key(0x09, 0x11); } finally { keybd_event(0x10, 0, 2, UIntPtr.Zero); Pump(); }
                Check(tabs.SelectedIndex == 1 && Field<ScintillaEditorHost>(second, "_leftEditor").IsEditorFocused, "Ctrl+Shift+Tab did not focus B");
                Key(0x21, 0x11); Check(tabs.SelectedIndex == 0 && left.IsEditorFocused, "Ctrl+PageUp did not focus A");
                Key(0x22, 0x11); Check(tabs.SelectedIndex == 1, "Ctrl+PageDown did not select B");
            }));
        Case("T-pending-search", "queued tab focus does not steal newly chosen search input", () =>
            View(Endpoint("A", "A"), Endpoint("A-right", "A right"), (window, first, left, _) =>
            {
                var second = window.OpenComparison(Endpoint("B", "B"), Endpoint("B-right", "B right")); Compare(second);
                var tabs = Field<TabControl>(window, "ComparisonTabs"); Click(Field<ScintillaEditorHost>(second, "_leftEditor"));
                tabs.SelectedIndex = 0; window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                Call(first, "OpenFind_Click", first, new RoutedEventArgs()); window.UpdateLayout();
                Check(Field<TextBox>(first, "FindInput").Focus(), "Search input could not receive focus before deferred callback"); Pump();
                Check(Field<TextBox>(first, "FindInput").IsKeyboardFocused && !left.IsEditorFocused, "Deferred tab focus stole search input focus");
            }));
        Case("T-reactivate", "opening a comparison in a hidden workspace focuses the new visible tab", () =>
            View(Endpoint("A", "A"), Endpoint("A-right", "A right"), (window, first, left, _) =>
            {
                Click(left); window.Hide(); Pump();
                var second = window.OpenComparison(Endpoint("B", "B"), Endpoint("B-right", "B right"));
                window.Show(); window.Activate(); Pump(); Key(0x58);
                var other = Field<ScintillaEditorHost>(second, "_leftEditor");
                Check(left.GetText() == "A" && other.GetText() != "B" && other.IsEditorFocused, $"Reactivated workspace typed in wrong place: A={left.GetText()}, B={other.GetText()}");
            }));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
