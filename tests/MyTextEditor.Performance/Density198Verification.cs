using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;

internal static class Density198Verification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Bounds(FrameworkElement element, FrameworkElement root, string label)
    {
        if (!element.IsVisible) return;
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
            $"{label} outside content bounds: {bounds}, available {root.ActualWidth} x {root.ActualHeight}");
    }
    private static void Capture(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path); encoder.Save(stream);
    }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var main = new MainWindow { WindowState = WindowState.Normal };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(main, false);
        Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop(); Call(main, "StopFileSync");
        Window? diff = null, macro = null;
        var output = System.IO.Path.GetFullPath("artifacts/v1.9.8/verify"); System.IO.Directory.CreateDirectory(output);
        try
        {
            main.Show(); Pump();
            Field<ToggleButton>(main, "DiffToggleButton").IsChecked = true; Call(main, "ToggleDiff_Click", main, new RoutedEventArgs());
            diff = Field<Window>(main, "_diffWorkspaceWindow");
            Field<ToggleButton>(main, "MacroToggleButton").IsChecked = true; Call(main, "ToggleMacro_Click", main, new RoutedEventArgs());
            macro = Field<Window>(main, "_macroWindow");
            foreach (var theme in new[] { "Light", "Dark", "DarkPlus", "OneDark", "SolarizedLight" })
            {
                app.Resources.MergedDictionaries[0] = ThemePalette.Get(theme).CreateResources();
                foreach (var (name, window) in new[] { ("Main", (Window)main), ("Merge", diff), ("Macro", macro) })
                {
                    foreach (var minimum in new[] { true, false })
                    {
                        window.WindowState = WindowState.Normal; window.Width = minimum ? window.MinWidth : 1600;
                        window.Height = minimum ? window.MinHeight : 900; window.UpdateLayout(); Pump();
                        var root = (FrameworkElement)window.Content;
                        var toolbar = name == "Main" ? Field<Grid>(main, "MainToolbarGrid") : root;
                        var buttons = Descendants<Button>(toolbar).Where(b => b.IsVisible && b.Content is string).ToArray();
                        foreach (var button in buttons)
                        {
                            Bounds(button, root, $"{theme}/{name}/{button.Content}");
                            Check(button.ActualHeight >= 24 && button.ActualHeight <= 28, $"{name}/{button.Content} height {button.ActualHeight} is not compact");
                        }
                        foreach (var tabs in Descendants<TabControl>(root))
                            foreach (var item in tabs.Items)
                                if (tabs.ItemContainerGenerator.ContainerFromItem(item) is TabItem tab && tab.IsVisible)
                                    Bounds(tab, root, $"{theme}/{name} tab");
                        if (name == "Main")
                            Console.WriteLine($"{theme} Main {window.Width}x{window.Height}: toolbar={toolbar.ActualHeight:0.0}, document body={main.Documents[0].Editor.ActualHeight:0.0}");
                        else if (name == "Merge")
                        {
                            var editors = Descendants<MyTextEditor.Controls.ScintillaEditorHost>(root).ToArray();
                            Console.WriteLine($"{theme} Merge {window.Width}x{window.Height}: editor heights={string.Join(",", editors.Select(e => e.ActualHeight.ToString("0.0")))}");
                        }
                        else
                            Console.WriteLine($"{theme} Macro {window.Width}x{window.Height}: steps={Field<System.Windows.Controls.ListBox>(macro, "_steps").ActualHeight:0.0}, preview text={Field<System.Windows.Controls.TextBox>(macro, "_before").ActualHeight:0.0}");
                        if (minimum && theme is "Light" or "Dark") Capture(window, System.IO.Path.Combine(output, $"density198-{theme}-{name}.png"));
                    }
                }
            }
            Console.WriteLine($"PASS density198: five palettes, minimum/wide main/Merge/Macro command bounds. Actual DPI {VisualTreeHelper.GetDpi(main).PixelsPerInchX:0}. PNGs render WPF only; native Scintilla bodies and physical IME are not captured.");
            return 0;
        }
        finally
        {
            macro?.Close(); diff?.Close();
            Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
            foreach (var document in main.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(main, true); main.Close(); app.Shutdown();
        }
    }
}
