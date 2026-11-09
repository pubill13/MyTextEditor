using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using MenuItem = System.Windows.Controls.MenuItem;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;

internal static class MergeWorkflowExploration
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record Observation(string Case, string Outcome, string Evidence);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static int Run()
    {
        var results = new List<Observation>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var folder = Path.Combine(Path.GetTempPath(), "OmniEdit-MergeWorkflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var main = new MainWindow { ShowInTaskbar = false };
        Set(main, "_settingsReady", false);
        Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
        Call(main, "StopFileSync");
        main.Show(); Pump();
        void Observe(string id, Func<string> test)
        {
            try { results.Add(new(id, "PASS", test())); }
            catch (Exception exception) { results.Add(new(id, "FAIL", exception.ToString())); }
            Console.WriteLine($"{results[^1].Outcome} {id}: {results[^1].Evidence}");
        }
        DiffWorkspaceWindow Workspace()
        {
            var old = Field<DiffWorkspaceWindow?>(main, "_diffWorkspaceWindow");
            if (old is not null) ForceClose(old);
            var toggle = Field<ToggleButton>(main, "DiffToggleButton");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var workspace = Field<DiffWorkspaceWindow>(main, "_diffWorkspaceWindow");
            Check(workspace.IsVisible, "MainWindow routed toggle did not show Merge workspace");
            return workspace;
        }
        DiffTabView Linked(DiffWorkspaceWindow workspace, string source, string right)
        {
            var document = main.Documents[0];
            document.Editor.LoadUtf8(Encoding.UTF8.GetBytes(source));
            document.Editor.MarkSaved(); document.IsModified = false;
            var createEndpoint = typeof(MainWindow).GetMethod("CreateDocumentEndpoint", BindingFlags.Static | BindingFlags.NonPublic)!;
            var endpoint = (DiffEndpoint)createEndpoint.Invoke(null, [document])!;
            var view = workspace.OpenComparison(endpoint, Endpoint("right", right));
            Ready(view);
            return view;
        }
        try
        {
            Observe("WF-01-MAIN-ENTRY", () =>
            {
                var workspace = Workspace();
                Check(workspace.ComparisonCount == 1 && Current(workspace).IsCompletelyEmpty, "Entry should create one empty comparison");
                return "Actual MainWindow comparison toggle Click routed event opens one empty Merge tab.";
            });
            Observe("WF-02-HIDE-RESTORE", () =>
            {
                var workspace = Workspace();
                var view = Current(workspace);
                PumpUntil(() => Field<bool>(view, "_initialized"));
                Field<ScintillaEditorHost>(view, "_leftEditor").ReplaceAll("retain this work");
                var toggle = Field<ToggleButton>(main, "DiffToggleButton");
                toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!workspace.IsVisible, "Toggle did not hide workspace");
                toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(ReferenceEquals(workspace, Field<DiffWorkspaceWindow>(main, "_diffWorkspaceWindow")) && Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == "retain this work", "Hide/show lost work");
                return "MainWindow toggle hide/show retains the same workspace, tab and unsaved native editor text.";
            });
            Observe("WF-03-OPEN-DOCUMENT-SNAPSHOT", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "original document", "other");
                var callbacks = (DiffWindowCallbacks)Call(main, "CreateDiffCallbacks")!;
                var snapshot = callbacks.GetOpenDocuments!().Single(item => item.SourceDocumentId == main.Documents[0].Id);
                Check(snapshot.Text == "original document", "Real callbacks did not expose source document");
                Field<ScintillaEditorHost>(view, "_leftEditor").ReplaceAll("Merge-local edit");
                Check(main.Documents[0].Text == "original document", "Editing Merge snapshot changed original without Apply");
                return "Real MainWindow snapshot builder/callbacks used via reflection; Merge local edit remains isolated until Apply.";
            });
            Observe("WF-04-APPLY-ORIGINAL-UNDO", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "before apply", "other");
                Field<ScintillaEditorHost>(view, "_leftEditor").ReplaceAll("after apply");
                Click(view, "원본 반영");
                Pump();
                Check(main.Documents[0].Text == "after apply", "Apply did not update actual main document");
                main.Documents[0].Editor.Undo();
                Check(main.Documents[0].Text == "before apply", "Apply should be one Undo in original document");
                return "Actual Apply button Click routed event updates real MainWindow document; one native Undo restores original.";
            });
            Observe("WF-05-STALE-APPLY-REJECTION", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "original", "other");
                Field<ScintillaEditorHost>(view, "_leftEditor").ReplaceAll("stale local proposal");
                main.Documents[0].Editor.ReplaceAll("newer original");
                Click(view, "원본 반영"); Pump();
                Check(main.Documents[0].Text == "newer original" && view.HasUnsavedChanges, "Stale Apply overwrote new original or lost local dirty state");
                return "Actual MainWindow callbacks reject stale Apply and preserve both newer original and unsaved Merge proposal.";
            });
            Observe("WF-06-REAL-LINKED-RELOAD-CACHE", () =>
            {
                var workspace = Workspace();
                const string updated = "head\nnew source\ntail";
                var view = Linked(workspace, "head\nold source\ntail", updated);
                var oldCount = view.DifferenceCount;
                main.Documents[0].Editor.ReplaceAll(updated);
                view.RefreshSourceState();
                var generation = Field<int>(view, "_generation");
                Click(view, "원본 다시 불러오기");
                PumpUntil(() => Field<int>(view, "_generation") > generation && Field<TextDiffResult?>(view, "_result") is not null && Field<CancellationTokenSource?>(view, "_comparisonCancellation") is null);
                var displayed = Field<ScintillaEditorHost>(view, "_leftEditor").GetText();
                Check(displayed == updated && view.DifferenceCount == 0, $"Real linked source reload: initial differences={oldCount}, displayed latest={displayed == updated}, post-reload differences={view.DifferenceCount}, expected=0");
                return "Actual MainWindow source callbacks + Reload button routed event: refreshed text and comparison agree.";
            });
            Observe("WF-07-EXTRACT-NEW-DOCUMENT", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "extract 😀 한글", "right");
                var count = main.Documents.Count;
                Click(view, "새 문서"); Pump();
                Check(main.Documents.Count == count + 1 && main.Documents.Last().Text == "extract 😀 한글", "Extract did not create actual main document with exact text");
                return "Actual new-document button routed event creates a real main document with exact Unicode text.";
            });
            Observe("WF-08-CLOSE-CANCEL", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "before", "right");
                Field<ScintillaEditorHost>(view, "_leftEditor").ReplaceAll("unsaved after");
                var count = workspace.ComparisonCount;
                var item = Field<TabControl>(workspace, "ComparisonTabs").SelectedItem!;
                bool closed = true;
                Respond(workspace, 2, () => closed = Wait((Task<bool>)Call(workspace, "CloseTabAsync", item)!));
                Check(!closed && workspace.ComparisonCount == count && Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == "unsaved after", "Cancel close lost comparison or text");
                return "CloseTabAsync UI handler operation via reflection; native owned save prompt Cancel preserves dirty tab and exact text.";
            });
            Observe("WF-09-CLOSE-DISCARD", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "before", "right");
                Field<ScintillaEditorHost>(view, "_leftEditor").ReplaceAll("discard this");
                var count = workspace.ComparisonCount;
                var item = Field<TabControl>(workspace, "ComparisonTabs").SelectedItem!;
                bool closed = false;
                Respond(workspace, 7, () => closed = Wait((Task<bool>)Call(workspace, "CloseTabAsync", item)!));
                Check(closed && workspace.ComparisonCount == count - 1 && Field<bool>(view, "_resourcesReleased"), "Discard did not close/release only requested tab");
                return "CloseTabAsync UI operation via reflection; native owned prompt No discards and releases only selected comparison.";
            });
            var firstPath = Path.Combine(folder, "first 한글.txt");
            var secondPath = Path.Combine(folder, "second.txt");
            var thirdPath = Path.Combine(folder, "third.txt");
            File.WriteAllText(firstPath, "first", new UTF8Encoding(false));
            File.WriteAllText(secondPath, "second", new UTF8Encoding(false));
            File.WriteAllText(thirdPath, "third", new UTF8Encoding(false));
            Observe("WF-10-DROP-ONE", () =>
            {
                var workspace = Workspace();
                Wait(workspace.OpenDroppedFilesAsync([firstPath]));
                var view = Current(workspace);
                Check(view.LeftIsReady && !view.RightIsReady && Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == "first", "One-file drop did not fill first empty left side");
                return "Public drop-handler operation (not physical drag) loads one Unicode-path file into empty left side.";
            });
            Observe("WF-11-DROP-TWO", () =>
            {
                var workspace = Workspace();
                Wait(workspace.OpenDroppedFilesAsync([firstPath, secondPath]));
                var view = Current(workspace); Ready(view);
                Check(workspace.ComparisonCount == 1 && Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == "first" && Field<ScintillaEditorHost>(view, "_rightEditor").GetText() == "second", "Two-file drop order/reuse failed");
                return "Public drop operation reuses empty tab, keeps left/right input order, and compares both files.";
            });
            Observe("WF-12-DROP-THREE", () =>
            {
                var workspace = Workspace();
                Wait(workspace.OpenDroppedFilesAsync([firstPath, secondPath, thirdPath]));
                var view = Current(workspace); Ready(view);
                var status = Field<TextBlock>(workspace, "StatusText").Text;
                Check(workspace.ComparisonCount == 1 && Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == "first" && Field<ScintillaEditorHost>(view, "_rightEditor").GetText() == "second" && status.Contains("1개"), "Third-file exclusion lost order or user feedback");
                return "Three-file drop loads first two in order and explicitly reports one excluded item: " + status;
            });
            Observe("WF-13-DROP-MISSING-ATOMIC", () =>
            {
                var workspace = Workspace();
                var view = Linked(workspace, "existing left", "existing right");
                var count = workspace.ComparisonCount;
                Wait(workspace.OpenDroppedFilesAsync([Path.Combine(folder, "missing.txt"), firstPath]));
                Check(workspace.ComparisonCount == count && ReferenceEquals(Current(workspace), view) && Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == "existing left" && Field<ScintillaEditorHost>(view, "_rightEditor").GetText() == "existing right", "Failed pair drop changed existing comparison");
                Check(Field<TextBlock>(workspace, "StatusText").Text.Contains("못해"), "Failed pair drop omitted error feedback");
                return "Missing+valid pair reports failure and preserves existing tab count, selection and both contents.";
            });
            Observe("WF-14-EXTERNAL-CHANGE-SAVE-CANCEL", () =>
            {
                var workspace = Workspace();
                var path = Path.Combine(folder, "external.txt");
                File.WriteAllText(path, "disk original", new UTF8Encoding(false));
                var endpoint = Wait(DiffTabView.LoadFileEndpointAsync(path));
                var view = workspace.OpenComparison(endpoint, Endpoint("right", "right")); Ready(view);
                var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
                editor.ReplaceAll("local unsaved edit");
                File.WriteAllText(path, "externally changed disk content", new UTF8Encoding(false));
                bool saved = true;
                Respond(workspace, 2, () => saved = Wait((Task<bool>)Call(view, "SaveEndpointAsync", true)!));
                Check(!saved && File.ReadAllText(path) == "externally changed disk content" && editor.GetText() == "local unsaved edit" && view.HasUnsavedChanges, "External-change save cancellation lost disk or local text/dirty state");
                return "Actual loaded file baseline + Save UI handler via reflection; native external-change Cancel preserves disk and unsaved local buffer.";
            });
            Observe("WF-15-IDENTICAL-COPY-DIRTY", () =>
            {
                var workspace = Workspace();
                var view = workspace.OpenComparison(Endpoint("left", "same\ncontent"), Endpoint("right", "same\ncontent")); Ready(view);
                var editor = Field<ScintillaEditorHost>(view, "_rightEditor");
                var undoBefore = editor.CanUndo;
                Field<MenuItem>(view, "CopyAllRightButton").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check(!view.HasUnsavedChanges && editor.CanUndo == undoBefore, $"Identical full copy leaves text equal but view unsaved={view.HasUnsavedChanges}, native modified={editor.IsModified}, Undo before={undoBefore}, after={editor.CanUndo}");
                return "Identical-content full-copy menu Click routed event creates neither dirty state nor a new Undo action.";
            });
        }
        finally
        {
            var workspace = Field<DiffWorkspaceWindow?>(main, "_diffWorkspaceWindow");
            if (workspace is not null) ForceClose(workspace);
            Set(main, "_settingsReady", false); Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
            Call(main, "StopFileSync");
            foreach (var document in main.Documents) document.Editor.ReleaseResources();
            Set(main, "_allowClose", true); main.Close(); app.Shutdown();
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
            var output = Path.GetFullPath(Path.Combine("artifacts", "merge-exploration-fixed", "workflow-results.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                generatedUtc = DateTime.UtcNow,
                method = "Real isolated MainWindow and its actual Merge callbacks. Routed Click events where stated; other UI handler operations invoked via reflection/public API. Native owned dialogs answered automatically. No physical drag/keyboard or Explorer dialog claims.",
                isolation = "Plain WPF Application (no App RecoveryStore); MainWindow settingsReady=false, settings timer stopped, file sync stopped; test fixtures only in unique temp directory.",
                results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Workflow observations: " + output);
        }
        return results.Any(result => result.Outcome == "FAIL") ? 1 : 0;
    }

    private static DiffEndpoint Endpoint(string name, string text) => new() { Kind = DiffEndpointKind.Scratch, DisplayName = name, Text = text, NewLine = "\n" };
    private static DiffTabView Current(DiffWorkspaceWindow window) => (DiffTabView)((TabItem)Field<TabControl>(window, "ComparisonTabs").SelectedItem).Content;
    private static void Ready(DiffTabView view) => PumpUntil(() => Field<bool>(view, "_initialized") && Field<TextDiffResult?>(view, "_result") is not null);
    private static void ForceClose(DiffWorkspaceWindow workspace)
    {
        foreach (var tab in Field<TabControl>(workspace, "ComparisonTabs").Items.OfType<TabItem>()) ((DiffTabView)tab.Content).ReleaseResources();
        Set(workspace, "_closingApproved", true); workspace.Close(); Pump();
    }
    private static void Click(DiffTabView view, string label)
    {
        var button = Descendants(view).OfType<Button>().First(item => Equals(item.Content, label) && item.IsVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Workflow operation timed out"); Pump(); Thread.Sleep(5); }
    }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(); }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }

    private static void Respond(Window owner, int command, Action action)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var threadId = GetWindowThreadProcessId(ownerHandle, out var processId);
        Check(processId == Environment.ProcessId, "Dialog automation must target this test process only");
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
        try { action(); Check(response.GetAwaiter().GetResult(), "Expected owned modal dialog was not shown"); }
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
