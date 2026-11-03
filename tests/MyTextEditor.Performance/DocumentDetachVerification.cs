using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

internal static class DocumentDetachVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Isolate(MainWindow window)
    {
        Set(window, "_settingsReady", false);
        ((DispatcherTimer)typeof(MainWindow).GetField("_settingsSaveTimer", Private)!.GetValue(window)!).Stop();
        Call(window, "StopFileSync");
    }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var source = new MainWindow { ShowInTaskbar = false, Topmost = true, WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Width = 1040, Height = 680 };
        Isolate(source);
        MainWindow? target = null;
        try
        {
            source.Show(); source.Activate(); Pump();
            var document = source.Documents[0];
            var editor = document.Editor;
            editor.LoadUtf8(Encoding.UTF8.GetBytes("original"));
            editor.ReplaceAll("미저장 문서 😀");
            editor.SetWordWrap(true);
            editor.SelectUtf16Range(0, 3);
            document.ChangeEncoding(DocumentEncodingKind.Utf8Bom);
            var selection = editor.SelectionRange;
            var position = source.PointToScreen(new Point(source.ActualWidth + 40, 100));
            Set(source, "_dragDocument", document);
            Set(source, "_tabDetachPosition", position);
            Call(source, "FinishDocumentTabDrag", false);
            Check(source.Documents.Contains(document) && app.Windows.OfType<MainWindow>().Count() == 1, "Cancelled drag detached a document");
            var grid = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("DocumentPanesGrid", Private)!.GetValue(source)!;
            var drop = grid.PointFromScreen(position);
            typeof(DocumentPanesVerification).GetMethod("DragTab", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { source, document, drop });
            target = app.Windows.OfType<MainWindow>().SingleOrDefault(window => window != source);
            Check(target is not null, $"Detach failed: window={source.Left},{source.Top},{source.ActualWidth},{source.ActualHeight}; drop={position}; state={typeof(MainWindow).GetField("StatusMessage", Private)!.GetValue(source)}");
            Isolate(target!); Pump();
            Check(source.Documents.Count == 0 && target!.Documents.Single() == document, "Document was copied or lost");
            Check(ReferenceEquals(editor, document.Editor) && document.Text == "미저장 문서 😀", "Native editor/text not preserved");
            Check(document.EncodingModified && document.HasByteOrderMark && editor.WordWrap, "Encoding or wrap lost");
            Check(editor.SelectionRange == selection, "Selection lost");
            editor.Undo(); Check(document.Text == "original", "Undo history lost"); editor.Redo();
            target!.Topmost = true;
            source.Topmost = false;
            target.Width = 1040;
            target.Height = 680;
            target.Left = 650;
            target.Top = 0;
            target.Activate(); Pump();
            var targetGrid = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("DocumentPanesGrid", Private)!.GetValue(target)!;
            var returnDrop = targetGrid.PointFromScreen(grid.PointToScreen(new Point(120, 100)));
            typeof(DocumentPanesVerification).GetMethod("DragTab", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { target, document, returnDrop });
            Check(source.Documents.Contains(document) && target.Documents.Count == 0 && app.Windows.OfType<MainWindow>().Count() == 2,
                "Physical return drag spawned a window or failed to merge");
            Check((bool)Call(source, "MoveDocumentToWindow", document, target, false, 0)!, "Return setup failed");
            Pump();
            for (var round = 0; round < 50; round++)
            {
                var from = round % 2 == 0 ? target! : source;
                var to = round % 2 == 0 ? source : target!;
                source.Topmost = ReferenceEquals(source, from);
                target.Topmost = ReferenceEquals(target, from);
                var fromGrid = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("DocumentPanesGrid", Private)!.GetValue(from)!;
                var toGrid = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("DocumentPanesGrid", Private)!.GetValue(to)!;
                var visibleTarget = new Point(ReferenceEquals(to, source) ? 120 : toGrid.ActualWidth - 80, 100);
                var destination = fromGrid.PointFromScreen(toGrid.PointToScreen(visibleTarget));
                typeof(DocumentPanesVerification).GetMethod("DragTab", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { from, document, destination });
                Check(to.Documents.Contains(document) && app.Windows.OfType<MainWindow>().Count() == 2, "Physical round-trip transfer failed");
                Pump();
                var focused = System.Windows.Input.FocusManager.GetFocusedElement(from) as DependencyObject;
                Check(focused is null || Window.GetWindow(focused) != to, "Source logical focus still points into destination");
                var titlePoint = from.PointToScreen(new Point(200, -12));
                SetCursorPos((int)titlePoint.X, (int)titlePoint.Y);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30); Pump();
                Check(from.IsActive, $"Reactivating source bounced at round {round}");
                var activeFocus = System.Windows.Input.FocusManager.GetFocusedElement(from) as DependencyObject;
                Check(activeFocus is null || Window.GetWindow(activeFocus) == from,
                    $"Reactivating source retained focus in the other window at round {round}");
                to.Activate(); Pump();
                Check(to.Documents.Contains(document) && !from.Documents.Contains(document), "Transfer ownership lost");
                Check(ReferenceEquals(document.Editor, editor) && document.Text == "미저장 문서 😀", "Transfer recreated editor or changed text");
                editor.Undo(); Check(document.Text == "original", "Round-trip lost Undo"); editor.Redo();
            }
            Console.WriteLine("PASS 50 round-trip transfers, source reactivation, native editor identity and Undo/Redo");
            Call(target!, "ApplyTheme", "Dark");
            Check(!ReferenceEquals(source.Resources.MergedDictionaries[0], target!.Resources.MergedDictionaries[0]), "Windows share theme state");
            source.Close(); Pump();
            Check(!source.IsVisible, "Empty source failed normal close");
            Check(target.IsVisible && document.Text == "미저장 문서 😀", "Closing source invalidated detached document");
            editor.ReplaceAll("new window edit");
            Check(document.IsModified, "Detached editor events not routed");
            editor.Undo(); Check(document.Text == "미저장 문서 😀", "Editing detached window lost Undo");
            var answerDialog = typeof(DocumentDisplayVerification).GetMethod("WithDialogResponse", BindingFlags.Static | BindingFlags.NonPublic)!;
            answerDialog.Invoke(null, new object[] { target, 2, (Action)(() => { target.Close(); Pump(); }) });
            Check(target.IsVisible && document.Text == "미저장 문서 😀", "Cancel shutdown lost document");
            target.WindowState = WindowState.Minimized; Pump();
            target.WindowState = WindowState.Normal; Pump();
            Check(document.Editor == editor && editor.CanUndo, "Minimize/restore lost native editor");
            VerifyLargeTransfers(target, document);
            answerDialog.Invoke(null, new object[] { target, 7, (Action)(() => { target.Close(); Pump(); }) });
            Check(!target.IsVisible, "Discard shutdown failed");
            Console.WriteLine("PASS detach: same editor, unsaved text, BOM/encoding dirty, wrap, selection, Undo/Redo, independent theme and source close");
            return 0;
        }
        finally
        {
            foreach (var window in app.Windows.OfType<MainWindow>().ToArray())
            {
                Isolate(window);
                foreach (var document in window.Documents) document.Editor.ReleaseResources();
                Set(window, "_allowClose", true); window.Close();
            }
            app.Shutdown();
        }
    }
    private static void VerifyLargeTransfers(MainWindow source, DocumentViewModel document)
    {
        var target = new MainWindow { ShowInTaskbar = false, WindowState = WindowState.Normal };
        Isolate(target);
        target.Show(); Pump();
        var editor = document.Editor;
        editor.SetWordWrap(false);
        foreach (var size in new[] { 30_000_000, 300_000_000 })
        {
            var bytes = new byte[size];
            Array.Fill(bytes, (byte)'x');
            for (var offset = 99; offset < bytes.Length; offset += 100) bytes[offset] = (byte)'\n';
            editor.LoadUtf8(bytes);
            bytes = null!;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var handles = process.HandleCount;
            var memory = process.PrivateMemorySize64;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var round = 0; round < 10; round++)
            {
                var from = round % 2 == 0 ? source : target;
                var to = round % 2 == 0 ? target : source;
                Check((bool)Call(from, "MoveDocumentToWindow", document, to, false, 0)!, "Large transfer rejected");
                Pump();
                Check(editor.Utf8ByteLength == size && ReferenceEquals(editor, document.Editor), "Large document changed");
            }
            watch.Stop();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            Console.WriteLine($"transfer size={size / 1_000_000}MB count=10 elapsedMs={watch.Elapsed.TotalMilliseconds:F1} handleDelta={process.HandleCount - handles} privateMemoryDelta={process.PrivateMemorySize64 - memory}");
        }
        target.Close(); Pump();
        document.IsModified = true;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
