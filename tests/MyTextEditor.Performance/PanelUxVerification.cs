using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using MyTextEditor.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

internal static class PanelUxVerification
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
        var window = new MainWindow { ShowInTaskbar = false, WindowState = WindowState.Normal, Width = 1380, Height = 860 };
        // Keep the real user's settings and documents untouched, including the closing flush.
        Set(window, "_settingsReady", false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        try
        {
            window.Show(); window.Activate(); Pump();
            VerifySettings();
            VerifyInputs(window);
            VerifyPanel(window);
            VerifyEscape(window);
            VerifyFavorites(window);
            VerifyLayout(window);
            Console.WriteLine("PASS panel visibility/width, search replacement/focus arbitration, Esc routing, settings JSON and five-theme layouts");
            Console.WriteLine($"Actual WPF DPI: {VisualTreeHelper.GetDpi(window).PixelsPerInchX:0}; physical Korean IME composition and other OS DPI settings were not automated.");
            return 0;
        }
        finally
        {
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop(); window.Hide();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            Set(window, "_allowClose", true); window.Close(); app.Shutdown();
        }
    }

    private static void VerifyInputs(MainWindow window)
    {
        var settings = Field<UserSettings>(window, "_settings");
        var all = Field<TextBox>(window, "SimpleAllBox");
        var any = Field<TextBox>(window, "SimpleAnyBox");
        var exclude = Field<TextBox>(window, "SimpleExcludeBox");
        var boxes = new[] { all, any, exclude };
        Call(window, "SetToolsVisible", true, false);
        Field<System.Windows.Controls.TabControl>(window, "ToolTabs").SelectedIndex = 0;
        all.Text = "AAA, 한글"; any.Text = "BBB"; exclude.Text = "CC";
        foreach (var (box, name) in boxes.Zip(new[] { "All", "Any", "Exclude" }))
        {
            box.BringIntoView(); box.Focus(); box.Select(1, 0); Pump();
            Check(settings.LastSearchField == name, $"Search field focus not tracked: {name}");
            Check(box.SelectionLength == 0, "Ordinary focus unexpectedly selects all");
            var previous = boxes.Select(item => item.Text).ToArray();
            for (int count = 0; count < 3; count++) Call(window, "ShowSearchInput");
            Pump();
            Check(box.IsKeyboardFocused && box.SelectedText == box.Text, $"Repeated Find did not select the active {name} input");
            box.SelectedText = "새 검색😀";
            Check(box.Text == "새 검색😀", "New search text accumulated instead of replacing the selection");
            for (int index = 0; index < boxes.Length; index++)
                if (boxes[index] != box) Check(boxes[index].Text == previous[index], "Find changed an unrelated search condition");
        }
        var from = Field<TextBox>(window, "ReplaceFromBox");
        var to = Field<TextBox>(window, "ReplaceToBox");
        from.Text = "기존 찾기"; to.Text = "유지할 치환";
        Call(window, "ShowSearchInput"); Call(window, "ShowReplaceInput"); Pump();
        Check(from.IsKeyboardFocused && from.SelectedText == from.Text, "Latest Ctrl+H request lost focus to an older Find request");
        Check(to.Text == "유지할 치환", "Replace focus cleared the replacement input");
        Call(window, "ShowReplaceInput"); Call(window, "ShowSearchInput"); Pump();
        Check(exclude.IsKeyboardFocused && exclude.SelectedText == exclude.Text, "Latest Find request lost to Replace");
        Field<CheckBox>(window, "MatchCaseCheck").IsChecked = true;
        Field<CheckBox>(window, "WholeWordCheck").IsChecked = true;
        Field<ComboBox>(window, "ContextLinesCombo").SelectedIndex = 2;
        string history = JsonSerializer.Serialize(settings.RecentSearches);
        Call(window, "ResetConditions_Click", window, new RoutedEventArgs()); Pump();
        Check(boxes.All(box => box.Text.Length == 0), "Condition reset left search inputs behind");
        Check(Field<CheckBox>(window, "MatchCaseCheck").IsChecked == true && Field<CheckBox>(window, "WholeWordCheck").IsChecked == true && Field<ComboBox>(window, "ContextLinesCombo").SelectedIndex == 2, "Condition reset changed search options");
        Check(JsonSerializer.Serialize(settings.RecentSearches) == history, "Condition reset changed recent history");
        Console.WriteLine("PASS repeated Find selects all, replacement is non-accumulating, ordinary focus unchanged, latest focus request wins");
    }

    private static void VerifyPanel(MainWindow window)
    {
        var settings = Field<UserSettings>(window, "_settings");
        var panel = Field<FrameworkElement>(window, "ToolPanel");
        var column = Field<ColumnDefinition>(window, "ToolColumn");
        var tabs = Field<System.Windows.Controls.TabControl>(window, "ToolTabs");
        var documents = Field<System.Windows.Controls.TabControl>(window, "DocumentTabs");
        var menu = Field<MenuItem>(window, "ToolsMenuItem");
        var toggle = Field<ToggleButton>(window, "ToolsToggleButton");
        Call(window, "SetToolsVisible", true, false);
        column.Width = new GridLength(417); window.UpdateLayout(); Pump();
        double width = column.ActualWidth, editorWidth = documents.ActualWidth;
        tabs.SelectedIndex = 1; Pump();
        var from = Field<TextBox>(window, "ReplaceFromBox");
        var scroll = Ancestor<ScrollViewer>(from)!;
        scroll.ScrollToVerticalOffset(135); Pump();
        double offset = scroll.VerticalOffset;
        string input = from.Text, history = JsonSerializer.Serialize(settings.RecentSearches);
        Call(window, "SetToolsVisible", false, false); window.UpdateLayout(); Pump();
        Check(panel.Visibility == Visibility.Collapsed && !menu.IsChecked && toggle.IsChecked == false, "Hide/menu/toggle state is inconsistent");
        Check(Field<ColumnDefinition>(window, "ToolSplitterColumn").ActualWidth == 0 && column.ActualWidth == 0, "Hidden panel still occupies columns");
        Check(documents.ActualWidth > editorWidth + 400, "Editor did not expand into hidden panel space");
        Check(Math.Abs(settings.ToolPanelWidth - width) < 1, "Panel width was not captured before hiding");
        Call(window, "SetToolsVisible", true, false); window.UpdateLayout(); Pump();
        Check(panel.Visibility == Visibility.Visible && menu.IsChecked && toggle.IsChecked == true && Math.Abs(column.ActualWidth - width) < 1, "Show did not restore width/menu/toggle state");
        Check(tabs.SelectedIndex == 1 && Math.Abs(scroll.VerticalOffset - offset) < 1 && from.Text == input, "Hide/show lost tool tab/input/scroll");
        Check(JsonSerializer.Serialize(settings.RecentSearches) == history, "Hide/show changed recent history");
        Call(window, "ShowSearchInput"); Pump();
        Check(Math.Abs(column.ActualWidth - width) < 1, "Find on visible panel reset its width");
        Check(settings.SelectedToolTab == 0, "Selected tool tab not tracked");
        menu.IsChecked = false; Call(window, "ToggleTools_Click", menu, new RoutedEventArgs()); Pump();
        Check(panel.Visibility == Visibility.Collapsed, "View menu does not use panel hide path");
        Call(window, "ShowReplaceInput"); Pump();
        Check(panel.Visibility == Visibility.Visible && settings.SelectedToolTab == 1, "Replace did not restore tool tab");
    }

    private static void VerifyEscape(MainWindow window)
    {
        var panel = Field<FrameworkElement>(window, "ToolPanel");
        Call(window, "ShowSearchInput"); Pump();
        var combo = Field<ComboBox>(window, "ContextLinesCombo");
        combo.Focus(); combo.IsDropDownOpen = true; Pump();
        RaiseEscape(combo, window); Pump();
        Check(panel.Visibility == Visibility.Visible, "Escape from open combo hid the whole panel");
        combo.IsDropDownOpen = false;
        var input = Field<TextBox>(window, "SimpleAllBox");
        input.Focus(); Pump(); RaiseEscape(input, window); Pump();
        Check(panel.Visibility == Visibility.Collapsed, "Escape inside panel did not hide it");
        Check(window.Documents[0].Editor.IsEditorFocused, "Escape did not restore editor focus");
        Call(window, "SetToolsVisible", true, false); Pump();
        window.Documents[0].Editor.FocusEditor(); Pump();
        RaiseEscape(window, window); Pump();
        Check(panel.Visibility == Visibility.Visible, "Escape outside panel hid it");
    }

    private static void VerifyLayout(MainWindow window)
    {
        Call(window, "ShowSearchInput"); Pump();
        var search = Field<Button>(window, "SearchButton");
        var scroll = Ancestor<ScrollViewer>(Field<TextBox>(window, "SimpleAllBox"))!;
        Check(Ancestor<ScrollViewer>(search) is null, "Find command is still inside scrolling content");
        foreach (var palette in ThemePalette.All)
        {
            Call(window, "ApplyTheme", palette.Id);
            foreach (double width in new[] { 1040d, 1920d })
            {
                window.Width = width; window.Height = 720; window.UpdateLayout(); Pump();
                Point before = search.TranslatePoint(new Point(), window);
                scroll.ScrollToBottom(); Pump();
                Check(Math.Abs(search.TranslatePoint(new Point(), window).Y - before.Y) < 1, "Search button moved when scrolling");
                foreach (var button in Descendants<ButtonBase>(window).Where(button => button.IsVisible && button.Content is string label && new[] { "새 문서", "열기", "저장", "검색·정리", "실행 취소", "찾기", "즐겨찾기 ▾" }.Contains(label)))
                {
                    Point position = button.TranslatePoint(new Point(), window);
                    Check(position.X >= -1 && position.Y >= -1 && position.X + button.ActualWidth <= window.ActualWidth + 1 && position.Y + button.ActualHeight <= window.ActualHeight + 1, $"Primary command clipped: {button.Content} at {width}/{palette.Id}");
                }
                if (width == 1040) Capture(window, palette.Id);
            }
        }
        Console.WriteLine("PASS fixed Find command and primary button bounds at 1040/1920 logical pixels, all five palettes");
    }

    private static void VerifyFavorites(MainWindow window)
    {
        var tools = (ToolDescriptor[])typeof(MainWindow).GetField("TextTools", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var settings = Field<UserSettings>(window, "_settings");
        settings.FavoriteToolIds = tools.Select(tool => tool.Id).ToList();
        window.Width = 1040; window.Height = 720;
        Call(window, "RenderFavoriteTools"); window.UpdateLayout(); Pump();
        var overflow = Field<Button>(window, "FavoriteOverflowButton");
        Check(overflow.IsVisible && overflow.IsEnabled, "Pinned tools are overflowing without an accessible menu");
        Check(Field<System.Windows.Controls.Panel>(window, "FavoriteToolsPanel").Children.OfType<Button>().Any(button => button.Visibility == Visibility.Collapsed), "Narrow toolbar did not move any overflowing favorites");
        ContextMenu? openedMenu = null;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is ContextMenu menu && ReferenceEquals(menu.PlacementTarget, overflow)) openedMenu = menu;
        }));
        Call(window, "FavoriteOverflow_Click", overflow, new RoutedEventArgs()); Pump();
        Check(openedMenu is not null && openedMenu.IsOpen, "Favorites overflow did not open");
        try
        {
            var items = openedMenu.Items.Cast<MenuItem>().ToArray();
            Check(items.Select(item => (item.Tag as ToolDescriptor)?.Id).SequenceEqual(settings.FavoriteToolIds), "Overflow omitted or reordered pinned favorites");
            Check(items.All(item => item.IsEnabled), "An overflow favorite is not reachable");
            var replace = items.Single(item => (item.Tag as ToolDescriptor)?.Id == TextToolIds.Replace);
            openedMenu.IsOpen = false;
            replace.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check(Field<System.Windows.Controls.TabControl>(window, "ToolTabs").SelectedIndex == 1 && Field<TextBox>(window, "ReplaceFromBox").IsKeyboardFocused, "Overflow favorite did not open and focus the selected tool");
        }
        finally { openedMenu.IsOpen = false; }
        Console.WriteLine($"PASS all {tools.Length} pinned tools reachable in order from narrow-toolbar overflow");
    }

    private static void VerifySettings()
    {
        var legacy = JsonSerializer.Deserialize<UserSettings>("{}")!;
        Check(legacy.LastSearchField == "All" && legacy.SelectedToolTab == 0, "Old settings do not receive safe panel defaults");
        legacy.LastSearchField = "Exclude"; legacy.SelectedToolTab = 1; legacy.ToolPanelVisible = false; legacy.ToolPanelWidth = 417;
        var restored = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(legacy))!;
        Check(restored.LastSearchField == "Exclude" && restored.SelectedToolTab == 1 && !restored.ToolPanelVisible && restored.ToolPanelWidth == 417, "Panel settings JSON roundtrip failed");
        restored.LastSearchField = "invalid"; restored.SelectedToolTab = 99;
        typeof(SettingsService).GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { restored });
        Check(restored.LastSearchField == "All" && restored.SelectedToolTab is 0 or 1, "Invalid persisted panel settings were not normalized");
    }

    private static void RaiseEscape(UIElement source, Window window)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        if (args.Handled) return;
        args.RoutedEvent = Keyboard.KeyDownEvent;
        source.RaiseEvent(args);
    }

    private static T? Ancestor<T>(DependencyObject item) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Capture(Window window, string theme)
    {
        string directory = System.IO.Path.GetFullPath("artifacts/panel-ux/visual");
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, $"panel-{theme}-1040-wpf.png"));
        encoder.Save(stream);
    }
}
