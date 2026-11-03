using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;

internal static class MergeLifetimeVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static Task<bool> Call(DiffTabView view, string name, params object[] args) =>
        (Task<bool>)typeof(DiffTabView).GetMethod(name, Private)!.Invoke(view, args)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PumpUntil(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(20))
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(done(), "Timed out waiting for Merge operation");
    }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var folder = Path.Combine(Path.GetTempPath(), "OmniEdit-MergeLifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            VerifyWorkspaceClose(app);
            foreach (var release in new[] { false, true })
            {
                var gate = new TaskCompletionSource<bool>();
                var callbacks = new DiffWindowCallbacks
                {
                    GetSourceRevision = _ => 0, GetSourceSnapshot = _ => null,
                    ApplyToSourceAsync = (_, _, _, _) => gate.Task, SaveSourceAsync = _ => Task.FromResult(true),
                    CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
                };
                var endpoint = new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "source", Text = "original", NewLine = "\n", IsReadOnly = false, SourceDocumentId = Guid.NewGuid() };
                var view = new DiffTabView(endpoint, DiffEndpoint.Empty("right"), new(), callbacks, new("Consolas", 11, false));
                var window = new Window { Content = view, Width = 900, Height = 600, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    PumpUntil(() => Field<bool>(view, "_initialized"));
                    var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
                    editor.ReplaceAll("pending apply");
                    var task = Call(view, "ApplyToSourceAsync", true, false);
                    Check(!task.IsCompleted, "Apply callback was not suspended");
                    if (release) view.ReleaseResources(); else editor.ReplaceAll("new unsaved edit");
                    gate.SetResult(true);
                    PumpUntil(() => task.IsCompleted);
                    Check(!task.GetAwaiter().GetResult(), "Stale Apply completion accepted");
                    if (!release) Check(editor.IsModified && view.HasUnsavedChanges, "Apply marked later edits saved");
                }
                finally { view.ReleaseResources(); window.Close(); }
            }

            foreach (var release in new[] { false, true })
            {
                var callbacks = new DiffWindowCallbacks
                {
                    GetSourceRevision = _ => 0, GetSourceSnapshot = _ => null,
                    ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
                    CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
                };
                var path = Path.Combine(folder, release ? "closed.txt" : "edited.txt");
                var endpoint = new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "save", Text = "original", FilePath = path, NewLine = "\n", IsReadOnly = false };
                var view = new DiffTabView(endpoint, DiffEndpoint.Empty("right"), new(), callbacks, new("Consolas", 11, false));
                var window = new Window { Content = view, Width = 900, Height = 600, ShowInTaskbar = false };
                try
                {
                    window.Show();
                    PumpUntil(() => Field<bool>(view, "_initialized"));
                    var editor = Field<ScintillaEditorHost>(view, "_leftEditor");
                    editor.ReplaceAll(new string('x', 8 * 1024 * 1024));
                    var task = Call(view, "SaveEndpointAsync", true);
                    Check(!task.IsCompleted, "File save did not suspend; overlap was not exercised");
                    if (release) view.ReleaseResources(); else editor.ReplaceAll("new unsaved edit");
                    PumpUntil(() => task.IsCompleted);
                    Check(!task.GetAwaiter().GetResult(), "Stale Save completion accepted");
                    Check(new FileInfo(path).Length >= 8 * 1024 * 1024, "Captured content was not saved");
                    if (!release) Check(editor.IsModified && view.HasUnsavedChanges, "Save marked later edits saved");
                }
                finally { view.ReleaseResources(); window.Close(); }
            }
            Console.WriteLine("Merge lifecycle: delayed apply/save after edit or release passed");
            return 0;
        }
        finally { Directory.Delete(folder, true); app.Shutdown(); }
    }

    private static void VerifyWorkspaceClose(Application app)
    {
        foreach (var prepared in new[] { false, true })
        {
            var callbacks = new DiffWindowCallbacks
            {
                GetSourceRevision = _ => 0, GetSourceSnapshot = _ => null,
                ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
                CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
            };
            var window = new DiffWorkspaceWindow(new(), callbacks, new("Consolas", 11, false)) { ShowInTaskbar = false };
            var view = window.OpenEmptyTab();
            var closed = false;
            var releases = 0;
            Exception? failure = null;
            void ObserveFailure(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                failure = e.Exception;
                e.Handled = true;
            }
            window.Closed += (_, _) => closed = true;
            window.ResourcesReleased += (_, _) => releases++;
            app.DispatcherUnhandledException += ObserveFailure;
            try
            {
                window.Show();
                PumpUntil(() => Field<bool>(view, "_initialized"));
                if (prepared)
                {
                    var preparation = window.RequestCloseAsync();
                    PumpUntil(() => preparation.IsCompleted);
                    Check(preparation.GetAwaiter().GetResult(), "Clean Merge close preparation failed");
                }
                window.Close();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(failure is null, $"Merge normal Close raised {failure}");
                Check(closed && releases == 1 && Field<bool>(view, "_resourcesReleased"), "Merge close did not release resources exactly once");
            }
            finally
            {
                app.DispatcherUnhandledException -= ObserveFailure;
                if (!closed) window.Close();
            }
        }
        Console.WriteLine("Merge workspace: clean and preapproved normal Close passed");
    }
}
