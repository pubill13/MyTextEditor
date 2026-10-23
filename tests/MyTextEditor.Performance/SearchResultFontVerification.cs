using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using ListBox = System.Windows.Controls.ListBox;
using TabControl = System.Windows.Controls.TabControl;
using FontFamily = System.Windows.Media.FontFamily;

internal static class SearchResultFontVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow();
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        try
        {
            window.Show(); Pump();
            var source = new SearchResultSource { DisplayName = "한글 파일.log" };
            for (var detached = 0; detached < 2; detached++)
            {
                var session = new SearchResultSession { Title = "글꼴", ToolTip = "test", ConditionSummary = "test" };
                for (var i = 0; i < 10000; i++) session.Rows.Add(new SearchResultRow(int.MaxValue - i, "한글 😀 본문", i % 2 == 0) { Source = source });
                window.SearchSessions.Add(session);
                Field<TabControl>(window, "SearchResultTabs").SelectedItem = session;
                Call(window, "ShowSearchResults"); Pump();
                if (detached == 1) { Call(window, "DetachResults_Click", window, new RoutedEventArgs()); Pump(); }
                foreach (var size in new[] { 6d, 15d, 72d })
                {
                    app.Resources["EditorFontFamily"] = new FontFamily("Malgun Gothic");
                    app.Resources["EditorFontSize"] = size;
                    Pump();
                    var list = Field<ListBox>(window, "_activeResultsList");
                    list.UpdateLayout(); Pump();
                    var rows = Descendants<ListBoxItem>(list).ToArray();
                    Check(rows.Length > 0 && rows.Length < 1000, "Result virtualization lost");
                    var blocks = Descendants<TextBlock>(rows[0]).ToArray();
                    foreach (var text in blocks.Where(b => b.Text != "●" && b.Text != "↳"))
                        Check(text.FontFamily.Source == "Malgun Gothic" && Math.Abs(text.FontSize - size * 96d / 72d) < 0.01, "Result font not updated");
                    var number = blocks.Single(b => b.Name == "ResultLineNumber");
                    Check(number.ActualWidth + 0.1 >= number.DesiredSize.Width, "Line number clipped");
                    Check(rows[0].ActualHeight >= number.DesiredSize.Height, "Result row height clipped");
                    list.SelectedItems.Add(session.Rows[0]); list.SelectedItems.Add(session.Rows[1]);
                    Check(list.SelectedItems.Count == 2, "Multiple selection changed");
                    list.SelectedItems.Clear();
                }
            }
            Console.WriteLine("PASS result font updates: docked/detached, 6/15/72, context, line numbers, virtualization, selection");
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
