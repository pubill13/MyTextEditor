using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

internal static class MergeSourceExploration
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Observation(string Case, string Outcome, string Evidence);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);

    public static int Run(string? filter = null)
    {
        var results = new List<Observation>();
        var selected = filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        bool Includes(string id) => selected is null || selected.Contains(id);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var folder = Path.Combine(Path.GetTempPath(), "OmniEdit-MergeSourceExploration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        void Observe(string id, Action test)
        {
            try { test(); }
            catch (Exception exception) { results.Add(new(id, "ERROR", exception.ToString())); }
        }
        try
        {
            if (Includes("SOURCE-RELOAD-CACHE") || Includes("SOURCE-RELOAD-MERGE-SAFETY"))
                Observe("SOURCE-RELOAD-CACHE", () => VerifyReload(results));
            if (Includes("SOURCE-RELOAD-PARTIAL-CANCEL"))
                Observe("SOURCE-RELOAD-PARTIAL-CANCEL", () => VerifyPartialReloadCancel(results));
            if (Includes("CLIPBOARD-LOCK-METADATA"))
                Observe("CLIPBOARD-LOCK-METADATA", () => VerifyClipboardLock(results));
            for (var round = 1; round <= 3; round++)
            {
                var currentRound = round;
                if (Includes($"OVERLAPPING-SAVE-{round}"))
                    Observe($"OVERLAPPING-SAVE-{round}", () => VerifyOverlappingSave(folder, currentRound, results));
            }
        }
        finally
        {
            foreach (var window in app.Windows.OfType<Window>().ToArray()) window.Close();
            app.Shutdown();
            // Only fixtures created by this invocation are removed.
            foreach (var path in Directory.GetFiles(folder)) File.Delete(path);
            Directory.Delete(folder);
            var output = Path.GetFullPath(Path.Combine("artifacts", "merge-exploration-fixed", filter is null ? "source-results.json" : "filtered-source-results.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                generatedUtc = DateTime.UtcNow,
                filter,
                method = "Real WPF views and native Scintilla editors; reload Button.Click routed event; save methods called via reflection; no physical mouse input",
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var result in results) Console.WriteLine($"{result.Outcome} {result.Case}: {result.Evidence}");
            Console.WriteLine("Observations: " + output);
        }
        return results.Any(result => result.Outcome is "FAIL" or "ERROR") ? 1 : 0;
    }

    private static DiffWindowCallbacks Callbacks() => new()
    {
        GetSourceRevision = _ => -1,
        GetSourceSnapshot = _ => null,
        ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
        SaveSourceAsync = _ => Task.FromResult(false),
        CreateDocumentAsync = (_, _) => Task.CompletedTask,
        SettingsChanged = _ => { }
    };

    private static (Window Window, DiffTabView View) Show(DiffEndpoint left, DiffEndpoint right, DiffWindowCallbacks callbacks)
    {
        var view = new DiffTabView(left, right, new(), callbacks, new("Consolas", 11, false));
        view.SetActive(true);
        var window = new Window { Content = view, Width = 1100, Height = 700, ShowInTaskbar = false };
        window.Show();
        PumpUntil(() => Field<bool>(view, "_initialized") && Field<TextDiffResult?>(view, "_result") is not null);
        return (window, view);
    }

    private static DiffEndpoint Endpoint(string name, string text) => new()
    { Kind = DiffEndpointKind.Scratch, DisplayName = name, Text = text, NewLine = "\n" };

    private static void VerifyReload(List<Observation> results)
    {
        var sourceId = Guid.NewGuid();
        long revision = 0;
        const string before = "head\nold source\ntail";
        const string after = "head\nnew line 1\nnew line 2\ntail";
        var sourceText = before;
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => revision,
            GetSourceSnapshot = _ => new("source", sourceText, null, new UTF8Encoding(false), false, "\n", revision, sourceId),
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
            SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask,
            SettingsChanged = _ => { }
        };
        var leftEndpoint = new DiffEndpoint
        { Kind = DiffEndpointKind.OpenDocument, DisplayName = "source", Text = before, SourceDocumentId = sourceId, SourceRevision = revision, NewLine = "\n" };
        var (window, view) = Show(leftEndpoint, Endpoint("right", after), callbacks);
        try
        {
            var initialCount = view.DifferenceCount;
            sourceText = after;
            revision++;
            view.RefreshSourceState();
            var generation = Field<int>(view, "_generation");
            var button = Descendants(view).OfType<Button>().Single(item => Equals(item.Content, "원본 다시 불러오기"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => Field<int>(view, "_generation") > generation && Field<TextDiffResult?>(view, "_result") is not null && Field<CancellationTokenSource?>(view, "_comparisonCancellation") is null);
            var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
            var textMatches = editor.GetText() == after;
            var pass = initialCount > 0 && textMatches && view.DifferenceCount == 0;
            results.Add(new("SOURCE-RELOAD-CACHE", pass ? "PASS" : "FAIL",
                $"Reload via actual Button.Click routed event: initial differences={initialCount}, displayed text matches new source={textMatches}, differences after reload={view.DifferenceCount}, expected=0; native revision={editor.ContentRevision}"));
            try
            {
                var right = Field<ScintillaEditorHost>(view, "_rightEditor");
                var staleCount = view.DifferenceCount;
                var equalBeforeMerge = editor.GetText() == right.GetText() && editor.GetText() == after;
                if (!equalBeforeMerge)
                {
                    results.Add(new("SOURCE-RELOAD-MERGE-SAFETY", "INCONCLUSIVE",
                        "Safety action not attempted because reload did not produce the expected equal displayed buffers."));
                }
                else if (staleCount == 0)
                {
                    results.Add(new("SOURCE-RELOAD-MERGE-SAFETY", "PASS",
                        "Equal displayed buffers expose no stale difference to merge after reload."));
                }
                else
                {
                    // Same implementation invoked by the visible right-to-left merge arrow;
                    // this is a reflected command invocation, not a physical arrow click.
                    Call(view, "MergeCurrent", DiffSide.Right);
                    var leftAfter = editor.GetText();
                    var rightAfter = right.GetText();
                    PumpUntil(() => Field<TextDiffResult?>(view, "_result") is not null && Field<CancellationTokenSource?>(view, "_comparisonCancellation") is null);
                    var safe = leftAfter == after && rightAfter == after;
                    results.Add(new("SOURCE-RELOAD-MERGE-SAFETY", safe ? "PASS" : "FAIL",
                        $"Equal displayed buffers had stale differences={staleCount}. Reflected MergeCurrent(DiffSide.Right), the right-to-left arrow command, changed left={leftAfter != after}, changed right={rightAfter != after}; expected={JsonSerializer.Serialize(after)}, leftAfter={JsonSerializer.Serialize(leftAfter)}, rightAfter={JsonSerializer.Serialize(rightAfter)}, differences after action={view.DifferenceCount}. No physical arrow click claimed."));
                }
            }
            catch (Exception exception)
            {
                results.Add(new("SOURCE-RELOAD-MERGE-SAFETY", "ERROR", exception.ToString()));
            }
        }
        finally { view.ReleaseResources(); window.Close(); }
    }

    private static void VerifyClipboardLock(List<Observation> results)
    {
        var endpoint = new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "클립보드", Text = "clipboard", IsReadOnly = true };
        var (window, view) = Show(endpoint, Endpoint("right", "right"), Callbacks());
        try
        {
            Field<CheckBox>(view, "LeftReadOnlyCheck").IsChecked = false;
            var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
            editor.ReplaceAll("editable clipboard");
            var meta = Field<TextBlock>(view, "LeftMetaText").Text;
            var textChanged = editor.GetText() == "editable clipboard";
            var unlockedConsistent = textChanged && !editor.IsReadOnly && meta.Contains("편집 가능", StringComparison.Ordinal) && !meta.Contains("읽기 전용", StringComparison.Ordinal);
            Field<CheckBox>(view, "LeftReadOnlyCheck").IsChecked = true;
            var relockedMeta = Field<TextBlock>(view, "LeftMetaText").Text;
            var relockedConsistent = editor.IsReadOnly && relockedMeta.Contains("읽기 전용", StringComparison.Ordinal) && !relockedMeta.Contains("편집 가능", StringComparison.Ordinal);
            results.Add(new("CLIPBOARD-LOCK-METADATA", unlockedConsistent && relockedConsistent ? "PASS" : "FAIL",
                $"Unlock/edit/relock round trip: text changed={textChanged}, unlocked consistent={unlockedConsistent}, unlocked metadata='{meta}', relocked readOnly={editor.IsReadOnly}, relocked consistent={relockedConsistent}, relocked metadata='{relockedMeta}'"));
        }
        finally { view.ReleaseResources(); window.Close(); }
    }

    private static void VerifyPartialReloadCancel(List<Observation> results)
    {
        var leftId = Guid.NewGuid();
        var rightId = Guid.NewGuid();
        long revision = 0;
        const string refreshedLeft = "head\nlatest left\nextra line\ntail";
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => revision,
            GetSourceSnapshot = id => new(id == leftId ? "left source" : "right source",
                id == leftId ? refreshedLeft : "head\nlatest right original\ntail", null,
                new UTF8Encoding(false), false, "\n", revision, id),
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
            SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask,
            SettingsChanged = _ => { }
        };
        var leftEndpoint = new DiffEndpoint
        { Kind = DiffEndpointKind.OpenDocument, DisplayName = "left source", Text = "head\nold left\ntail", SourceDocumentId = leftId, SourceRevision = 0, NewLine = "\n" };
        var rightEndpoint = new DiffEndpoint
        { Kind = DiffEndpointKind.OpenDocument, DisplayName = "right source", Text = "head\nold right\ntail", SourceDocumentId = rightId, SourceRevision = 0, NewLine = "\n" };
        var (window, view) = Show(leftEndpoint, rightEndpoint, callbacks);
        try
        {
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            right.ReplaceAll(refreshedLeft);
            PumpUntil(() => Field<TextDiffResult?>(view, "_result") is not null && !Field<bool>(view, "_needsComparison") && Field<CancellationTokenSource?>(view, "_comparisonCancellation") is null);
            var oldCount = view.DifferenceCount;
            revision = 1;
            view.RefreshSourceState();
            var generation = Field<int>(view, "_generation");
            var button = Descendants(view).OfType<Button>().Single(item => Equals(item.Content, "원본 다시 불러오기"));
            Respond(window, 7, () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            PumpUntil(() => Field<int>(view, "_generation") > generation && Field<TextDiffResult?>(view, "_result") is not null && !Field<bool>(view, "_needsComparison") && Field<CancellationTokenSource?>(view, "_comparisonCancellation") is null);
            var expectedDifferences = new TextDiffEngine().Compare(refreshedLeft, refreshedLeft).Blocks.Count;
            var leftMatches = left.GetText() == refreshedLeft;
            var rightPreserved = right.GetText() == refreshedLeft && Field<bool>(view, "_rightDirty");
            var revisionsMatch = leftEndpoint.SourceRevision == 1 && rightEndpoint.SourceRevision == 0;
            var pass = oldCount > 0 && leftMatches && rightPreserved && revisionsMatch &&
                !Field<bool>(view, "_leftDirty") && view.DifferenceCount == expectedDifferences &&
                Field<Border>(view, "StaleBanner").Visibility == Visibility.Visible;
            results.Add(new("SOURCE-RELOAD-PARTIAL-CANCEL", pass ? "PASS" : "FAIL",
                $"Both linked originals stale; actual Reload Button.Click routed event reloads clean left, then owned native No declines dirty right replacement. Initial differences={oldCount}, left latest={leftMatches}, right local text/dirty preserved={rightPreserved}, left source revision={leftEndpoint.SourceRevision}, right source revision={rightEndpoint.SourceRevision}, left dirty={Field<bool>(view, "_leftDirty")}, current differences={view.DifferenceCount}, expected fresh differences={expectedDifferences}, right stale banner={Field<Border>(view, "StaleBanner").Visibility}. Native response is restricted to the test process, owner window and UI thread."));
        }
        finally { view.ReleaseResources(); window.Close(); }
    }

    private static void VerifyOverlappingSave(string folder, int round, List<Observation> results)
    {
        var path = Path.Combine(folder, $"overlap-{round}.txt");
        File.WriteAllText(path, "seed", new UTF8Encoding(false));
        // A directly addressed file fixture intentionally has no historical size/time baseline.
        // This isolates save ordering from the independent external-file-change prompt.
        var endpoint = new DiffEndpoint { Kind = DiffEndpointKind.File, DisplayName = Path.GetFileName(path), Text = "seed", FilePath = path, NewLine = "\n" };
        var (window, view) = Show(endpoint, Endpoint("right", "seed"), Callbacks());
        try
        {
            view.SetActive(false);
            var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
            editor.ReplaceAll(new string('A', 64 * 1024 * 1024));
            var first = (Task<bool>)Call(view, "SaveEndpointCoreAsync", true, false)!;
            var firstWasPending = !first.IsCompleted;
            var expected = $"newest edit round {round}";
            editor.ReplaceAll(expected);
            var second = (Task<bool>)Call(view, "SaveEndpointCoreAsync", true, false)!;
            var overlapped = firstWasPending && !first.IsCompleted;
            PumpUntil(() => first.IsCompleted && second.IsCompleted, 30000);
            var firstAccepted = first.GetAwaiter().GetResult();
            var secondAccepted = second.GetAwaiter().GetResult();
            var diskMatches = File.ReadAllText(path) == expected;
            var editorMatches = editor.GetText() == expected;
            var state = diskMatches && editorMatches ? "PASS" : "FAIL";
            if (!overlapped && state == "PASS") state = "INCONCLUSIVE";
            results.Add(new($"OVERLAPPING-SAVE-{round}", state,
                $"Two same-side saves (64 MiB then {expected.Length} chars): overlap observed={overlapped}, first accepted={firstAccepted}, second accepted={secondAccepted}, disk matches latest={diskMatches}, editor matches latest={editorMatches}, native modified={editor.IsModified}, view unsaved={view.HasUnsavedChanges}. No external-change baseline was set on this fixture."));
        }
        finally { view.ReleaseResources(); window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void PumpUntil(Func<bool> done, int timeoutMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Merge source exploration did not settle");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
    }

    private static void Respond(Window owner, int command, Action action)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var threadId = GetWindowThreadProcessId(ownerHandle, out var processId);
        if (processId != Environment.ProcessId) throw new InvalidOperationException("Dialog response must target this test process only");
        using var cancellation = new CancellationTokenSource();
        var response = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                var responded = false;
                EnumThreadWindows(threadId, (dialog, _) =>
                {
                    if (GetWindow(dialog, 4) != ownerHandle || !IsWindowVisible(dialog)) return true;
                    var className = new StringBuilder(32); GetClassName(dialog, className, className.Capacity);
                    if (className.ToString() != "#32770") return true;
                    var button = GetDlgItem(dialog, command);
                    if (button == IntPtr.Zero || !IsWindowEnabled(button)) return true;
                    responded = PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    return !responded;
                }, IntPtr.Zero);
                if (responded) return true;
                Thread.Sleep(20);
            }
            return false;
        });
        try
        {
            action();
            if (!response.GetAwaiter().GetResult()) throw new InvalidOperationException("Expected owned reload confirmation was not shown");
        }
        finally { cancellation.Cancel(); response.GetAwaiter().GetResult(); }
    }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
