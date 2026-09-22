using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using MyTextEditor.Services;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

internal static class FileSyncVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-file-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "external.log");
        File.WriteAllText(file, "초기 AAA\r\n두 번째 😀", new UTF8Encoding(false));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        Set(window, "_settingsReady", false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Field<DispatcherTimer>(window, "_fileSyncTimer").Stop();
        try
        {
            window.Show();
            Toggle(window, false);
            WaitFor(window.OpenStartupFilesAsync([file]));
            var document = window.Documents.Single();
            File.WriteAllText(file, "OFF 중 외부 변경 AAA", new UTF8Encoding(false));
            Poll(window);
            Require(document.Text.StartsWith("초기", StringComparison.Ordinal), "OFF must preserve loaded document");

            Toggle(window, true);
            Poll(window);
            Require(document.Text == "OFF 중 외부 변경 AAA" && !document.IsModified, "ON must reload clean external changes");
            var firstRevision = document.ContentRevision;
            Require(firstRevision > 0, "External reload must advance revision");

            Field<System.Windows.Controls.CheckBox>(window, "FolderSearchCheck").IsChecked = false;
            Field<System.Windows.Controls.CheckBox>(window, "UseConditionSearchCheck").IsChecked = false;
            Field<TextBox>(window, "LiteralFindBox").Text = "AAA";
            Call(window, "Search_Click", window, new RoutedEventArgs());
            var snapshotRevision = window.SearchSessions.Last().Snapshot!.Revision;
            File.WriteAllText(file, "세 번째 상태\nUTF-16 한글 😀", Encoding.Unicode);
            Poll(window);
            Require(document.ContentRevision > snapshotRevision, "Search snapshots must become stale after reload");
            Require(document.Text == "세 번째 상태\nUTF-16 한글 😀" && document.NewLine == "\n" &&
                document.Encoding.CodePage == Encoding.Unicode.CodePage && document.HasByteOrderMark,
                "Reload must preserve detected UTF-16 and line endings");

            document.Editor.ReplaceAll("저장하지 않은 내 수정");
            File.WriteAllText(file, "외부 충돌 내용", new UTF8Encoding(false));
            Poll(window);
            Require(document.Text == "저장하지 않은 내 수정" && document.IsModified,
                "Dirty document must never be overwritten by auto reload");
            Require(Field<System.Windows.Controls.TextBlock>(window, "StatusMessage").Text.Contains("수정 내용", StringComparison.Ordinal),
                "Dirty conflict must be reported");
            document.Editor.Undo();
            Require(!document.IsModified, "Undo to save point must be clean");
            Poll(window);
            Require(document.Text == "외부 충돌 내용", "Conflict must reload once local edits are undone");

            File.Delete(file);
            Poll(window);
            Require(document.Text == "외부 충돌 내용", "Deletion must preserve buffer");
            File.WriteAllText(file, "재생성됨", new UTF8Encoding(false));
            Poll(window);
            Require(document.Text == "재생성됨", "Recreated file must reload");

            File.WriteAllText(file, "잠긴 외부 변경 내용", new UTF8Encoding(false));
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Poll(window);
                Require(document.Text == "재생성됨", "Locked file must preserve buffer");
            }
            Poll(window);
            Require(document.Text == "잠긴 외부 변경 내용", "Unlocked file must retry successfully");

            document.Editor.ReplaceAll("직접 저장한 내용");
            WaitFor((Task)Call(window, "SaveDocumentAsync", document, false)!);
            var savedRevision = document.ContentRevision;
            Poll(window);
            Require(document.ContentRevision == savedRevision && document.Editor.CanUndo,
                "Own save must not cause reload and discard Undo");

            File.WriteAllText(file, "비동기 진행 중 OFF", new UTF8Encoding(false));
            var inFlight = (Task)Call(window, "PollFileChangesAsync")!;
            Toggle(window, false);
            WaitFor(inFlight);
            Require(document.Text == "직접 저장한 내용", "Turning OFF must cancel pending reload application");
            Toggle(window, true);
            Poll(window);
            Require(document.Text == "비동기 진행 중 OFF", "Turning ON again must resume after a cancelled poll");
            Console.WriteLine("PASS file sync OFF/ON, monotonic revision, encoding, dirty guard, deletion/recreation, locked retry, own-save Undo, pending OFF");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            throw;
        }
        finally
        {
            Call(window, "StopFileSync");
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
            window.Hide();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            Set(window, "_allowClose", true);
            window.Close();
            app.Shutdown();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Toggle(MainWindow window, bool enabled)
    {
        Field<MenuItem>(window, "SyncFilesMenuItem").IsChecked = enabled;
        Call(window, "SyncFiles_Click", window, new RoutedEventArgs());
        Field<DispatcherTimer>(window, "_fileSyncTimer").Stop();
    }
    private static void Poll(MainWindow window)
    {
        WaitFor((Task)Call(window, "PollFileChangesAsync")!);
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void WaitFor(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("File sync verification timed out");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
