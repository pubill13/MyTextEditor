using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;

internal static class MergeExternalChangesVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Until(Func<bool> done, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(12))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Check(done(), message);
    }
    private static void Wait(Task task)
    {
        Until(() => task.IsCompleted, "External-change operation timed out");
        task.GetAwaiter().GetResult();
    }
    private static void Poll(DiffTabView view, bool manual = false) =>
        Wait((Task)Call(view, "PollExternalChangesAsync", manual)!);
    private static void Observe(DiffTabView view)
    {
        Poll(view);
        Wait(Task.Delay(550));
        Poll(view);
    }

    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-merge-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.txt");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var confirmation = typeof(DiffTabView).Assembly.GetType("MyTextEditor.Services.ExternalChangeConfirmation")!
            .GetProperty("ConfirmOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousConfirmation = confirmation.GetValue(null);
        var sourceId = Guid.NewGuid();
        var sourceRevision = 1L;
        var sourceText = "initial source\nsecond line";
        var approvals = 0;
        var warnings = 0;
        var approve = true;
        Action? duringConfirmation = null;
        confirmation.SetValue(null, new Func<Window, string, bool, bool>((_, _, dirty) =>
        {
            approvals++;
            if (dirty) warnings++;
            duringConfirmation?.Invoke();
            return approve;
        }));
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = id => id == sourceId ? sourceRevision : -1,
            GetSourceSnapshot = id => id == sourceId
                ? new("linked", sourceText, null, new UTF8Encoding(false), false, "\n", sourceRevision, sourceId) : null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
            SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask,
            SettingsChanged = _ => { }
        };
        DiffTabView? view = null;
        Window? window = null;
        try
        {
            File.WriteAllText(path, sourceText, new UTF8Encoding(false));
            var loading = DiffTabView.LoadFileEndpointAsync(path);
            Wait(loading);
            var fileEndpoint = loading.GetAwaiter().GetResult();
            var linked = new DiffEndpoint
            {
                Kind = DiffEndpointKind.OpenDocument, DisplayName = "linked", Text = sourceText,
                SourceDocumentId = sourceId, SourceRevision = sourceRevision, NewLine = "\n"
            };
            view = new(fileEndpoint, linked, new(), callbacks, new("Consolas", 11, false));
            window = new Window { Content = view, Width = 1000, Height = 650, ShowInTaskbar = false };
            window.Show();
            view.SetActive(true);
            Until(() => Field<bool>(view, "_initialized"), "Merge did not initialize");
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            var timer = Field<DispatcherTimer>(view, "_externalChangeTimer");
            Check(timer.IsEnabled, "Loaded Merge must start external-change timer");

            sourceText = "timer-driven linked update";
            sourceRevision++;
            Until(() => right.GetText() == sourceText, "Real dispatcher timer did not detect/reload a linked document");
            Check(approvals == 1, "Timer-driven linked update must prompt exactly once");
            timer.Stop();
            Console.WriteLine("PASS Merge external-change timer is connected to actual polling");

            approve = false;
            sourceText = "declined linked version";
            sourceRevision++;
            var count = approvals;
            Observe(view);
            Check(approvals == count + 1 && right.GetText() == "timer-driven linked update", "Declining linked update changed buffer or did not prompt");
            Observe(view);
            Check(approvals == count + 1, "Declined linked version was prompted again");
            approve = true;
            Poll(view, manual: true);
            Check(approvals == count + 2 && right.GetText() == sourceText, "Manual linked retry did not reload the declined version");

            left.ReplaceAll("my unsaved file edits");
            File.WriteAllText(path, "new external file version 😀\r\nsecond", Encoding.Unicode);
            approve = false;
            count = approvals;
            var warningCount = warnings;
            Observe(view);
            Check(approvals == count + 1 && warnings == warningCount + 1, "Dirty file reload must issue the unsaved-changes warning");
            Check(left.GetText() == "my unsaved file edits" && view.HasUnsavedChanges, "Declining dirty file reload lost edits");
            Observe(view);
            Check(approvals == count + 1, "Declined file version was prompted again");
            approve = true;
            left.IsReadOnly = true;
            Poll(view, manual: true);
            Check(left.GetText() == "new external file version 😀\r\nsecond" && left.IsReadOnly, "Approved reload must update a locked side and preserve its lock");
            Check(fileEndpoint.Encoding.CodePage == Encoding.Unicode.CodePage && fileEndpoint.HasByteOrderMark && fileEndpoint.NewLine == "\r\n", "File reload lost encoding/BOM/newline metadata");
            Check(!Field<bool>(view, "_leftDirty"), "Accepted file reload must clear discarded local edits");

            sourceText = "next approved linked version";
            sourceRevision++;
            Observe(view);
            Check(right.GetText() == sourceText, "A newer linked version must prompt and reload after a declined version");
            File.WriteAllText(path, sourceText, new UTF8Encoding(false));
            Observe(view);
            Check(left.GetText() == sourceText && view.DifferenceCount == 0, "Reload reused stale snapshot/index data when both sides became equal");
            File.WriteAllText(path, "locked external file version", new UTF8Encoding(false));
            count = approvals;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Observe(view);
                Check(left.GetText() == sourceText, "Read failure replaced the current Merge buffer");
            }
            Observe(view);
            Check(approvals == count + 1 && left.GetText() == sourceText, "Failed file version was prompted repeatedly or silently reapplied");
            Poll(view, manual: true);
            Check(left.GetText() == "locked external file version", "Manual retry after file read failure did not recover");
            Console.WriteLine("PASS linked/file approve, decline once, manual retry, dirty warning, lock and encoding preservation, comparison cache invalidation");

            count = approvals;
            view.SetActive(false);
            sourceText = "inactive source version";
            sourceRevision++;
            Observe(view);
            Check(approvals == count && right.GetText() != sourceText, "Inactive Merge prompted or reloaded");
            view.SetActive(true);
            Observe(view);
            Check(right.GetText() == sourceText, "Reactivated Merge did not detect pending update");
            Call(view, "PauseExternalChanges");
            count = approvals;
            sourceText = "paused source version";
            sourceRevision++;
            Observe(view);
            Check(approvals == count && right.GetText() != sourceText, "Paused Merge prompted or reloaded");
            Call(view, "ResumeExternalChanges");
            Observe(view);
            Check(right.GetText() == sourceText, "Resumed Merge did not process pending update");
            right.ReplaceAll("ordinary edit after repeated reloads");
            Check(right.GetText() == "ordinary edit after repeated reloads" && view.HasUnsavedChanges,
                "Ordinary edit after repeated external reloads did not complete and mark the buffer dirty");
            Console.WriteLine("PASS inactive/paused guards, reactivation, and ordinary editing after repeated reloads");

            sourceText = "must not overwrite concurrent edit";
            sourceRevision++;
            duringConfirmation = () => right.ReplaceAll("edit made during confirmation");
            Observe(view);
            duringConfirmation = null;
            Check(right.GetText() == "edit made during confirmation" && view.HasUnsavedChanges, "Confirmation overwrote a concurrently changed buffer");
            Poll(view, manual: true);
            Check(right.GetText() == sourceText, "Manual retry after concurrent edit did not recover");

            sourceText = "must not apply after pause";
            sourceRevision++;
            duringConfirmation = () => Call(view, "PauseExternalChanges");
            Observe(view);
            duringConfirmation = null;
            Check(right.GetText() != sourceText, "Confirmation applied after external checks were paused");
            Call(view, "ResumeExternalChanges");
            Poll(view, manual: true);
            Check(right.GetText() == sourceText, "Manual retry after paused approval did not recover");

            sourceText = "must not apply after release";
            sourceRevision++;
            duringConfirmation = view.ReleaseResources;
            Observe(view);
            duringConfirmation = null;
            Check(!timer.IsEnabled && Field<bool>(view, "_resourcesReleased"), "Released Merge retained its external polling timer");
            count = approvals;
            Poll(view, manual: true);
            Check(approvals == count, "Released Merge prompted again");
            Console.WriteLine("PASS inactive/paused/released guards, reactivation, and edits/pause/release during confirmation");
            return 0;
        }
        finally
        {
            duringConfirmation = null;
            confirmation.SetValue(null, previousConfirmation);
            view?.ReleaseResources();
            window?.Close();
            app.Shutdown();
            Directory.Delete(directory, recursive: true);
        }
    }
}
