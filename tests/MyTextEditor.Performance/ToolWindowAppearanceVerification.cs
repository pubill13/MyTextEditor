using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Controls;
using MyTextEditor.Diff;
using MyTextEditor.Macros;
using MyTextEditor.Models;
using MyTextEditor.Core.Macros;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using ComboBox = System.Windows.Controls.ComboBox;

internal static class ToolWindowAppearanceVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Isolate(MainWindow window)
    {
        typeof(MainWindow).GetField("_settingsReady", Hidden)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
    }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var owners = new List<MainWindow>();
        var views = new List<DiffTabView>();
        try
        {
            var first = OpenTools("SolarizedLight", 15);
            var second = OpenTools("Dark", 19);
            var before = Field<TextBox>(first.Macro, "_before");
            var after = Field<TextBox>(first.Macro, "_after");
            before.Text = "변경 전\n한글 preview";
            after.Text = "변경 후\n한글 preview";
            var editor = Field<ScintillaEditorHost>(first.View, "_leftEditor");
            editor.ReplaceAll("theme switching must preserve Undo");
            var otherBrush = second.Macro.Background.ToString();
            var otherFont = Field<TextBox>(second.Macro, "_before").FontFamily.Source;
            foreach (var theme in ThemePalette.All)
            {
                Call(first.Owner, "ApplyTheme", theme.Id);
                Pump();
                VerifyTheme(first.Macro, first.Diff, first.View, theme);
                Check(second.Macro.Background.ToString() == otherBrush && second.Diff.Background.ToString() == otherBrush,
                    "Changing one owner changed another owner's tool theme");
                Check(app.Resources["WindowBrush"].ToString() == ThemePalette.Get("Light").CreateResources()["WindowBrush"].ToString(),
                    "Owner theme changed application resources");
            }
            foreach (var size in new[] { 6d, 15d, 72d })
            {
                Call(first.Owner, "ApplyEditorFont", "Consolas");
                Call(first.Owner, "ApplyEditorFontSize", size, Field<ComboBox>(first.Owner, "FontSizeCombo"));
                first.Macro.Width = first.Macro.MinWidth;
                first.Macro.Height = first.Macro.MinHeight;
                Pump();
                Check(before.FontSize == size && after.FontSize == size, "Macro preview size did not follow owner");
                Check(before.FontFamily.Source == "Consolas" && after.FontFamily.Source == "Consolas", "Macro preview font did not follow owner");
                Check(Field<DiffAppearance>(first.View, "_appearance").FontSize == 11, "Owner font size overwrote Merge's independent size");
                Check(Field<DiffAppearance>(first.View, "_appearance").FontFamily == "Consolas", "Merge font family did not follow owner");
                Check(Field<Button>(first.Macro, "_preview").FontSize == 13, "Editor size leaked into macro controls");
                Check(before.ActualWidth > 50 && before.ActualHeight > 40 && after.ActualWidth > 50 && after.ActualHeight > 40,
                    "Macro previews collapsed at minimum window size");
                Check(before.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && before.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto,
                    "Large preview font has no scroll access");
            }
            Check(Field<TextBox>(second.Macro, "_before").FontSize == 19 && Field<TextBox>(second.Macro, "_before").FontFamily.Source == otherFont,
                "Changing one owner changed another owner's preview font");
            first.Macro.Hide(); first.Diff.Hide();
            Call(first.Owner, "ApplyTheme", "OneDark");
            Call(first.Owner, "OpenMacro_Click", first.Owner, new RoutedEventArgs());
            Call(first.Owner, "OpenDiff_Click", first.Owner, new RoutedEventArgs());
            Pump();
            Check(ReferenceEquals(first.Macro, Field<MacroWindow>(first.Owner, "_macroWindow")), "Hidden macro window was recreated");
            VerifyTheme(first.Macro, first.Diff, first.View, ThemePalette.Get("OneDark"));
            Check(before.Text == "변경 전\n한글 preview" && after.Text == "변경 후\n한글 preview", "Appearance changes reset macro preview text");
            Check(editor.CanUndo && editor.GetText() == "theme switching must preserve Undo", "Appearance changes reset Merge text/Undo");
            editor.Undo();
            Check(editor.GetText() == "", "Merge Undo changed after appearance updates");
            VerifyMacroPlacementAndSave();
            Console.WriteLine("PASS real owner-created Merge/Macro: five themes, popup colors, two-owner isolation, preview fonts 6/15/72, independent Merge size, minimum layout, hide/show and preserved text/Undo");
            return 0;
        }
        finally
        {
            foreach (var view in views)
            {
                typeof(DiffTabView).GetField("_leftDirty", Hidden)!.SetValue(view, false);
                typeof(DiffTabView).GetField("_rightDirty", Hidden)!.SetValue(view, false);
            }
            foreach (var owner in owners) { Isolate(owner); owner.Close(); Pump(); }
            app.Shutdown();
        }

        (MainWindow Owner, MacroWindow Macro, DiffWorkspaceWindow Diff, DiffTabView View) OpenTools(string theme, double size)
        {
            var owner = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
            owners.Add(owner); Isolate(owner);
            Field<UserSettings>(owner, "_settings").Diff.FontSize = 11;
            owner.Show(); Pump();
            Call(owner, "ApplyTheme", theme);
            Call(owner, "ApplyEditorFontSize", size, Field<ComboBox>(owner, "FontSizeCombo"));
            Call(owner, "OpenMacro_Click", owner, new RoutedEventArgs());
            var macro = Field<MacroWindow>(owner, "_macroWindow");
            var diff = (DiffWorkspaceWindow)Call(owner, "EnsureDiffWorkspace")!;
            var view = diff.OpenEmptyTab(); views.Add(view);
            Pump();
            VerifyTheme(macro, diff, view, ThemePalette.Get(theme));
            return (owner, macro, diff, view);
        }
    }

    private static void VerifyMacroPlacementAndSave()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OmniEdit-ToolWindow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "macros.json");
        var store = new MacroStore(path);
        var callbacks = new MacroWindowCallbacks { GetCurrentDocument = () => null, ApplyResult = (_, _, _) => false };
        var macro = new MacroWindow(callbacks, store, new MacroWindowPlacement(1000, 650, -1_000_000, -1_000_000)) { Topmost = true };
        MacroWindowPlacement? saved = null;
        macro.PlacementChangedByUser += placement => saved = placement;
        try
        {
            macro.Show(); macro.Activate(); Pump();
            Check(saved is not null && macro.Left > -100_000 && macro.Top > -100_000, "Offscreen macro placement was not repaired");
            macro.Width = 960; macro.Height = 620;
            macro.Left += 10; macro.Top += 10; Pump();
            Check(saved is not null && saved.Width == 960 && saved.Height == 620 && saved.Left == macro.Left && saved.Top == macro.Top,
                "Macro placement change was not reported");
            var name = Field<TextBox>(macro, "_name");
            var draft = Field<MacroDefinition>(macro, "_draft");
            draft.Steps.Add(new MacroStep { Operation = MacroOperation.TrimWhitespace });
            name.Text = "shortcut save verification";
            name.Focus(); Pump();
            try
            {
                keybd_event(0x11, 0, 0, UIntPtr.Zero); Thread.Sleep(30); Pump();
                keybd_event(0x53, 0, 0, UIntPtr.Zero); Thread.Sleep(30); Pump();
            }
            finally
            {
                keybd_event(0x53, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(30); Pump();
            }
            Check(File.Exists(path) && store.Load().Single().Name == name.Text && !macro.HasUnsavedChanges,
                "Ctrl+S did not save the macro through the focused input");
            name.Text = "dirty close cancellation";
            WithCancelDialog(macro, macro.Close);
            Check(macro.IsVisible && macro.HasUnsavedChanges, "Canceling macro close discarded draft or closed window");
            var placement = saved!;
            typeof(MacroWindow).GetField("_prepared", Hidden)!.SetValue(macro, true);
            macro.Close(); Pump();
            var reopened = new MacroWindow(callbacks, store, placement);
            try
            {
                reopened.Show(); Pump();
                Check(reopened.Width == placement.Width && reopened.Height == placement.Height && reopened.Left == placement.Left && reopened.Top == placement.Top,
                    "Saved macro placement did not restore");
                Check(Field<TextBox>(reopened, "_name").Text == "shortcut save verification", "Canceled draft overwrote saved macro");
            }
            finally { reopened.Close(); Pump(); }
            Console.WriteLine("PASS macro placement repair/restore, physical Ctrl+S in name input, and dirty-close cancellation with isolated store");
        }
        finally
        {
            typeof(MacroWindow).GetField("_prepared", Hidden)!.SetValue(macro, true);
            if (macro.IsVisible) macro.Close();
            File.Delete(path); Directory.Delete(folder);
        }
    }

    private static void WithCancelDialog(Window owner, Action action)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var thread = GetWindowThreadProcessId(handle, out var process);
        Check(process == Environment.ProcessId, "Dialog belongs to another process");
        using var cancellation = new CancellationTokenSource();
        var response = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            while (!cancellation.IsCancellationRequested && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                var responded = false;
                EnumThreadWindows(thread, (dialog, _) =>
                {
                    if (GetWindow(dialog, 4) != handle || !IsWindowVisible(dialog)) return true;
                    var name = new StringBuilder(32); GetClassName(dialog, name, name.Capacity);
                    if (name.ToString() != "#32770") return true;
                    var button = GetDlgItem(dialog, 2);
                    if (button != IntPtr.Zero) responded = PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    return !responded;
                }, IntPtr.Zero);
                if (responded) return true;
                Thread.Sleep(20);
            }
            return false;
        });
        try { action(); Check(response.GetAwaiter().GetResult(), "Expected macro close confirmation did not appear"); }
        finally { cancellation.Cancel(); response.GetAwaiter().GetResult(); }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    private static void VerifyTheme(MacroWindow macro, DiffWorkspaceWindow diff, DiffTabView view, ThemePalette theme)
    {
        var expected = theme.CreateResources();
        void BrushMatches(System.Windows.Media.Brush actual, string key,
            [System.Runtime.CompilerServices.CallerArgumentExpression("actual")] string target = "") =>
            Check(actual.ToString() == expected[key].ToString(), $"{theme.Id}: {target} {key} mismatch ({actual})");
        BrushMatches(macro.Background, "WindowBrush");
        BrushMatches(diff.Background, "WindowBrush");
        BrushMatches(view.Background, "WindowBrush");
        var input = Field<TextBox>(macro, "_name");
        BrushMatches(input.Background, "SurfaceBrush");
        BrushMatches(input.Foreground, "TextBrush");
        BrushMatches(input.BorderBrush, "BorderBrush");
        BrushMatches(input.CaretBrush, "TextBrush");
        BrushMatches(input.SelectionBrush, "AccentBrush");
        BrushMatches(Field<Button>(macro, "_preview").Background, "SurfaceAltBrush");
        BrushMatches(Field<Button>(view, "PreviousButton").Foreground, "TextBrush");
        var options = Field<MenuItem>(view, "OptionsMenu");
        options.IsSubmenuOpen = true;
        try
        {
            Pump();
            var item = (MenuItem)options.Items[0];
            Check(item.TryFindResource("TextBrush") is not null, "Popup item did not inherit tool-window theme resources");
            BrushMatches(item.Foreground, "TextBrush");
        }
        finally { options.IsSubmenuOpen = false; Pump(); }
    }
}
