using TabControl = System.Windows.Controls.TabControl;
using ListBox = System.Windows.Controls.ListBox;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;

internal static class ResultsCloseVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private static void CloseKey(bool all = false)
    {
        try
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            if (all) keybd_event(0x10, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30); Pump();
            keybd_event(0x57, 0, 0, UIntPtr.Zero); Thread.Sleep(30); Pump();
        }
        finally
        {
            keybd_event(0x57, 0, 2, UIntPtr.Zero);
            keybd_event(0x10, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(30); Pump();
        }
    }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow();
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop(); Call(window, "StopFileSync");
        try
        {
            window.Show(); window.Activate(); Pump();
            var document = window.Documents[0];
            for (var detached = 0; detached < 2; detached++)
            {
                var sessions = Enumerable.Range(0, 3).Select(i => new SearchResultSession { Title = $"검색 {i}", ToolTip = "test", ConditionSummary = "test" }).ToArray();
                foreach (var session in sessions) { session.Rows.Add(new SearchResultRow(1, "한글 😀")); window.SearchSessions.Add(session); }
                var tabs = Field<TabControl>(window, "SearchResultTabs");
                tabs.SelectedItem = sessions[1]; Call(window, "ShowSearchResults"); Pump();
                if (detached == 1) { Call(window, "DetachResults_Click", window, new RoutedEventArgs()); Field<Window>(window, "_resultsWindow").Activate(); Pump(); }
                Field<ListBox>(window, "_activeResultsList").Focus(); Pump();
                CloseKey();
                Check(window.SearchSessions.Count == 2 && ReferenceEquals(tabs.SelectedItem, sessions[2]), "Close must select right neighbor");
                Check(Field<FrameworkElement>(window, "ResultPanel").IsKeyboardFocusWithin, "Result focus lost after close");
                CloseKey();
                Check(window.SearchSessions.Count == 1 && ReferenceEquals(tabs.SelectedItem, sessions[0]), "Repeated close must select left neighbor");
                window.SearchSessions.Add(sessions[1]); window.SearchSessions.Add(sessions[2]);
                CloseKey(true);
                Check(window.SearchSessions.Count == 0 && window.Documents.Contains(document), "Result close affected source document");
                Pump(); Pump();
                Check(document.Editor.IsEditorFocused, "Final result close must return editor focus");
            }
            var empty = new SearchResultSession { Title = "빈 검색", ToolTip = "test", ConditionSummary = "test" };
            window.SearchSessions.Add(empty);
            var emptyTabs = Field<TabControl>(window, "SearchResultTabs");
            emptyTabs.SelectedItem = empty; Call(window, "ShowSearchResults"); Pump();
            ((TabItem)emptyTabs.ItemContainerGenerator.ContainerFromItem(empty)).Focus(); Pump();
            typeof(MainWindow).GetField("_pendingTransformedText", Private)!.SetValue(window, "preview");
            CloseKey();
            Check(window.SearchSessions.Count == 0 && Field<FrameworkElement>(window, "TransformPreviewView").Visibility == Visibility.Visible, "Last result close must preserve pending preview");
            Check(Field<FrameworkElement>(window, "PreviewGrid").IsKeyboardFocusWithin, "Preview focus was not retained");
            CloseKey(true);
            Check(window.Documents.Contains(document), "Ctrl+Shift+W in preview closed source document");
            Console.WriteLine("PASS docked/detached native Ctrl+W, repeated neighbor focus, Ctrl+Shift+W and source retention");
            return 0;
        }
        finally
        {
            window.Hide();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close(); app.Shutdown();
        }
    }
}
