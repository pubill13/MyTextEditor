using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyTextEditor.Controls;
using MyTextEditor.Models;
using MyTextEditor.Macros;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;

internal static class OmniThemeVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{typeof(ScintillaEditorHost).Assembly.GetName().Name};component/Themes/Controls.xaml", UriKind.Relative) });
        var host = new ScintillaEditorHost();
        var window = new Window { Content = host, Width = 600, Height = 300, ShowInTaskbar = false };
        window.Show();
        try
        {
            host.LoadUtf8(System.Text.Encoding.UTF8.GetBytes("가😀 ABC 가😀"));
            host.SelectUtf16Range(0, 0);
            Require(host.FindOccurrence("가😀", true, false, false, out var firstWrap) &&
                !firstWrap && host.SelectedText == "가😀", "Native Unicode find first");
            Require(host.FindOccurrence("가😀", true, false, false, out var secondWrap) &&
                !secondWrap && host.SelectedText == "가😀", "Native Unicode find second");
            Require(host.FindOccurrence("가😀", true, false, false, out var thirdWrap) &&
                thirdWrap && host.SelectedText == "가😀", "Native Unicode find wrap");
            var menu = (Forms.ContextMenuStrip)typeof(ScintillaEditorHost).GetField("_contextMenu", Private)!.GetValue(host)!;
            foreach (var palette in ThemePalette.All)
            {
                app.Resources.MergedDictionaries[0] = palette.CreateResources();
                host.ApplyAppearance("Consolas", 11, palette);
                InspectMenu(menu, menu.Renderer, palette);
                VerifyNativeText(menu, palette);
                VerifyWpfMenu(palette);
                VerifyScrollbars(host, palette);
            }
            var process = typeof(ScintillaEditorHost).GetMethod("ProcessShortcut", Private)!;
            Require(!(bool)process.Invoke(host, [Forms.Keys.Escape])!, "Ordinary editor must leave Escape unhandled");
            host.ShortcutRequested += (_, e) => { if (e.Shortcut == EditorShortcut.CloseWindow) e.Handled = true; };
            Require((bool)process.Invoke(host, [Forms.Keys.Escape])!, "Diff opt-in Escape routing");
            VerifyMacroEscape();
            Console.WriteLine("PASS Omni theme menus, editor/WPF horizontal scrollbars across all palettes, Unicode native find/wrap, native Escape and Macro Escape");
            return 0;
        }
        finally { host.ReleaseResources(); window.Close(); app.Shutdown(); }
    }

    private static void VerifyScrollbars(ScintillaEditorHost host, ThemePalette palette)
    {
        host.LoadUtf8(System.Text.Encoding.UTF8.GetBytes(new string('한', 2000)));
        var native = (ScintillaNET.Scintilla)typeof(ScintillaEditorHost).GetField("_editor", Private)!.GetValue(host)!;
        native.Refresh();
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var bar = (Forms.Control)typeof(ScintillaEditorHost).GetField("_horizontalBar", Private)!.GetValue(host)!;
        Require(bar.Visible && !native.HScrollBar, "Native horizontal scrollbar bypasses application theme");
        Require(native.ScrollWidth > native.ClientSize.Width, $"Loaded Unicode long line lost horizontal range: range={native.ScrollWidth}, width={native.ClientSize.Width}, wrap={host.WordWrap}, end={native.Lines[0].EndPosition}, x={native.PointXFromPosition(native.Lines[0].EndPosition)}");
        using var bitmap = new Drawing.Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(bitmap, bar.ClientRectangle);
        Require(bitmap.GetPixel(0, 0).ToArgb() == palette.ScrollTrack.ToArgb(), $"{palette.Id}: native host horizontal track color");
        var thumb = (Drawing.Color)bar.GetType().GetField("_thumb", Private)!.GetValue(bar)!;
        Require(thumb == palette.ScrollThumb, $"{palette.Id}: horizontal thumb color");
        bar.GetType().GetMethod("OnKeyDown", Private)!.Invoke(bar, [new Forms.KeyEventArgs(Forms.Keys.End)]);
        Require(host.HorizontalOffset > 0, "Horizontal End key did not move editor");
        bar.GetType().GetMethod("OnKeyDown", Private)!.Invoke(bar, [new Forms.KeyEventArgs(Forms.Keys.Home)]);
        Require(host.HorizontalOffset == 0, "Horizontal Home key did not return editor");
        host.SetWordWrap(true);
        Require(!bar.Visible, "Wrapped editor has horizontal scrollbar");
        host.SetWordWrap(false);
        Require(bar.Visible, "Disabling wrap did not restore themed horizontal scrollbar");

        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 1400, Height = 800, Background = (SolidColorBrush)host.FindResource("SurfaceBrush") }
        };
        var window = new Window { Content = viewer, Width = 360, Height = 180, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        try
        {
            var horizontal = (System.Windows.Controls.Primitives.ScrollBar)viewer.Template.FindName("PART_HorizontalScrollBar", viewer);
            Require(horizontal.IsVisible && horizontal.ActualWidth > 200 && horizontal.ActualHeight >= 10, "WPF horizontal scrollbar layout");
            var color = ((SolidColorBrush)horizontal.Background).Color;
            Require(color.R == palette.ScrollTrack.R && color.G == palette.ScrollTrack.G && color.B == palette.ScrollTrack.B,
                $"{palette.Id}: WPF horizontal track color");
            viewer.ScrollToRightEnd();
            Require(viewer.HorizontalOffset >= 0, "WPF horizontal scrolling failed");
            window.UpdateLayout();
            var render = new System.Windows.Media.Imaging.RenderTargetBitmap((int)viewer.ActualWidth, (int)viewer.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            render.Render(viewer);
            var pixel = new byte[4];
            render.CopyPixels(new Int32Rect(render.PixelWidth - 4, render.PixelHeight - 4, 1, 1), pixel, 4, 0);
            Require(pixel[0] == palette.ScrollTrack.B && pixel[1] == palette.ScrollTrack.G && pixel[2] == palette.ScrollTrack.R,
                $"{palette.Id}: scrollbar corner uses system color: {pixel[2]},{pixel[1]},{pixel[0]}");
        }
        finally { window.Close(); }
    }

    private static void InspectMenu(Forms.ToolStrip menu, Forms.ToolStripRenderer renderer, ThemePalette palette)
    {
        Require(ReferenceEquals(menu.Renderer, renderer), "Submenu lost themed renderer");
        Require(menu.BackColor == palette.MarginBackground, "Submenu background");
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            Require(item.ForeColor == palette.EditorForeground, "Menu text must not inherit system black");
            if (item is Forms.ToolStripMenuItem child && child.HasDropDownItems) InspectMenu(child.DropDown, renderer, palette);
        }
    }

    private static void VerifyNativeText(Forms.ContextMenuStrip menu, ThemePalette palette)
    {
        var render = menu.Renderer.GetType().GetMethod("OnRenderItemText", Private)!;
        using var item = new Forms.ToolStripMenuItem("Copy 복사");
        menu.Items.Add(item);
        foreach (var state in new[] { "normal", "selected", "disabled" })
        {
            item.Enabled = state != "disabled";
            if (state == "selected") item.Select();
            var expected = !item.Enabled ? palette.MarginForeground : item.Selected ? palette.SelectionForeground : palette.EditorForeground;
            using var bitmap = new Drawing.Bitmap(200, 40);
            using var graphics = Drawing.Graphics.FromImage(bitmap);
            graphics.Clear(state == "selected" ? palette.SelectionBackground : palette.MarginBackground);
            render.Invoke(menu.Renderer, [new Forms.ToolStripItemTextRenderEventArgs(graphics, item, item.Text, new Drawing.Rectangle(2, 2, 195, 35), Drawing.Color.Black, menu.Font, Forms.TextFormatFlags.Left)]);
            var found = false;
            for (int y = 0; y < bitmap.Height && !found; y++)
                for (int x = 0; x < bitmap.Width && !found; x++) found = bitmap.GetPixel(x, y).ToArgb() == expected.ToArgb();
            Require(found, $"{palette.Id}/{state} did not render themed text");
        }
        menu.Items.Remove(item);
    }

    private static void VerifyWpfMenu(ThemePalette palette)
    {
        var menu = new System.Windows.Controls.Menu();
        var item = new MenuItem { Header = "메뉴", InputGestureText = "Ctrl+C" };
        item.Items.Add(new MenuItem { Header = "하위 메뉴" }); menu.Items.Add(item);
        var window = new Window { Content = menu, Width = 350, Height = 150, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        try
        {
            Require(item.Template.FindName("MenuBorder", item) is Border, "Explicit themed WPF menu template");
            var foreground = ((SolidColorBrush)item.Foreground).Color;
            Require(foreground.R == palette.EditorForeground.R && foreground.G == palette.EditorForeground.G, "WPF normal text");
            var selection = ((SolidColorBrush)window.FindResource("SelectionBrush")).Color;
            Require(selection.R == palette.UiSelection.R && selection.G == palette.UiSelection.G,
                "WPF menu selection resource");
            item.IsEnabled = false; window.UpdateLayout();
            Require(((SolidColorBrush)item.Foreground).Color == ((SolidColorBrush)window.FindResource("MutedBrush")).Color, "WPF disabled text");
        }
        finally { window.Close(); }
    }

    private static void VerifyMacroEscape()
    {
        var macro = new MacroWindow(new MacroWindowCallbacks { GetCurrentDocument = () => null, ApplyResult = (_, _, _) => false }, new MacroStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json")));
        macro.Show();
        var closed = false; macro.Closed += (_, _) => closed = true;
        macro.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(macro), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        Require(closed, "Macro Escape must follow Close flow");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
