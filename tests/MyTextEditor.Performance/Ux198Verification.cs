using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using TabControl = System.Windows.Controls.TabControl;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;

internal static class Ux198Verification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static DocumentViewModel Current(MainWindow window) => (DocumentViewModel)typeof(MainWindow).GetProperty("CurrentDocument", Private)!.GetValue(window)!;
    private static void Wait(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted) { Pump(); Thread.Sleep(5); if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException("File open timed out"); }
        task.GetAwaiter().GetResult(); Pump();
    }
    private static void TypeIntoCurrent(MainWindow window, string text)
    {
        var document = Current(window);
        var before = document.Text;
        TypeKeys(text);
        Check(document.Text.Length == before.Length + text.Length && document.Text.Contains(text), "Native editor did not receive typing: " + text + $" current={document.DisplayName}, actual={document.Text}, focus={Keyboard.FocusedElement?.GetType().Name}, visible={document.Editor.IsVisible}, generation={Field<long>(window, "_documentFocusGeneration")}, all={string.Join("|", window.Documents.Select(d => d.Text))}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private static void TypeKeys(string text)
    {
        foreach (var letter in text)
        {
            var key = (byte)char.ToUpperInvariant(letter);
            keybd_event(key, 0, 0, UIntPtr.Zero); Thread.Sleep(15); Pump();
            keybd_event(key, 0, 2, UIntPtr.Zero); Thread.Sleep(15); Pump();
        }
    }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1380, Height = 860 };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmniEdit-ux198-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var left = System.IO.Path.Combine(directory, "left.txt");
        var right = System.IO.Path.Combine(directory, "right.txt");
        var delayed = System.IO.Path.Combine(directory, "delayed.txt");
        System.IO.File.WriteAllText(left, "left\n"); System.IO.File.WriteAllText(right, "right\n"); System.IO.File.WriteAllText(delayed, "delayed\n");
        try
        {
            window.Show(); window.Activate(); Pump();
            TypeIntoCurrent(window, "initial");
            Call(window, "NewDocument", "", null); Pump(); TypeIntoCurrent(window, "newdoc");
            Wait((Task)Call(window, "OpenFilesAsync", new object[] { new[] { left, right } })!);
            Check(Current(window).FilePath == right, "Multi-file open did not select final file"); TypeIntoCurrent(window, "multiple");
            var count = window.Documents.Count;
            Wait((Task)Call(window, "OpenFilesAsync", new object[] { new[] { left } })!);
            Check(window.Documents.Count == count && Current(window).FilePath == left, "Duplicate open changed document count/selection");
            TypeIntoCurrent(window, "duplicate");

            Call(window, "SetToolsVisible", true, false);
            Field<TabControl>(window, "ToolTabs").SelectedIndex = 0;
            Field<System.Windows.Controls.CheckBox>(window, "UseConditionSearchCheck").IsChecked = false;
            var input = Field<TextBox>(window, "LiteralFindBox");
            var opening = (Task)Call(window, "OpenFilesAsync", new object[] { new[] { delayed } })!;
            Check(!opening.IsCompleted, "File-open fixture failed to yield before focus arbitration");
            input.BringIntoView(); input.Focus(); Keyboard.Focus(input);
            Wait(opening);
            Check(input.IsKeyboardFocused, "Delayed file load stole search input focus");
            TypeKeys("preserved");
            Check(input.Text.Contains("preserved") && !Current(window).Text.Contains("preserved"), "Typing went to document after focus cancellation");

            foreach (var theme in new[] { "Light", "Dark", "DarkPlus", "OneDark", "SolarizedLight" })
            {
                app.Resources.MergedDictionaries[0] = ThemePalette.Get(theme).CreateResources();
                foreach (var width in new[] { window.MinWidth, 1600d })
                {
                    window.Width = width; window.UpdateLayout(); Pump();
                    Check(Field<System.Windows.Controls.Button>(window, "UndoButton").ActualHeight == 26, "Toolbar button height drifted");
                    var tabs = Field<TabControl>(window, "DocumentTabs");
                    var tab = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(Current(window));
                    Check(tab.ActualHeight == 28, "Document header height drifted");
                    Check(Field<Grid>(window, "MainToolbarGrid").ActualHeight <= 34, "Main toolbar exceeded compact height");
                }
            }
            Console.WriteLine("PASS v1.9.8 native typing: initial/new/multiple/duplicate file focus and load-time focus cancellation; compact main toolbar/document tabs across five palettes and two widths");
            Console.WriteLine($"Actual DPI: {VisualTreeHelper.GetDpi(window).PixelsPerInchX:0}; physical IME, drag/drop and alternate OS DPI not covered by this test.");
            return 0;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close(); app.Shutdown();
            foreach (var path in new[] { left, right, delayed }) System.IO.File.Delete(path);
            System.IO.Directory.Delete(directory);
        }
    }
}
