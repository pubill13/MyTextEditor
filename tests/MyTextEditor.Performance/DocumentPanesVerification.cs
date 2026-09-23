using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Controls.MenuItem;
using TabControl = System.Windows.Controls.TabControl;
using Point = System.Windows.Point;

internal static class DocumentPanesVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        Set(window, "_settingsReady", false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        try
        {
            window.Show(); window.Activate(); Pump();
            var first = window.Documents[0];
            first.Editor.LoadUtf8(Encoding.UTF8.GetBytes("왼쪽 문서"));
            Call(window, "NewDocument", "", null);
            var second = window.Documents[1];
            second.Editor.LoadUtf8(Encoding.UTF8.GetBytes("오른쪽 문서"));
            second.Editor.ReplaceAll("오른쪽 수정");
            Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked = true;
            Call(window, "ToggleDocumentSplit_Click", window, new RoutedEventArgs()); Pump();
            Check(window.LeftDocuments.SequenceEqual(new[] { first }) && window.RightDocuments.SequenceEqual(new[] { second }), "Split did not move current document right");
            Check(first.Editor.IsVisible && second.Editor.IsVisible, "Both native editors must be visible");
            first.Editor.FocusEditor(); Pump();
            Call(window, "Undo_Click", window, new RoutedEventArgs());
            Check(second.Text == "오른쪽 수정", "Left Undo affected right document");
            second.Editor.FocusEditor(); Pump();
            Call(window, "Undo_Click", window, new RoutedEventArgs());
            Check(second.Text == "오른쪽 문서", "Moved document lost Undo or active pane routing");
            second.Editor.Redo();
            Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked = false;
            Call(window, "ToggleDocumentSplit_Click", window, new RoutedEventArgs()); Pump();
            Check(window.LeftDocuments.Count == 2 && window.RightDocuments.Count == 0 && second.Text == "오른쪽 수정", "Unsplit lost document content");
            VerifyDragTargets(window, second);
            second.Editor.MarkSaved(); second.IsModified = false;
            Call(window, "NewDocument", "", null); Pump();
            window.Documents.Last().Editor.FocusEditor(); Pump();
            for (var remaining = 2; remaining >= 0; remaining--)
            {
                SendCloseKeys();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (window.Documents.Count > remaining && watch.ElapsedMilliseconds < 2500) { Thread.Sleep(10); Pump(); }
                Check(window.Documents.Count == remaining, $"Repeated native Ctrl+W: expected {remaining}, actual {window.Documents.Count}, selected={Field<TabControl>(window, "DocumentTabs").SelectedIndex}, focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
            }
            Console.WriteLine("PASS split/unsplit native editors, active-pane Undo, document preservation and three consecutive Ctrl+W closes");
            return 0;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            Set(window, "_allowClose", true); window.Close(); app.Shutdown();
        }
    }

    private static void VerifyDragTargets(MainWindow window, DocumentViewModel document)
    {
        var grid = Field<System.Windows.Controls.Grid>(window, "DocumentPanesGrid");
        Check(Call(window, "ResolveDocumentTabDrop", new Point(grid.ActualWidth / 2, 80)) is null, "Editor center must not split accidentally");
        Check((bool?)Call(window, "ResolveDocumentTabDrop", new Point(grid.ActualWidth - 5, 80)) == true, "Right edge must offer split");
        Check(Call(window, "ResolveDocumentTabDrop", new Point(-10, 80)) is null, "Outside editor must cancel drop");
        Set(window, "_dragDocument", document); Set(window, "_tabDropRight", true);
        Call(window, "ShowDocumentTabDropPreview");
        Check(Field<System.Windows.Controls.Primitives.Popup>(window, "_tabDropPreview").IsOpen, "Native-airspace split preview must open");
        Call(window, "FinishDocumentTabDrag", true); Pump();
        Check(window.RightDocuments.Contains(document) && document.Text == "오른쪽 수정", "Drag split lost document");
        Check((bool?)Call(window, "ResolveDocumentTabDrop", new Point(5, 80)) == false, "Left pane must accept return drop");
        Set(window, "_dragDocument", document); Set(window, "_tabDropRight", false);
        Call(window, "FinishDocumentTabDrag", false);
        Check(window.RightDocuments.Contains(document), "Cancelled drop moved document");
        Set(window, "_dragDocument", document); Set(window, "_tabDropRight", false);
        Call(window, "FinishDocumentTabDrag", true); Pump();
        Check(window.LeftDocuments.Contains(document) && window.RightDocuments.Count == 0, "Return drag did not move left");
        document.Editor.Undo(); Check(document.Text == "오른쪽 문서", "Drag lost Undo history"); document.Editor.Redo();
        Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked = false;
        Call(window, "ToggleDocumentSplit_Click", window, new RoutedEventArgs()); Pump();
        DragTab(window, document, new Point(grid.ActualWidth - 10, grid.ActualHeight / 2));
        Check(window.RightDocuments.Contains(document), "Physical mouse drag across native editor did not split");
        DragTab(window, document, new Point(20, grid.ActualHeight / 2));
        Check(window.LeftDocuments.Contains(document), "Physical mouse drag back across native editor failed");
        Field<MenuItem>(window, "SplitDocumentsMenuItem").IsChecked = false;
        Call(window, "ToggleDocumentSplit_Click", window, new RoutedEventArgs()); Pump();
        Console.WriteLine("PASS tab drag edge targeting, native preview, cancellation, bidirectional move and Undo preservation");
    }

    private static void DragTab(MainWindow window, DocumentViewModel document, Point destination)
    {
        var grid = Field<System.Windows.Controls.Grid>(window, "DocumentPanesGrid");
        var tabs = Field<TabControl>(window, window.RightDocuments.Contains(document) ? "RightDocumentTabs" : "DocumentTabs");
        var header = (System.Windows.Controls.TabItem)tabs.ItemContainerGenerator.ContainerFromItem(document);
        var start = header.PointToScreen(new Point(18, header.ActualHeight / 2));
        var end = grid.PointToScreen(destination);
        SetCursorPos((int)start.X, (int)start.Y); Pump();
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30); Pump();
        try
        {
            for (var step = 1; step <= 12; step++)
            {
                SetCursorPos((int)(start.X + (end.X - start.X) * step / 12), (int)(start.Y + (end.Y - start.Y) * step / 12));
                Thread.Sleep(20); Pump();
            }
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); Pump(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private static void SendCloseKeys()
    {
        try
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero); Thread.Sleep(30); Pump();
            keybd_event(0x57, 0, 0, UIntPtr.Zero); Thread.Sleep(30); Pump();
        }
        finally { keybd_event(0x57, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(30); Pump(); }
    }
}
