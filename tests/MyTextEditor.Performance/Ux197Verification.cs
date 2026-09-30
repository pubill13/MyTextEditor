using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

/// <summary>Small, deterministic checks for the v1.9.7 presentation and input contracts.
/// The runner can call Run() from a STA process; it intentionally does not touch user settings.</summary>
internal static class Ux197Verification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static int Run()
    {
        var oneDark = ThemePalette.Get("OneDark");
        var solarized = ThemePalette.Get("SolarizedLight");
        Check(oneDark.EditorForeground.R == 0xB0 && oneDark.EditorForeground.G == 0xB7 && oneDark.EditorForeground.B == 0xC4,
            "One Dark foreground palette drifted");
        Check(solarized.EditorForeground.R == 0x53 && solarized.EditorForeground.G == 0x69 && solarized.EditorForeground.B == 0x70,
            "Solarized Light foreground palette drifted");
        Check(FolderSearchOptions.NormalizeEmptyPatterns(" ; ") == FolderSearchOptions.DefaultFilePatterns,
            "Empty file pattern did not restore the default list");
        Check(FolderSearchOptions.NormalizeEmptyPatterns("*.trace;*.log") == "*.trace;*.log",
            "A valid custom file pattern was overwritten");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(oneDark.CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false, Width = 1200, Height = 760 };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<System.Windows.Threading.DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        typeof(MainWindow).GetMethod("StopFileSync", Private)!.Invoke(window, null);
        try
        {
            window.Show();
            var tools = Field<FrameworkElement>(window, "ToolPanel");
            var literal = Field<TextBox>(window, "LiteralFindBox");
            var mode = Field<CheckBox>(window, "UseConditionSearchCheck");
            mode.IsChecked = true;
            Check(!literal.IsEnabled, "Literal search remained enabled in condition mode");
            mode.IsChecked = false;
            Check(literal.IsEnabled, "Literal search did not reactivate after condition mode");
            var setVisible = window.GetType().GetMethod("SetToolsVisible", Private)!;
            setVisible.Invoke(window, [false, false]);
            Check(tools.Visibility == Visibility.Collapsed, "Tool panel did not hide");
            setVisible.Invoke(window, [true, false]);
            Check(tools.Visibility == Visibility.Visible, "Tool panel did not restore");
            void Invoke(string method) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, [window, new RoutedEventArgs()]);
            var diffToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "DiffToggleButton");
            diffToggle.IsChecked = true; Invoke("ToggleDiff_Click");
            var diff = Field<MyTextEditor.Diff.DiffWorkspaceWindow>(window, "_diffWorkspaceWindow");
            var tabs = diff.ComparisonCount;
            diffToggle.IsChecked = false; Invoke("ToggleDiff_Click");
            Check(!diff.IsVisible, "Diff OFF must hide");
            diffToggle.IsChecked = true; Invoke("ToggleDiff_Click");
            Check(diff.IsVisible && diff.ComparisonCount == tabs, "Diff restore must preserve tabs");
            diff.Close();
            Check(diffToggle.IsChecked == false, "Diff close must reset toggle");
            var macroToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "MacroToggleButton");
            macroToggle.IsChecked = true; Invoke("ToggleMacro_Click");
            var macro = Field<Window>(window, "_macroWindow");
            macroToggle.IsChecked = false; Invoke("ToggleMacro_Click");
            Check(!macro.IsVisible, "Macro OFF must hide");
            macroToggle.IsChecked = true; Invoke("ToggleMacro_Click");
            Check(ReferenceEquals(macro, Field<Window>(window, "_macroWindow")) && macro.IsVisible, "Macro restore must reuse window");
            macro.Close();
            Console.WriteLine("PASS v1.9.7 palette, pattern defaults, condition input state, panel hide/restore");
            return 0;
        }
        finally
        {
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close();
            app.Shutdown();
        }
    }
}
