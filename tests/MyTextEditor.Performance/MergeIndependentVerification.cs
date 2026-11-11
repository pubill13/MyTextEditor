using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Help;
using MyTextEditor.Models;
using MyTextEditor.Services;

internal static class MergeIndependentVerification
{
    private sealed class TestApp : App { protected override void OnStartup(StartupEventArgs e) { } }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) =>
        (target is App ? typeof(App) : target.GetType()).GetMethod(name, Private | BindingFlags.Static)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(20))
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(done(), "Timed out waiting for independent Merge operation");
    }
    private static void Isolate(MainWindow window)
    {
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
    }

    public static int Run()
    {
        var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var folder = Path.Combine(Path.GetTempPath(), "OmniEdit-Independent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var hidden in new[] { false, true })
            {
                var main = new MainWindow { ShowInTaskbar = false };
                Isolate(main);
                main.Show();
                var document = main.Documents[0];
                document.Editor.LoadUtf8(Encoding.UTF8.GetBytes("original\nleft"));
                document.IsModified = false;
                var path = Path.Combine(folder, hidden ? "hidden.txt" : "visible.txt");
                File.WriteAllText(path, document.Text, new UTF8Encoding(false));
                document.FilePath = path;
                var workspace = (DiffWorkspaceWindow)Call(main, "EnsureDiffWorkspace")!;
                var endpoint = (DiffEndpoint)Call(main, "CreateDocumentEndpoint", document)!;
                var view = workspace.OpenComparison(endpoint, new DiffEndpoint
                {
                    Kind = DiffEndpointKind.Scratch, DisplayName = "right", Text = "original\nright"
                });
                PumpUntil(() => Field<bool>(view, "_initialized") && view.DifferenceCount > 0);
                var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
                editor.ReplaceAll("edited\nleft");
                editor.SelectUtf16Range(1, 3);
                var selection = editor.SelectionRange;
                var brush = workspace.Background.ToString();
                var retainedPath = Path.Combine(folder, hidden ? "snapshot-hidden.txt" : "snapshot-visible.txt");
                var retained = workspace.OpenComparison(new DiffEndpoint
                {
                    Kind = DiffEndpointKind.OpenDocument, DisplayName = "retained snapshot",
                    Text = "unsaved source snapshot", FilePath = retainedPath, SourceDocumentId = Guid.NewGuid()
                }, DiffEndpoint.Empty("right"));
                PumpUntil(() => Field<bool>(retained, "_initialized"));
                if (hidden) workspace.Hide();
                main.Close();
                PumpUntil(() => !app.Windows.OfType<MainWindow>().Contains(main));
                Check(workspace.IsVisible && workspace.Owner is null && workspace.ComparisonCount == 2,
                    "Main close closed or hid the Merge workspace");
                Check(!Field<bool>(view, "_resourcesReleased") && editor.GetText() == "edited\nleft" && editor.SelectionRange == selection,
                    "Main close released or changed the Merge editor");
                Check(workspace.Background.ToString() == brush, "Detached Merge lost its theme");
                editor.Undo();
                Check(editor.GetText() == "original\nleft", "Detached Merge lost Undo");
                editor.Redo();
                Check(editor.GetText() == "edited\nleft", "Detached Merge lost Redo");
                var rightEditor = Field<ScintillaEditorHost>(view, "_rightEditor");
                Call(view, "CopyAll", DiffSide.Left);
                Check(rightEditor.GetText().Replace("\r\n", "\n") == "edited\nleft", "Detached Merge cannot merge");
                rightEditor.Undo();
                Check(rightEditor.GetText() == "original\nright", "Detached merge Undo failed");
                var detached = Field<DiffEndpoint>(view, "_left");
                Check(detached.SourceDocumentId is null && detached.FilePath == path && view.HasUnsavedChanges,
                    "Detached source remains linked or lost unsaved state");
                Check(retained.HasUnsavedChanges, "Clean snapshot of a closed unsaved source could be silently lost");
                var retainedEditor = Field<ScintillaEditorHost>(retained, "_leftEditor");
                retainedEditor.ReplaceAll("temporary edit");
                retainedEditor.Undo();
                Check(retained.HasUnsavedChanges, "Undo incorrectly marked an unsaved source snapshot saved");
                var retainedSave = (Task<bool>)Call(retained, "SaveEndpointAsync", true)!;
                PumpUntil(() => retainedSave.IsCompleted);
                Check(retainedSave.GetAwaiter().GetResult() && File.ReadAllText(retainedPath) == "unsaved source snapshot",
                    "Retained source snapshot cannot be saved independently");
                var save = (Task<bool>)Call(view, "SaveEndpointAsync", true)!;
                PumpUntil(() => save.IsCompleted);
                Check(save.GetAwaiter().GetResult() && File.ReadAllText(path).Replace("\r\n", "\n") == "edited\nleft", "Detached Merge cannot save");
                var callbacks = Field<DiffWindowCallbacks>(view, "_callbacks");
                callbacks.ShowHelp!();
                Check(app.Windows.OfType<HelpWindow>().Single().Owner == workspace, "Detached help used the closed main window");
                app.Windows.OfType<HelpWindow>().Single().Close();
                callbacks.CreateDocumentAsync("extracted", "extracted text").GetAwaiter().GetResult();
                var extracted = app.Windows.OfType<MainWindow>().Single();
                Isolate(extracted);
                Check(extracted.Documents.Single().Text == "extracted text", "Detached extraction lost text");
                extracted.Documents.Single().Editor.MarkSaved();
                extracted.Documents.Single().IsModified = false;
                extracted.Close();
                PumpUntil(() => !app.Windows.OfType<MainWindow>().Any());
                var launchFiles = hidden ? new[] { path } : Array.Empty<string>();
                Check((bool)Call(app, "TryReceiveFiles", FileOpenRequest.Create(launchFiles))!, "Cannot reopen main with only Merge alive");
                var reopened = app.Windows.OfType<MainWindow>().Single();
                Isolate(reopened);
                if (hidden) PumpUntil(() => reopened.Documents.Any(item => item.FilePath == path));
                reopened.Close();
                PumpUntil(() => !app.Windows.OfType<MainWindow>().Any());
                var exited = false;
                var exitFrame = new DispatcherFrame();
                if (hidden)
                {
                    app.Exit += (_, _) => { exited = true; exitFrame.Continue = false; };
                    app.ShutdownMode = ShutdownMode.OnLastWindowClose;
                }
                workspace.Close();
                PumpUntil(() => !app.Windows.OfType<DiffWorkspaceWindow>().Any());
                Check(Field<bool>(view, "_resourcesReleased"), "Final Merge close leaked its editor");
                if (hidden)
                {
                    if (!exited) Dispatcher.PushFrame(exitFrame);
                    Check(exited, "Closing the last Merge window did not exit the application");
                }
                Console.WriteLine($"PASS independent Merge hidden={hidden}: main Close, multiple tabs, text/selection/Undo/Redo, merge, unsaved snapshot, save, help, extraction, reopen, final Close");
            }
            return 0;
        }
        finally
        {
            app.Shutdown();
            Directory.Delete(folder, true);
        }
    }
}
