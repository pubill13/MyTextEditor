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
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MyTextEditor;component/Themes/Controls.xaml", UriKind.Relative) });
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
