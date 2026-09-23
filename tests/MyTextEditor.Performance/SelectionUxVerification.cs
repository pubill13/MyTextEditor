using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;

internal static class SelectionUxVerification
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
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            window.Show(); window.Activate(); Pump();
            var editor = window.Documents[0].Editor;
            editor.LoadUtf8(Encoding.UTF8.GetBytes("한글 😀\r\n다음"));
            editor.SelectUtf16Range(0, 5); Pump();
            Call(window, "UpdateStatus");
            Check(Field<System.Windows.Controls.TextBlock>(window, "CaretStatus").Text.Contains("선택 5자"), "Selection length incorrect");
            var from = Field<TextBox>(window, "ReplaceFromBox");
            var to = Field<TextBox>(window, "ReplaceToBox");
            to.Text = "유지";
            Call(window, "ShowReplaceInput"); Pump();
            Check(from.Text == "한글 😀" && from.SelectionLength == 5 && to.Text == "유지", "Selected replace seed or focus incorrect");
            from.Text = "다른 입력";
            Call(window, "ShowReplaceInput"); Pump();
            Check(from.Text == "다른 입력" && from.SelectionLength == from.Text.Length, "Repeated replace overwrote input");
            editor.FocusEditor(); Pump(); Call(window, "ShowSearchInput"); Pump();
            Check(Field<TextBox>(window, "LiteralFindBox").Text == "한글 😀", "Selected Find seed incorrect");
            editor.SelectUtf16Range(0, 0); Pump(); Call(window, "UpdateStatus");
            Check(!Field<System.Windows.Controls.TextBlock>(window, "CaretStatus").Text.Contains("선택"), "Cleared selection count remains");
            Call(window, "ShowReplaceInput"); Pump();
            Check(from.Text == "다른 입력", "No selection must preserve existing replacement input");
            editor.SelectUtf16Range(0, 9); Pump();
            Call(window, "ShowReplaceInput"); Pump();
            Check(from.Text == "한글 😀\r\n다음" && from.SelectionLength == 9, "Multiline replacement seed changed line endings");
            var paths = Enumerable.Range(0, 3).Select(i => Path.Combine(directory, $"{i}.log")).ToArray();
            var content = string.Concat(Enumerable.Repeat("AAA BBB 한글 로그 0123456789\r\n", 3_000_000));
            foreach (var path in paths) File.WriteAllText(path, content, new UTF8Encoding(false));
            var watch = Stopwatch.StartNew();
            var task = window.OpenStartupFilesAsync(paths.Concat([paths[0]]));
            while (!task.IsCompleted) { Check(watch.Elapsed.TotalSeconds < 60, "Batch open timed out"); Pump(); Thread.Sleep(1); }
            task.GetAwaiter().GetResult();
            Check(window.Documents.Count == 3, "Batch duplicate or empty-tab handling incorrect");
            Check(window.Documents.Select(d => d.FilePath).SequenceEqual(paths), "Batch order incorrect");
            Check(window.Documents.All(d => d.Text == content && !d.IsModified), "Batch content or dirty state incorrect");
            Console.WriteLine($"PASS selection count, Ctrl+H/F seeding and repeat focus; three large files ({paths.Sum(path => new FileInfo(path).Length):N0} bytes) + duplicate opened in {watch.Elapsed.TotalSeconds:F3}s");
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
}
