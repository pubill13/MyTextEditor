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
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
            WithDialogResponse(window, 1, () => Wait((Task)Call(window, "ReloadWithEncodingAsync", document, DocumentEncodingKind.Utf8)!));
            Check(document.Text == "다시 열기 전에 저장" && !document.IsModified, "Failed decoding must preserve loaded content and dirty state");
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
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var responded = false;
        timer.Tick += (_, _) =>
        {
            var dialog = FindWindow(null, "인코딩으로 다시 열기");
            if (dialog == IntPtr.Zero) dialog = GetLastActivePopup(ownerHandle);
            if (dialog == ownerHandle || dialog == IntPtr.Zero) return;
            responded = PostMessage(dialog, 0x0111, (IntPtr)command, IntPtr.Zero);
            timer.Stop();
        };
        timer.Start();
        try { action(); Check(responded, "Expected modal dialog was not shown"); }
        finally { timer.Stop(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
