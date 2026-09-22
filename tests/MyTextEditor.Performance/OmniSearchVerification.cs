using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using MyTextEditor.Services;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

internal static class OmniSearchVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run()
    {
        VerifyLegacySearchMode();
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "one.log");
        File.WriteAllText(file, "가😀 AAA\nBBB\nAAA 끝", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "two.log"), "다른 AAA\n끝", new UTF8Encoding(false));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        Set(window, "_settingsReady", false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        try
        {
            window.Show();
            WaitFor(window.OpenStartupFilesAsync([file]));
            Require(window.Documents.Count == 1 && window.Documents[0].FilePath == file &&
                !window.Documents[0].IsModified,
                "Command-line file did not replace initial blank tab");
            Field<CheckBox>(window, "FolderSearchCheck").IsChecked = false;
            Field<CheckBox>(window, "UseConditionSearchCheck").IsChecked = true;
            Field<TextBox>(window, "LiteralFindBox").Text = "";
            Require(!Field<System.Windows.Controls.Button>(window, "SearchButton").IsEnabled, "Empty literal must disable Find");
            Field<TextBox>(window, "LiteralFindBox").Text = "AAA";
            Require(Field<System.Windows.Controls.Button>(window, "SearchButton").IsEnabled, "Saved condition mode must not disable literal Find");
            Call(window, "FindOccurrence_Click", window, new RoutedEventArgs());
            Require(window.Documents[0].Editor.SelectedText == "AAA" && window.SearchSessions.Count == 0,
                "Ordinary find must select one occurrence without creating results");
            Call(window, "FindOccurrence_Click", window, new RoutedEventArgs());
            Require(window.Documents[0].Editor.CurrentLine == 2, "Next ordinary occurrence did not move");
            Call(window, "Search_Click", window, new RoutedEventArgs());
            Require(window.SearchSessions.Last().Rows.Count == 2, "All Find did not create result rows");

            Call(window, "SearchOpenDocuments_Click", window, new RoutedEventArgs());
            Require(window.SearchSessions.Last().Rows.Count == 2 &&
                window.SearchSessions.Last().Rows.All(row => row.Source?.Snapshot is not null),
                "Open-document search lost snapshot/source");
            var otherFolder = Path.Combine(directory, "selected-folder");
            Directory.CreateDirectory(otherFolder);
            File.WriteAllText(Path.Combine(otherFolder, "chosen.log"), "선택한 폴더 AAA", new UTF8Encoding(false));
            Field<CheckBox>(window, "FolderSearchCheck").IsChecked = true;
            Field<TextBox>(window, "SearchFolderPathBox").Text = "";
            Require(!Field<System.Windows.Controls.Button>(window, "SearchAllButton").IsEnabled, "Folder search requires a selected folder");
            Field<TextBox>(window, "SearchFolderPathBox").Text = otherFolder;
            Call(window, "Search_Click", window, new RoutedEventArgs());
            PumpUntil(() => window.SearchSessions.Last().Rows.Any(row => row.Source?.DisplayName == "chosen.log"));
            Require(window.SearchSessions.Last().Rows.Count == 1, "Search must use selected folder instead of current document folder");
            Call(window, "CaptureWorkState");
            var saved = Field<UserSettings>(window, "_settings");
            Require(saved.SearchInFolder && saved.SearchFolderPath == otherFolder, "Folder scope settings were not captured");
            Field<CheckBox>(window, "FolderSearchCheck").IsChecked = false;
            Field<TextBox>(window, "SearchFolderPathBox").Text = "";
            Call(window, "RestoreWorkState");
            Require(Field<CheckBox>(window, "FolderSearchCheck").IsChecked == true &&
                Field<TextBox>(window, "SearchFolderPathBox").Text == otherFolder,
                "Selected folder and scope did not restore");
            Field<TextBox>(window, "SearchFolderPathBox").Text = directory;
            Field<CheckBox>(window, "IncludeSubfoldersCheck").IsChecked = false;
            Call(window, "Search_Click", window, new RoutedEventArgs());
            PumpUntil(() => window.SearchSessions.Last().Rows.Count == 3);
            var folderResult = window.SearchSessions.Last();
            Require(folderResult.Rows.Select(row => row.Source?.DisplayName).Distinct().Count() == 2,
                "Folder search did not include both files");
            Require(folderResult.Rows.Any(row => row.LineNumber == 1 && row.Source?.DisplayName == "two.log"),
                "Folder search lost file/line index");
            Call(window, "CopyAllResults_Click", window, new RoutedEventArgs());
            Require(System.Windows.Clipboard.GetText().Contains("다른 AAA", StringComparison.Ordinal),
                "Folder result copy failed");
            Console.WriteLine("PASS startup file, Unicode ordinary find, All Find, open-file and folder results");
            return 0;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
            window.Hide();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            Set(window, "_allowClose", true);
            window.Close();
            app.Shutdown();
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
    }

    private static void VerifyLegacySearchMode()
    {
        const string legacy = "{\"SearchState\":{\"SimpleAllTerms\":[\"AAA\"]}}";
        using var document = JsonDocument.Parse(legacy);
        var settings = JsonSerializer.Deserialize<UserSettings>(legacy)!;
        typeof(SettingsService).GetMethod("MigrateSearchModeFields", BindingFlags.Static | Private)!
            .Invoke(null, [document.RootElement, settings]);
        Require(settings.SearchState.UseConditions, "Old condition search was silently hidden by ordinary Find");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
    private static void WaitFor(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Search workflow did not finish");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
