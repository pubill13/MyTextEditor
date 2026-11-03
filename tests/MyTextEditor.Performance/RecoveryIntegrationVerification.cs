using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Models;
using MyTextEditor.Services;

internal static class RecoveryIntegrationVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class TestApp : App { protected override void OnStartup(StartupEventArgs e) { } }
    private static object? Call(object target, string method, params object?[] args) =>
        (target is App ? typeof(App) : typeof(MainWindow)).GetMethod(method, Hidden)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Task<bool> Checkpoint(MainWindow window, MyTextEditor.Models.DocumentViewModel document) =>
        (Task<bool>)Call(window, "EnsureRecoveryCheckpointAsync", document)!;
    private static void SetStore(App app, RecoveryStore store) => typeof(App).GetProperty("RecoveryStore", Hidden)!.SetValue(app, store);
    private static void Isolate(MainWindow window)
    {
        typeof(MainWindow).GetField("_settingsReady", Hidden)!.SetValue(window, false);
        ((DispatcherTimer)typeof(MainWindow).GetField("_settingsSaveTimer", Hidden)!.GetValue(window)!).Stop();
        Call(window, "StopFileSync");
    }

    public static int Run()
    {
        var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var frame = new DispatcherFrame();
        Exception? failure = null;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await VerifyAsync(app); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        app.Shutdown();
        if (failure is not null) throw failure;
        return 0;
    }

    private static async Task VerifyAsync(App app)
    {
        var artifactRoot = Path.GetFullPath(Path.Combine("artifacts", "recovery-ui-tests"));
        var root = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RecoveryStore? store = new(root);
        SetStore(app, store);
        var window = new MainWindow();
        Isolate(window);
        try
        {
            var document = window.Documents[0];
            document.Editor.ReplaceAll("복구 테스트 😀\r\nsecond");
            document.ChangeEncoding(DocumentEncodingKind.Utf8Bom);
            document.Editor.SelectUtf16Range(1, 3);
            Call(app, "RecoveryTimer_Tick", null, EventArgs.Empty);
            var watch = Stopwatch.StartNew();
            while ((bool)typeof(App).GetField("_recoveryTickRunning", Hidden)!.GetValue(app)! && watch.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(10);
            var path = Directory.GetFiles(root, "*.recovery", SearchOption.AllDirectories).Single();
            var original = await File.ReadAllBytesAsync(path);
            Check(await Checkpoint(window, document), "Unchanged checkpoint rejected");
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Unchanged document was captured again");

            document.Editor.ReplaceAll("checkpoint write failure");
            await using (var denyReplace = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var count = app.Windows.Count;
                await (Task)Call(window, "CompleteDocumentTransferAsync", document, null, false, null, new System.Windows.Point(1100, 120))!;
                Check(window.Documents.Contains(document) && app.Windows.Count == count && document.IsModified,
                    "Checkpoint failure moved or lost the document");
            }
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Failed transfer replaced the recovery checkpoint");
            typeof(MainWindow).GetField("_closingAllDocuments", Hidden)!.SetValue(window, true);
            await (Task)Call(window, "CaptureRecoveryDocumentsAsync")!;
            typeof(MainWindow).GetField("_closingAllDocuments", Hidden)!.SetValue(window, false);
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Close-all allowed periodic checkpoint publication");

            document.Editor.ReplaceAll(new string('a', 3_000_000));
            var capturing = Checkpoint(window, document);
            document.Editor.ReplaceAll("edited during capture");
            Check(!await capturing, "Edited capture was published");
            Check(original.SequenceEqual(await File.ReadAllBytesAsync(path)), "Cancelled capture replaced previous checkpoint");
            Check(await Checkpoint(window, document), "Retry checkpoint failed");
            document.Editor.ReplaceAll(new string('b', 3_000_000));
            capturing = Checkpoint(window, document);
            Check(await (Task<bool>)Call(window, "DiscardRecoveryAsync", document)!, "Discard failed");
            Check(!await capturing && !File.Exists(path), "Discard allowed stale capture to recreate backup");
            document.Editor.ReplaceAll("복원 후 내용 😀\r\nsecond");
            document.Editor.SelectUtf16Range(1, 3);
            Check(await Checkpoint(window, document), "Final checkpoint failed");
            await store.DisposeAsync();
            store = new RecoveryStore(root);
            SetStore(app, store);
            var claims = await store.ClaimOrphanSessionsAsync();
            try
            {
                Check(claims.Count == 1, "Previous session not available");
                var snapshot = await claims[0].ReadAsync(document.Id);
                Check(await (Task<bool>)Call(window, "RestoreRecoveryAsync", snapshot)!, "Restore rejected");
                var restored = window.Documents.Last();
                Check(restored.Text == snapshot.Text && restored.IsModified && restored.HasByteOrderMark, "Recovered state changed");
                Check(!restored.Editor.CanUndo && restored.Editor.SelectionRange.Start == 1, "Recovery restored Undo or lost selection");
                restored.Editor.ReplaceAll("after restore"); restored.Editor.Undo();
                Check(restored.IsModified && restored.Text == snapshot.Text, "Undo incorrectly marks restored document saved");
                restored.FilePath = Path.Combine(root, "saved.txt");
                Check(await (Task<bool>)Call(window, "SaveDocumentAsync", restored, false)!, "Recovered document could not save");
                Check(!restored.IsModified && File.ReadAllText(restored.FilePath) == snapshot.Text, "Save did not clear recovery modified state");
                Check(!Directory.GetFiles(root, restored.Id.ToString("N") + ".recovery", SearchOption.AllDirectories).Any(), "Saved recovery not removed");
                await claims[0].DiscardAsync(document.Id);
            }
            finally { foreach (var claim in claims) await claim.DisposeAsync(); }
            Console.WriteLine("PASS recovery UI: periodic handler, unchanged snapshot cache, concurrent edit cancellation, discard/capture race, session restore, UTF-8 BOM/selection, Undo dirty state, successful-save cleanup");
        }
        finally
        {
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Hidden)!.SetValue(window, true);
            window.Close();
            if (store is not null) await store.DisposeAsync();
            if (!root.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup");
            Directory.Delete(root, recursive: true);
        }
    }
}
