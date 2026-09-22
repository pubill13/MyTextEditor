using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Controls.MenuItem;
using TabControl = System.Windows.Controls.TabControl;

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
