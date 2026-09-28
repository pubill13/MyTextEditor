using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;

internal static class TabVisibilityVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new[] { Path.Combine(directory, "첫번째.log"), Path.Combine(directory, "두번째.log") };
            foreach (var path in paths) File.WriteAllText(path, "AAA 한글\r\nBBB");
            window.Show(); Pump();
            Wait((Task)Call(window, "OpenFilesAsync", (object)paths)!);
            VerifyHeaders(window, 3);
            foreach (var document in window.Documents.ToArray()) Call(window, "RemoveDocument", document);
            Check(Field<Border>(window, "EmptyDocumentState").IsVisible, "Empty screen must appear after closing all documents");
            Wait((Task)Call(window, "OpenFilesAsync", (object)paths)!);
            VerifyHeaders(window, 2);
            foreach (var document in window.Documents.ToArray()) Call(window, "RemoveDocument", document);
            Call(window, "NewDocument", "", null);
            Wait(window.OpenStartupFilesAsync(paths));
            VerifyHeaders(window, 2);
            Console.WriteLine("PASS initial two-file open and empty-screen batch drop: headers visible, hit-testable and selectable");
            return 0;
        }
        finally
        {
            window.Hide();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close(); app.Shutdown(); Directory.Delete(directory, true);
        }
    }

    private static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted) { Check(watch.Elapsed.TotalSeconds < 20, "File open timed out"); Pump(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult(); Pump();
    }

    private static void VerifyHeaders(MainWindow window, int expectedCount)
    {
        Check(window.Documents.Count == expectedCount, "Expected all opened files");
        Check(Field<Border>(window, "EmptyDocumentState").Visibility == Visibility.Collapsed, "Empty-state overlay hides loaded tab headers");
        var tabs = Field<TabControl>(window, "DocumentTabs");
        window.UpdateLayout();
        foreach (var document in window.Documents)
        {
            var header = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(document);
            Check(header.IsVisible && header.ActualHeight > 0 && header.ActualWidth > 0, "Tab header has no visible bounds");
            Check(Descendants(header).OfType<TextBlock>().Any(text => text.Text == document.TabTitle), "Tab title binding is missing or stale");
            var location = header.TranslatePoint(new Point(8, header.ActualHeight / 2), window);
            var hit = window.InputHitTest(location) as DependencyObject;
            while (hit is not null && hit != header) hit = VisualTreeHelper.GetParent(hit);
            Check(hit == header, "Tab header is obstructed and cannot receive a click");
            header.IsSelected = true; Pump();
            Check(tabs.SelectedItem == document && document.Editor.IsVisible, "Tab selection did not show its editor");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
