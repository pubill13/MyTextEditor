using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;

internal static class ToolsWindowVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
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
        try
        {
            window.Show(); window.Activate(); Pump();
            var panel = Field<Border>(window, "ToolPanel");
            var literal = Field<TextBox>(window, "LiteralFindBox");
            literal.Text = "한글 검색";
            var replace = Field<TextBox>(window, "ReplaceToBox");
            replace.Text = "보존";
            Call(window, "SetToolsVisible", true, false); Pump();
            var width = Field<ColumnDefinition>(window, "ToolColumn").ActualWidth;
            Call(window, "DetachTools_Click", window, new RoutedEventArgs()); Pump();
            var detached = Field<Window>(window, "_toolsWindow");
            Check(ReferenceEquals(detached.Content, panel), "Panel instance was not moved");
            Check(Field<ColumnDefinition>(window, "ToolColumn").ActualWidth == 0, "Dock column remains open");
            Check(literal.Text == "한글 검색" && replace.Text == "보존", "Inputs changed on detach");
            Check(Field<TextBlock>(window, "ToolsTargetText").Text.Contains(window.Documents[0].DisplayName), "Target title missing");
            Call(window, "SetToolsVisible", false, true); Pump();
            Check(!detached.IsVisible && panel.Visibility == Visibility.Collapsed, "Detached hide failed");
            Call(window, "ShowReplaceInput"); Pump();
            Check(detached.IsVisible && replace.Text == "보존", "Ctrl+H route lost input or did not restore window");
            var from = Field<TextBox>(window, "ReplaceFromBox");
            from.Text = "전체 선택";
            Call(window, "ShowReplaceInput"); Pump();
            Check(from.SelectionLength == from.Text.Length, "Detached replacement input not selected");
            detached.Close(); Pump();
            Check(!detached.IsVisible && ReferenceEquals(detached.Content, panel), "X should hide the same detached window");
            Check(Field<ColumnDefinition>(window, "ToolColumn").ActualWidth == 0, "X reopened dock column");
            Call(window, "ShowReplaceInput"); Pump();
            Check(ReferenceEquals(Field<Window>(window, "_toolsWindow"), detached) && detached.IsVisible, "Reopen did not reuse detached window");
            Call(window, "DetachTools_Click", window, new RoutedEventArgs()); Pump();
            Check(ReferenceEquals(panel.Parent, Field<Grid>(window, "MainContentGrid")), "Dock icon did not dock panel");
            Check(Math.Abs(Field<ColumnDefinition>(window, "ToolColumn").ActualWidth - width) < 2, "Panel width not restored");
            Check(literal.Text == "한글 검색" && replace.Text == "보존", "Inputs changed on dock");
            Call(window, "DetachTools_Click", window, new RoutedEventArgs()); Pump();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            Call(window, "CloseToolsWindowForShutdown"); Pump();
            Check(Field<Window?>(window, "_toolsWindow") is null, "Shutdown left tools window alive");
            Console.WriteLine("PASS tools panel same-instance detach, target, hide/restore, input selection, docking width and shutdown (programmatic GUI)");
            return 0;
        }
        finally
        {
            window.Hide();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            Call(window, "CloseToolsWindowForShutdown");
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            window.Close(); app.Shutdown();
        }
    }
}
