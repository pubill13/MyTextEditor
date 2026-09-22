using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Macros;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ListBox = System.Windows.Controls.ListBox;
using TabControl = System.Windows.Controls.TabControl;

internal static class SearchActionsVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        // No normal shutdown/settings path: these tests must not persist the user's workspace.
        Set(window, "_settingsReady", false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        var allDocuments = new List<DocumentViewModel>();
        try
        {
            window.Show(); Pump();
            var source = window.Documents[0];
            const string original = "123 한글😀\r\n문맥\r\n\r\n456 끝";
            source.Editor.LoadUtf8(Encoding.UTF8.GetBytes(original));
            var session = new SearchResultSession
            {
                Title = "검색 · 3", ToolTip = "test", ConditionSummary = "test",
                Snapshot = new SearchSnapshot(source, source.ContentRevision, original, "\r\n"),
                MatchedLineNumbers = new[] { 1, 3, 4 }
            };
            session.Rows.Add(new(1, "123 한글😀")); session.Rows.Add(new(2, "문맥", true));
            session.Rows.Add(new(3, "")); session.Rows.Add(new(4, "456 끝"));
            window.SearchSessions.Add(session);
            Field<TabControl>(window, "SearchResultTabs").SelectedItem = session;
            Call(window, "ShowSearchResults"); window.UpdateLayout(); Pump();
            var list = Field<ListBox>(window, "_activeResultsList");
            Check(list is not null && list.Items.Count == 4, "Real result list did not bind rows");
            window.Width = 1040; window.Height = 720;
            typeof(V18Verification).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { window, "search-actions-1040" });
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = System.IO.File.Create("artifacts/v1.8/visual/search-actions-1040-wpf.png")) encoder.Save(stream);
            Check(!session.IncludeLineNumbers, "Copy line numbers must default off");
            Check(VirtualizingPanel.GetIsVirtualizing(list), "Result virtualization disabled");
            Call(window, "CopyAllResults_Click", window, new RoutedEventArgs());
            const string extracted = "123 한글😀\r\n\r\n456 끝";
            Check(Clipboard.GetText() == extracted, "All copy lost body/order/blank line or included context/index");
            session.IncludeLineNumbers = true;
            Call(window, "CopyAllResults_Click", window, new RoutedEventArgs());
            Check(Clipboard.GetText() == "1: 123 한글😀\r\n3: \r\n4: 456 끝", "Numbered copy incorrect");
            Call(window, "CopyRows", new object[] { new[] { session.Rows[3], session.Rows[1] } });
            Check(Clipboard.GetText() == "2: 문맥\r\n4: 456 끝", "Selected/context copy order incorrect");
            Clipboard.SetText("unchanged");
            Call(window, "CopyRows", new object[] { Array.Empty<SearchResultRow>() });
            Check(Clipboard.GetText() == "unchanged", "Empty selection changed clipboard");

            Call(window, "NewDocument", "other", "other.txt"); Pump();
            list.ScrollIntoView(session.Rows[3]); window.UpdateLayout();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(session.Rows[3]);
            Check(item is not null, "Result row not realized");
            list.SelectedItems.Add(session.Rows[0]); list.SelectedItems.Add(session.Rows[3]);
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Check(ReferenceEquals(Field<TabControl>(window, "DocumentTabs").SelectedItem, source) && source.Editor.CurrentLine == 3, "Single click did not select source/line");
            Check(list.IsKeyboardFocusWithin && list.SelectedItems.Count == 2, "Navigation stole result focus or selection");
            VerifyResultWorkspace(window, session);
            list = Field<ListBox>(window, "_activeResultsList");

            source.Editor.ReplaceAll("changed");
            Call(window, "RefreshSearchSessionState");
            Check(!Field<System.Windows.Controls.Control>(window, "DeleteAllButton").IsEnabled && Field<Button>(window, "CopyAllResultsButton").IsEnabled, "Stale action states incorrect");
            Call(window, "ExtractAllResults_Click", window, new RoutedEventArgs());
            Check(window.Documents.Last().Text == extracted, "Stale extraction added indices or used current text");
            var active = Field<TabControl>(window, "DocumentTabs").SelectedItem;
            Call(window, "NavigateResult", list, session.Rows[0]);
            Check(ReferenceEquals(active, Field<TabControl>(window, "DocumentTabs").SelectedItem), "Stale navigation switched document");
            allDocuments.Add(source); window.Documents.Remove(source);
            session.IncludeLineNumbers = false;
            Call(window, "CopyAllResults_Click", window, new RoutedEventArgs());
            Check(Clipboard.GetText() == extracted, "Closed source snapshot copy failed");
            Call(window, "ExtractAllResults_Click", window, new RoutedEventArgs());
            Check(window.Documents.Last().Text == extracted, "Closed source extraction failed");
            Call(window, "UpdateStatus");
            Check(Field<Button>(window, "UndoButton").IsEnabled && Field<Button>(window, "ToolsUndoButton").IsEnabled && Field<Button>(window, "PreviewUndoButton").IsEnabled, "Undo buttons inconsistent");
            Call(window, "Undo_Click", window, new RoutedEventArgs());
            Check(window.Documents.Last().Text == "" && !Field<Button>(window, "UndoButton").IsEnabled, "Shared Undo did not restore document/state");
            window.SearchSessions.Clear(); Call(window, "RefreshSearchSessionState");
            Check(!Field<Button>(window, "CopyAllResultsButton").IsEnabled && !Field<Button>(window, "ExtractAllButton").IsEnabled, "No-result buttons enabled");
            VerifyMacro();
            Console.WriteLine("PASS search copy/extract snapshots, click source/focus, Undo buttons, macro cheap undo state");
            return 0;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop(); window.Hide();
            foreach (var document in window.Documents.Concat(allDocuments).Distinct()) document.Editor.ReleaseResources();
            Set(window, "_allowClose", true); window.Close(); app.Shutdown();
        }
    }

    private static void VerifyResultWorkspace(MainWindow window, SearchResultSession session)
    {
        var list = Field<ListBox>(window, "_activeResultsList");
        Check(list is not null, "Missing list before result workspace checks");
        Clipboard.SetText("before command");
        Check(ApplicationCommands.Copy.CanExecute(null, list), "Result copy command unavailable");
        ApplicationCommands.Copy.Execute(null, list);
        var copied = Clipboard.GetText();
        Check(copied == "1: 123 한글😀\r\n4: 456 끝", "Routed Ctrl+C copy lost selection");
        var height = Field<RowDefinition>(window, "ResultRow").ActualHeight;
        Call(window, "MinimizeResults_Click", window, new RoutedEventArgs()); Pump();
        Check(Field<RowDefinition>(window, "ResultRow").ActualHeight <= 29 && window.SearchSessions.Contains(session), "Minimize lost session or occupied editor space");
        Call(window, "RestoreResults_Click", window, new RoutedEventArgs()); Pump();
        Check(Math.Abs(Field<RowDefinition>(window, "ResultRow").ActualHeight - height) < 2, "Restore lost panel height");
        Call(window, "DetachResults_Click", window, new RoutedEventArgs()); Pump();
        var detached = Field<Window>(window, "_resultsWindow");
        Check(detached is not null, "Detached window disappeared");
        Check(detached.IsVisible && ReferenceEquals(detached.Content, Field<Border>(window, "ResultPanel")), "Result panel did not move into detached window");
        list = Field<ListBox>(window, "_activeResultsList");
        Check(list is not null, "Detached result list was not registered");
        Check(list.SelectedItems.Count == 2, "Detaching lost selected rows");
        ApplicationCommands.Copy.Execute(null, list);
        Check(Clipboard.GetText() == copied, "Detached copy differs from docked copy");
        detached.Activate(); list.Focus(); Pump();
        Check(detached.IsActive && list.IsKeyboardFocusWithin, "Detached keyboard test did not acquire focus");
        Clipboard.SetText("before keyboard copy");
        SendCopyKeys();
        WaitForClipboard(copied);
        detached.Width = 640; detached.UpdateLayout();
        foreach (var palette in ThemePalette.All)
        {
            Call(window, "ApplyTheme", palette.Id); Pump();
            var bar = Field<Border>(window, "ResultsCommandBar");
            Check(bar.ActualHeight <= 38, "Result toolbar uses more than one compact row");
            foreach (var name in new[] { "CopyAllResultsButton", "ExtractAllButton", "CopySelectedResultsButton", "ExtractSelectedButton", "DetachResultsButton" })
            {
                var button = Field<Button>(window, name);
                var point = button.TranslatePoint(new System.Windows.Point(), bar);
                Check(point.X >= 0 && point.X + button.ActualWidth <= bar.ActualWidth + 1, "Result command clipped at minimum detached width: " + name);
            }
        }
        Call(window, "MinimizeResults_Click", window, new RoutedEventArgs()); Pump();
        Check(!detached.IsVisible, "Detached minimize did not hide window");
        Call(window, "RestoreResults_Click", window, new RoutedEventArgs()); Pump();
        Check(detached.IsVisible, "Detached restore did not show window");
        detached.Close(); Pump();
        Check(Field<ListBox>(window, "_activeResultsList") is not null, "Docked result list was not registered");
        Check(Field<Window?>(window, "_resultsWindow") is null && Field<ListBox>(window, "_activeResultsList").SelectedItems.Count == 2,
            "Closing detached window did not dock results with selection intact");
        window.Activate();
        list = Field<ListBox>(window, "_activeResultsList"); list.Focus(); Pump();
        Clipboard.SetText("before docked keyboard copy");
        SendCopyKeys();
        WaitForClipboard(copied);
        list.SelectedItems.Clear();
        Clipboard.SetText("empty selection");
        SendCopyKeys(); Pump();
        Check(Clipboard.GetText() == "empty selection", "Ctrl+C without selected results changed clipboard");
        Console.WriteLine("PASS compact result toolbar in five themes, routed copy, minimize/restore, detach/dock and selection preservation");
    }

    private static void WaitForClipboard(string expected)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Pump();
            if (Clipboard.GetText() == expected) return;
            Thread.Sleep(10);
        } while (watch.Elapsed < TimeSpan.FromSeconds(2));
        throw new InvalidOperationException("Ctrl+C keyboard gesture did not copy selected results");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    private static void SendCopyKeys()
    {
        try
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30); Pump();
            keybd_event(0x43, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30); Pump();
        }
        finally
        {
            keybd_event(0x43, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            Thread.Sleep(30); Pump();
        }
    }

    private static void VerifyMacro()
    {
        int snapshots = 0, undoCalls = 0;
        MacroDocumentUndoState? state = new(Guid.NewGuid(), "one.log", true);
        var macro = new MacroWindow(new MacroWindowCallbacks
        {
            GetCurrentDocument = () => { snapshots++; throw new InvalidOperationException("Refresh read full text"); },
            ApplyResult = (_, _, _) => false, GetCurrentUndoState = () => state,
            UndoCurrentDocument = () => { undoCalls++; state = state! with { CanUndo = false }; }
        }, new MacroStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString(), "macros.json"))) { ShowInTaskbar = false };
        try
        {
            macro.Show(); macro.RefreshDocumentState();
            var undo = Field<Button>(macro, "_undo");
            Check(undo.IsEnabled && Field<TextBlock>(macro, "_targetDocument").Text.Contains("one.log"), "Macro undo target missing");
            Check((string)Field<Button>(macro, "_cancel").Content == "실행 중단", "Cancel still confused with Undo");
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(undoCalls == 1 && !undo.IsEnabled, "Macro Undo did not refresh state");
            state = new(Guid.NewGuid(), "two.log", true); macro.RefreshDocumentState();
            Check(undo.IsEnabled && Field<TextBlock>(macro, "_targetDocument").Text.Contains("two.log"), "Macro tab target did not refresh");
            using var execution = new CancellationTokenSource(); Set(macro, "_execution", execution); macro.RefreshDocumentState();
            Check(!undo.IsEnabled, "Running macro permits Undo");
            Set(macro, "_execution", null); state = null; macro.RefreshDocumentState();
            Check(!undo.IsEnabled && snapshots == 0, "Missing document/cheap state incorrect");
        }
        finally { macro.Close(); }
    }
}
