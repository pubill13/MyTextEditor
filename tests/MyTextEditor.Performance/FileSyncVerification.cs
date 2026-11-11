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
        var confirmation = typeof(MainWindow).Assembly.GetType("MyTextEditor.Services.ExternalChangeConfirmation")!
            .GetProperty("ConfirmOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousConfirmation = confirmation.GetValue(null);
        var approve = true;
        var prompts = 0;
        var lastWarning = false;
        Action? duringConfirmation = null;
        confirmation.SetValue(null, new Func<Window, string, bool, bool>((owner, _, dirty) =>
        {
            Require(ReferenceEquals(owner, window), "Reload confirmation must belong to the document window");
            prompts++;
            lastWarning = dirty;
            var action = duringConfirmation;
            duringConfirmation = null;
            action?.Invoke();
            return approve;
        }));
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
            Require(prompts == 1 && !lastWarning, "Clean external changes must ask before reload without a discard warning");

            approve = false;
            File.WriteAllText(file, "거절한 외부 변경 AAA", new UTF8Encoding(false));
            Poll(window);
            var declinedPrompts = prompts;
            Require(document.ContentRevision == firstRevision && declinedPrompts == 2 && !lastWarning,
                "Declining a clean reload must preserve the buffer and revision");
            Poll(window);
            Require(prompts == declinedPrompts, "The same declined file version must not prompt again");
            approve = true;
            File.WriteAllText(file, "다음 외부 변경 AAA", new UTF8Encoding(false));
            Poll(window);
            Require(prompts == declinedPrompts + 1 && document.Text == "다음 외부 변경 AAA",
                "A new file version must ask again after a declined version");

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
            approve = false;
            Poll(window);
            Require(document.Text == "저장하지 않은 내 수정" && document.IsModified,
                "Declining a dirty reload must preserve local content and Undo");
            Require(lastWarning, "Reloading a dirty document must warn about discarding local edits");
            Require(Field<System.Windows.Controls.TextBlock>(window, "StatusMessage").Text.Contains("수정 내용", StringComparison.Ordinal),
                "Dirty conflict must be reported");
            document.Editor.Undo();
            Require(!document.IsModified, "Undo to save point must be clean");
            declinedPrompts = prompts;
            approve = true;
            Poll(window);
            Require(prompts == declinedPrompts && document.Text == "세 번째 상태\nUTF-16 한글 😀",
                "Undo must not silently approve a previously declined file version");
            WaitFor((Task)Call(window, "ReloadExternalFileAsync")!);
            Require(prompts == declinedPrompts + 1 && document.Text == "외부 충돌 내용",
                "Manual reload must allow retrying a declined file version");

            document.Editor.ReplaceAll("승인 전에 남아 있는 내 수정");
            File.WriteAllText(file, "승인한 충돌 내용", new UTF8Encoding(false));
            Poll(window);
            Require(lastWarning && document.Text == "승인한 충돌 내용" && !document.IsModified && !document.Editor.CanUndo,
                "Approved dirty reload must replace the buffer cleanly and discard the old Undo history");

            File.Delete(file);
            Poll(window);
            Require(document.Text == "승인한 충돌 내용", "Deletion must preserve buffer");
            File.WriteAllText(file, "재생성됨", new UTF8Encoding(false));
            Poll(window);
            Require(document.Text == "재생성됨", "Recreated file must reload");

            File.WriteAllText(file, "잠긴 외부 변경 내용", new UTF8Encoding(false));
            var beforeLockedPrompts = prompts;
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Poll(window);
                Require(document.Text == "재생성됨", "Locked file must preserve buffer");
            }
            Require(prompts == beforeLockedPrompts + 1, "A stable locked file must ask once before the read failure");
            Poll(window);
            Require(document.Text == "재생성됨" && prompts == beforeLockedPrompts + 1,
                "A failed approved read must retain the buffer without repeatedly prompting");
            WaitFor((Task)Call(window, "ReloadExternalFileAsync")!);
            Require(document.Text == "잠긴 외부 변경 내용" && prompts == beforeLockedPrompts + 2,
                "Manual reload must retry a failed file version successfully");

            Toggle(window, false);
            File.WriteAllText(file, "자동 감시 OFF 중 수동 변경", new UTF8Encoding(false));
            approve = false;
            WaitFor((Task)Call(window, "ReloadExternalFileAsync")!);
            Require(document.Text == "잠긴 외부 변경 내용", "Declining manual reload must preserve the buffer while monitoring is OFF");
            approve = true;
            WaitFor((Task)Call(window, "ReloadExternalFileAsync")!);
            Require(document.Text == "자동 감시 OFF 중 수동 변경", "Manual reload must work while monitoring is OFF");
            Toggle(window, true);

            document.Editor.ReplaceAll("직접 저장한 내용");
            WaitFor((Task)Call(window, "SaveDocumentAsync", document, false)!);
            var savedRevision = document.ContentRevision;
            Poll(window);
            Require(document.ContentRevision == savedRevision && document.Editor.CanUndo,
                "Own save must not cause reload and discard Undo");

            File.WriteAllText(file, "비동기 진행 중 OFF", new UTF8Encoding(false));
            WaitFor((Task)Call(window, "PollFileChangesAsync")!);
            WaitFor(Task.Delay(550));
            var inFlight = (Task)Call(window, "PollFileChangesAsync")!;
            Toggle(window, false);
            WaitFor(inFlight);
            Require(document.Text == "직접 저장한 내용", "Turning OFF must cancel pending reload application");
            Toggle(window, true);
            Poll(window);
            Require(document.Text == "비동기 진행 중 OFF", "Turning ON again must resume after a cancelled poll");
            var otherFile = Path.Combine(directory, "inactive.log");
            File.WriteAllText(otherFile, "다른 문서", new UTF8Encoding(false));
            WaitFor(window.OpenStartupFilesAsync([otherFile]));
            var other = window.Documents.Single(item => item.FilePath == otherFile);
            File.WriteAllText(file, "비활성 문서 최신 내용", new UTF8Encoding(false));
            Poll(window);
            Require(document.Text == "비동기 진행 중 OFF", "Inactive documents must not reload their full content");
            Call(window, "SelectDocument", document);
            Poll(window);
            Require(document.Text == "비활성 문서 최신 내용", "Selecting pending document must reload it");
            File.WriteAllText(file, "기록 중 첫 변경", new UTF8Encoding(false));
            WaitFor((Task)Call(window, "PollFileChangesAsync")!);
            Require(document.Text == "비활성 문서 최신 내용", "First observation must defer until stable");
            File.WriteAllText(file, "기록 중 두 번째 변경", new UTF8Encoding(false));
            WaitFor((Task)Call(window, "PollFileChangesAsync")!);
            Require(document.Text == "비활성 문서 최신 내용", "Changing stamp must restart stable wait");
            Poll(window);
            Require(document.Text == "기록 중 두 번째 변경", "Stable latest content must apply");

            foreach (var guardName in new[] { "_savingDocuments", "_closingDocuments", "_encodingReloads" })
            {
                var guard = Field<HashSet<DocumentViewModel>>(window, guardName);
                var before = document.ContentRevision;
                File.WriteAllText(file, guardName, new UTF8Encoding(false));
                guard.Add(document);
                try { Poll(window); }
                finally { guard.Remove(document); }
                Require(document.ContentRevision == before, guardName + " must prevent external reload");
                Poll(window);
                Require(document.Text == guardName, "Reload must resume after " + guardName);
            }

            File.WriteAllText(file, "동시 편집 중 외부 변경", new UTF8Encoding(false));
            duringConfirmation = () => document.Editor.ReplaceAll("승인 중 추가한 편집");
            Poll(window);
            Require(document.Text == "승인 중 추가한 편집", "Editing during approval must invalidate that approval and preserve local content");
            var invalidatedPrompts = prompts;
            Poll(window);
            Require(document.Text == "승인 중 추가한 편집" && prompts == invalidatedPrompts,
                "An invalidated approval must not overwrite later edits on the next poll");
            document.Editor.Undo();
            WaitFor((Task)Call(window, "ReloadExternalFileAsync")!);
            Require(document.Text == "동시 편집 중 외부 변경", "Manual retry after an invalidated approval must reload");

            File.WriteAllText(file, "승인 후 읽는 중 외부 변경", new UTF8Encoding(false));
            duringConfirmation = () => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => document.Editor.ReplaceAll("승인 후 읽는 중 추가 편집")));
            Poll(window);
            Require(document.Text == "승인 후 읽는 중 추가 편집" && document.IsModified,
                "Editing after approval while asynchronous I/O is pending must preserve the new local edit");
            invalidatedPrompts = prompts;
            Poll(window);
            Require(document.Text == "승인 후 읽는 중 추가 편집" && prompts == invalidatedPrompts,
                "A read invalidated by later edits must not reuse its approval");
            document.Editor.Undo();
            WaitFor((Task)Call(window, "ReloadExternalFileAsync")!);

            File.WriteAllText(file, "확인 중 디스크 변경 전", new UTF8Encoding(false));
            duringConfirmation = () => File.WriteAllText(file, "확인 중 디스크 변경 후 새 버전", new UTF8Encoding(false));
            var beforeDiskRace = document.Text;
            Poll(window);
            Require(document.Text == beforeDiskRace, "A disk version changed during approval must not be applied under the old approval");
            var beforeNewVersionPrompt = prompts;
            Poll(window);
            Require(document.Text == "확인 중 디스크 변경 후 새 버전" && prompts == beforeNewVersionPrompt + 1,
                "A newer disk version must receive a fresh approval before being applied");

            VerifyLargeReloads(window, document, file);

            File.WriteAllText(file, "종료 중 외부 변경", new UTF8Encoding(false));
            var closingPoll = (Task)Call(window, "PollFileChangesAsync")!;
            var beforeStop = document.ContentRevision;
            Call(window, "StopFileSync");
            WaitFor(closingPoll);
            Require(document.ContentRevision == beforeStop, "Stopping file sync must cancel pending application");
            Console.WriteLine("PASS file sync confirmation/decline/new version, dirty warning/approved discard, manual OFF/decline/failure retry, approval edit/disk races, revision/encoding, guards, deletion/recreation, own-save Undo, inactive/stability, pending OFF/stop, 30/100/300MB");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            throw;
        }
        finally
        {
            confirmation.SetValue(null, previousConfirmation);
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

    private static void VerifyLargeReloads(MainWindow window, DocumentViewModel document, string file)
    {
        var chunk = Encoding.UTF8.GetBytes(new string('x', 999) + "\n");
        foreach (var megabytes in new[] { 30, 100, 300 })
        {
            var bytes = megabytes * 1_000_000;
            using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
                for (var remaining = bytes; remaining > 0; remaining -= chunk.Length) stream.Write(chunk);
            var before = document.ContentRevision;
            Poll(window);
            var measurement = typeof(MainWindow).GetProperty("LastFileSyncMeasurement", Private)!.GetValue(window)!;
            var actualBytes = (long)measurement.GetType().GetProperty("Utf8Bytes")!.GetValue(measurement)!;
            Require(document.ContentRevision > before && !document.IsModified && actualBytes == bytes &&
                document.Editor.LineCount == bytes / chunk.Length + 1,
                $"{megabytes}MB external reload must apply the complete buffer cleanly");
            Console.WriteLine($"file-sync {megabytes}MB: {measurement}");
        }
    }

    private static void Toggle(MainWindow window, bool enabled)
    {
        Field<System.Windows.Controls.Primitives.ToggleButton>(window, "SyncFilesMenuItem").IsChecked = enabled;
        Call(window, "SyncFiles_Click", window, new RoutedEventArgs());
        Field<DispatcherTimer>(window, "_fileSyncTimer").Stop();
    }
    private static void Poll(MainWindow window)
    {
        WaitFor((Task)Call(window, "PollFileChangesAsync")!);
        WaitFor(Task.Delay(550));
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
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("File sync verification timed out");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}

