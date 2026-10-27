using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Controls.MenuItem;

internal static class DocumentDisplayVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Document operation timed out");
        task.GetAwaiter().GetResult(); Pump();
    }

    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-document-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "encoding.txt");
        const string original = "첫 줄\r\n둘째 줄 😀";
        File.WriteAllText(path, original, new UTF8Encoding(false));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        try
        {
            window.Show(); Pump();
            Wait(window.OpenStartupFilesAsync([path]));
            var document = window.Documents.Single();
            document.ChangeEncoding(DocumentEncodingKind.Utf8Bom);
            Check(document.IsModified && !document.Editor.IsModified, "Encoding-only change must mark document dirty");
            document.ChangeEncoding(DocumentEncodingKind.Utf8);
            Check(!document.IsModified, "Returning to saved encoding must clear metadata dirty");
            document.Editor.ReplaceAll(original + " edited");
            document.ChangeEncoding(DocumentEncodingKind.Utf16Be);
            document.Editor.Undo(); Pump();
            Check(document.Text == original && document.IsModified && document.EncodingModified, "Undo must preserve encoding dirty");
            Console.WriteLine("document-display: encoding-only save");
            var save = (Task<bool>)Call(window, "SaveDocumentAsync", document, false)!;
            Wait(save);
            Check(save.Result && !document.IsModified && !document.EncodingModified, "Encoding-only save must clear dirty without a savepoint event");
            Check(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()), "Save must write selected UTF-16 BE BOM");
            document.ChangeEncoding(DocumentEncodingKind.Utf8);
            document.ChangeEncoding(DocumentEncodingKind.Utf16Be);
            Check(!document.IsModified, "Successful save must update encoding baseline");

            Console.WriteLine("document-display: reload and concurrent-edit protection");
            File.WriteAllText(path, "다시 읽은 파일", new UTF8Encoding(false));
            Wait((Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!);
            Check(document.Text == "다시 읽은 파일" && !document.IsModified && !document.Editor.CanUndo, "Explicit reload must replace content and reset Undo");
            File.WriteAllText(path, "읽는 중 외부 파일", new UTF8Encoding(false));
            var reload = (Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!;
            var conflictingSave = (Task<bool>)Call(window, "SaveDocumentAsync", document, false)!;
            Check(conflictingSave.IsCompletedSuccessfully && !conflictingSave.Result, "Save must not race an encoding reload");
            document.Editor.ReplaceAll("다시 읽는 중 사용자 편집");
            Wait(reload);
            Check(document.Text == "다시 읽는 중 사용자 편집" && document.IsModified, "Reload must preserve edits made during I/O");
            Console.WriteLine("document-display: reload dialog responses (Cancel / Discard / Save / failure)");
            WithDialogResponse(window, 2, () => Wait((Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!));
            Check(document.Text == "다시 읽는 중 사용자 편집" && document.IsModified, "Cancel reload must preserve unsaved edits");
            WithDialogResponse(window, 7, () => Wait((Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!));
            Check(document.Text == "읽는 중 외부 파일" && !document.IsModified, "Discard then reload must replace edits");
            document.Editor.ReplaceAll("다시 열기 전에 저장");
            WithDialogResponse(window, 6, () => Wait((Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!));
            Check(document.Text == "다시 열기 전에 저장" && File.ReadAllText(path) == document.Text && !document.IsModified, "Save then reload must save edits while holding the reload guard");
            File.WriteAllBytes(path, [0xff]);
            Wait((Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!);
            Check(document.Text == "다시 열기 전에 저장" && !document.IsModified, "Failed decoding must preserve loaded content and dirty state");
            Check(Field<System.Windows.Controls.TextBlock>(window, "StatusMessage").Text.Contains("다시 열지 못했습니다"),
                "Failed decoding must report its cause in the status bar");
            Check(!Field<HashSet<DocumentViewModel>>(window, "_encodingReloads").Contains(document), "Failed reload must release its guard");
            Console.WriteLine("document-display: split and close protection");
            VerifyPanes(window, document);
            File.WriteAllText(path, "닫힌 문서", new UTF8Encoding(false));
            var closingReload = (Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!;
            Call(window, "RemoveDocument", document);
            Wait(closingReload);
            Check(!window.Documents.Contains(document), "Completing reload must not resurrect a closed document");
            Console.WriteLine("PASS encoding dirty/revert, Undo, BOM save, reload success/failure/cancel/edit/close protection, split auto-collapse");
            return 0;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close(); app.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    private static void VerifyPanes(MainWindow window, DocumentViewModel first)
    {
        Call(window, "NewDocument", "", null); Pump();
        var second = window.Documents.Last();
        second.Editor.ReplaceAll("두 번째 문서");
        Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked = true;
        Call(window, "ToggleDocumentSplit_Click", window, new RoutedEventArgs()); Pump();
        Check(window.RightDocuments.Contains(second), "Split must put selected document in right pane");
        WithDialogResponse(window, 2, () => Wait((Task)Call(window, "CloseDocumentAsync", second)!));
        Check(window.RightDocuments.Contains(second) && Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked, "Cancelled close must preserve split");
        Call(window, "MoveToPane", first, true); Pump();
        Check(window.RightDocuments.Count == 0 && window.LeftDocuments.Count == 2 && !Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked, "Empty left pane must collapse and move right documents left");
        second.Editor.Undo(); Pump();
        Check(second.Text == "", "Automatic collapse must preserve Undo");
        Call(window, "SelectDocument", second);
        Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked = true;
        Call(window, "ToggleDocumentSplit_Click", window, new RoutedEventArgs()); Pump();
        Wait((Task)Call(window, "CloseDocumentAsync", second)!);
        Check(window.Documents.Count == 1 && window.LeftDocuments.Contains(first) && window.RightDocuments.Count == 0 && !Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked, "Closing last right document must collapse split");
    }

    private static void WithDialogResponse(MainWindow owner, int command, Action action)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        var threadId = GetWindowThreadProcessId(ownerHandle, out var processId);
        Check(processId == Environment.ProcessId, "Dialog automation must target this test process only");
        using var cancellation = new CancellationTokenSource();
        // Native MessageBox uses a modal Win32 loop; it need not service WPF DispatcherTimer.
        var response = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                var responded = false;
                EnumThreadWindows(threadId, (dialog, _) =>
                {
                    if (GetWindow(dialog, 4) != ownerHandle || !IsWindowVisible(dialog)) return true; // GW_OWNER
                    var className = new StringBuilder(32);
                    GetClassName(dialog, className, className.Capacity);
                    if (className.ToString() != "#32770") return true;
                    var button = GetDlgItem(dialog, command);
                    if (button == IntPtr.Zero || !IsWindowEnabled(button)) return true;
                    responded = PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
                    return !responded;
                }, IntPtr.Zero);
                if (responded) return true;
                Thread.Sleep(20);
            }
            return false;
        });
        try { action(); Check(response.GetAwaiter().GetResult(), $"Expected owned modal dialog was not shown (command {command})"); }
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
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
