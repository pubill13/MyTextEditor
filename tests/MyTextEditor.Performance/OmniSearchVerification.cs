using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
        var window = new MainWindow { ShowInTaskbar = false, Topmost = true };
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
            Field<System.Windows.Controls.ComboBox>(window, "ContextLinesCombo").SelectedIndex = 0;
            Field<CheckBox>(window, "MatchCaseCheck").IsChecked = false;
            Field<CheckBox>(window, "WholeWordCheck").IsChecked = false;
            Field<CheckBox>(window, "UseConditionSearchCheck").IsChecked = true;
            Field<TextBox>(window, "LiteralFindBox").Text = "";
            Require(!Field<System.Windows.Controls.Button>(window, "SearchButton").IsEnabled, "Empty literal must disable Find");
            Field<TextBox>(window, "LiteralFindBox").Text = "AAA";
            Require(!Field<System.Windows.Controls.Button>(window, "SearchButton").IsEnabled, "Condition mode must disable literal Find");
            Field<CheckBox>(window, "UseConditionSearchCheck").IsChecked = false;
            Require(Field<System.Windows.Controls.Button>(window, "SearchButton").IsEnabled, "Leaving condition mode must restore literal Find");
            VerifyRepeatedEnter(window);
            Call(window, "FindOccurrence_Click", window, new RoutedEventArgs());
            Require(window.Documents[0].Editor.SelectedText == "AAA" && window.SearchSessions.Count == 0,
                "Ordinary find must select one occurrence without creating results");
            Call(window, "FindOccurrence_Click", window, new RoutedEventArgs());
            Require(window.Documents[0].Editor.CurrentLine == 2, "Next ordinary occurrence did not move");
            Call(window, "Search_Click", window, new RoutedEventArgs());
            Require(window.SearchSessions.Last().Rows.Count == 2, "All Find did not create result rows");

            var previousSessions = window.SearchSessions.Count;
            Call(window, "SearchOpenDocuments_Click", window, new RoutedEventArgs());
            PumpUntil(() => window.SearchSessions.Count > previousSessions);
            Require(window.SearchSessions.Last().Rows.Count == 2 &&
                window.SearchSessions.Last().Rows.All(row => row.Source?.Document == window.Documents[0] && row.Source.Snapshot is null),
                "Open-document search lost snapshot/source");
            var openResults = window.SearchSessions.Last();
            window.Documents[0].Editor.ReplaceAll("modified");
            Require(openResults.Rows[0].Text == "가😀 AAA" && openResults.Rows[1].Text == "AAA 끝" &&
                openResults.Rows[0].Source!.DocumentRevision != window.Documents[0].ContentRevision,
                "Matched-line snapshots must survive source edits without retaining the whole source");
            window.Documents[0].Editor.Undo();
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
            Field<CheckBox>(window, "FolderSearchCheck").IsChecked = false;
            var largeText = string.Concat(Enumerable.Repeat("unmatched data 0123456789\n", 60000)) + "AAA retained 😀";
            window.Documents[0].Editor.ReplaceAll(largeText);
            previousSessions = window.SearchSessions.Count;
            Call(window, "Search_Click", window, new RoutedEventArgs());
            Require(window.SearchSessions.Count == previousSessions,
                "Large single-document search blocked the initiating UI event");
            Call(window, "CancelFolderSearch_Click", window, new RoutedEventArgs());
            PumpUntil(() => typeof(MainWindow).GetField("_folderSearchCancellation", Private)!.GetValue(window) is null);
            Require(window.SearchSessions.Count == previousSessions, "Cancelled search published incomplete results");
            Call(window, "SearchOpenDocuments_Click", window, new RoutedEventArgs());
            Require(window.SearchSessions.Count == previousSessions,
                "Open-document search blocked the initiating UI event");
            PumpUntil(() => window.SearchSessions.Count > previousSessions);
            Require(window.SearchSessions.Last().Rows.Count == 1 && window.SearchSessions.Last().Rows[0].Text == "AAA retained 😀" &&
                window.SearchSessions.Last().Rows[0].Source!.Snapshot is null,
                "Large open-document scan lost match content or retained a full snapshot");
            VerifyNoResults(window, otherFolder);
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

    private static void VerifyRepeatedEnter(MainWindow window)
    {
        var document = window.Documents[0];
        window.Activate();
        document.Editor.SelectUtf16Range(0, 0);
        Call(window, "ShowSearchInput");
        var input = Field<TextBox>(window, "LiteralFindBox");
        PumpUntil(() => input.IsKeyboardFocused);
        var original = document.Text;
        foreach (var expectedLine in new[] { 0, 2, 0 })
        {
            PressEnter();
            Require(input.IsKeyboardFocused && document.Editor.CurrentLine == expectedLine &&
                document.Editor.SelectedText == "AAA" && document.Text == original && !document.IsModified,
                "Find Enter must advance, wrap and keep the query focused without changing the document");
        }
        document.Editor.FocusEditor();
        PressEnter();
        Require(document.IsModified && document.Text != original, "Editor Enter must still edit after returning focus");
        document.Editor.Undo();
        document.Editor.SelectUtf16Range(0, 0);
        Console.WriteLine("PASS physical Enter advances and wraps without editing, then edits normally with editor focus");
    }

    private static void PressEnter()
    {
        keybd_event(0x0D, 0, 0, UIntPtr.Zero);
        try { Thread.Sleep(30); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        finally
        {
            keybd_event(0x0D, 0, 2, UIntPtr.Zero);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static void VerifyNoResults(MainWindow window, string folder)
    {
        var input = Field<TextBox>(window, "LiteralFindBox");
        input.Text = "no-result-" + Guid.NewGuid();
        var total = window.SearchSessions.Count;
        var args = new RoutedEventArgs();
        ExpectNoResultsDialog(window, () => Call(window, "FindOccurrence_Click", input, args));
        ExpectNoResultsDialog(window, () => Call(window, "Search_Click", input, args));
        ExpectNoResultsDialog(window, () => Call(window, "SearchOpenDocuments_Click", input, args));
        Call(window, "DetachTools_Click", window, args);
        var detached = Field<Window>(window, "_toolsWindow");
        Field<CheckBox>(window, "FolderSearchCheck").IsChecked = true;
        Field<TextBox>(window, "SearchFolderPathBox").Text = folder;
        ExpectNoResultsDialog(detached, () => Call(window, "Search_Click", input, args));
        Require(window.SearchSessions.Count == total + 3 &&
            window.SearchSessions.Skip(total).All(session => session.Rows.Count == 0), "Zero-result searches lost their empty sessions");
        Call(window, "DockToolsWindow", detached);
        Console.WriteLine("PASS zero-result dialogs for Find, All Find, open files and detached folder search");
    }

    private static void ExpectNoResultsDialog(Window owner, Action search)
    {
        var handle = new WindowInteropHelper(owner).Handle;
        var threadId = GetWindowThreadProcessId(handle, out var processId);
        Require(processId == Environment.ProcessId, "Dialog verification must target this test process");
        var capture = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                var observedOwner = IntPtr.Zero;
                EnumThreadWindows(threadId, (dialog, _) =>
                {
                    if (!IsWindowVisible(dialog)) return true;
                    var name = new StringBuilder(32);
                    GetClassName(dialog, name, name.Capacity);
                    if (name.ToString() != "#32770") return true;
                    var caption = new StringBuilder(64);
                    GetWindowText(dialog, caption, caption.Capacity);
                    if (caption.ToString() != "검색 결과") return true;
                    var dialogOwner = GetWindow(dialog, 4); // GW_OWNER
                    if (!PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero)) return true; // WM_CLOSE
                    observedOwner = dialogOwner;
                    return false;
                }, IntPtr.Zero);
                if (observedOwner != IntPtr.Zero) return observedOwner;
                Thread.Sleep(15);
            }
            return IntPtr.Zero;
        });
        search();
        WaitFor(capture);
        Require(capture.Result == handle, "Zero-result popup missing or owned by the wrong window");
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder caption, int maxCount);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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
