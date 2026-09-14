using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Help;
using MyTextEditor.Models;
using ScintillaNET;
using Application = System.Windows.Application;

internal static class V18Verification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MyTextEditor;component/Themes/Controls.xaml", UriKind.Relative) });
        try
        {
            VerifyHighlights();
            VerifyHelp();
            VerifySettings();
            VerifyScroll(100);
            VerifyScroll(5000);
            Console.WriteLine("PASS v1.8 integration");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void VerifyHighlights()
    {
        var host = new ScintillaEditorHost();
        var window = new Window { Content = host, Width = 900, Height = 600, ShowInTaskbar = false };
        window.Show();
        var editor = (Scintilla)typeof(ScintillaEditorHost).GetField("_editor", Private)!.GetValue(host)!;
        int ValueAt(int position) => (int)editor.DirectMessage(2507, (IntPtr)23, (IntPtr)Encoding.UTF8.GetByteCount(editor.GetTextRange(0, position)));
        bool Marked(int position) => ValueAt(position) != 0;
        void WaitHighlights() => Pump(() => typeof(ScintillaEditorHost).GetField("_highlightCancellation", Private)!.GetValue(host) == null);
        try
        {
            foreach (var palette in ThemePalette.All)
            {
                host.ApplyAppearance("Consolas", 11, palette);
                if (editor.SelectionBackColor == editor.CaretLineBackColor) throw new Exception("Selection/current line indistinguishable");
                if (editor.BackColor != palette.EditorBackground) throw new Exception("Palette not applied");
                if (editor.SelectionInactiveTextColor != palette.InactiveSelectionForeground) throw new Exception("Inactive selection contrast not applied");
            }
            host.LoadUtf8(Encoding.UTF8.GetBytes("한글😀 AAA\nAAA aaa"));
            host.SetSelection(new EditorTextRange(5, 3));
            host.AddSelectionHighlight(false); WaitHighlights();
            if (!Marked(5) || host.IsModified || host.CanUndo) throw new Exception("Highlight changed text/undo");
            editor.InsertText(0, "앞😀");
            PumpDelay(); WaitHighlights();
            if (!Marked(8)) throw new Exception("Highlight did not follow unicode insertion");
            editor.InsertText(9, "x");
            PumpDelay(); WaitHighlights();
            if (Marked(8)) throw new Exception("Edited highlight retained");
            host.LoadUtf8(Encoding.UTF8.GetBytes("AAA aaa AAA"));
            host.SetSelection(new EditorTextRange(0, 3)); host.AddSelectionHighlight(true); WaitHighlights();
            if (!Marked(0) || Marked(4) || !Marked(8)) throw new Exception("Literal case-sensitive highlight mismatch");
            host.HighlightColor = "#BB86FC";
            host.SetSelection(new EditorTextRange(1, 1)); host.AddSelectionHighlight(false); WaitHighlights();
            if (ValueAt(0) == ValueAt(1)) throw new Exception("Last highlight does not win");
            host.RemoveCurrentHighlight(); WaitHighlights();
            if (ValueAt(0) != ValueAt(1)) throw new Exception("Previous highlight not restored");
            host.IsReadOnly = true; host.ClearUserHighlights();
            if (Marked(0) || host.IsModified) throw new Exception("Read-only highlight clear failed");
            host.IsReadOnly = false;
            host.LoadUtf8(Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(1, 200).Select(i => $"{i}: INFO 한글 로그 AAA BBB 😀 " + new string('x', 160)))));
            host.SetSelection(new EditorTextRange(8, 2)); host.AddSelectionHighlight(true); WaitHighlights();
            host.SetSelection(new EditorTextRange(15, 7));
            foreach (var palette in ThemePalette.All)
            {
                host.ApplyAppearance("Consolas", 11, palette);
                window.Title = palette.Name + " / selected + highlight";
                editor.Focus();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                Capture(window, "editor-" + palette.Id);
            }
            Console.WriteLine("PASS themes, unicode highlight tracking, overlap, literal matches, readonly/undo isolation");
        }
        finally { host.ReleaseResources(); window.Close(); }
    }

    private static void VerifyHelp()
    {
        var window = new HelpWindow(); window.Show();
        foreach (var topic in HelpCatalog.Topics) window.NavigateToTopic(topic.Id);
        window.NavigateToTopic("highlights");
        var title = (System.Windows.Controls.TextBlock)typeof(HelpWindow).GetField("TopicTitle", Private)!.GetValue(window)!;
        if (!title.Text.Contains("하이라이트")) throw new Exception("Context help navigation failed");
        Capture(window, "help");
        if (HelpCatalog.Topics.Select(t => t.Category).Distinct().Count() != 7) throw new Exception("Help categories missing");
        window.Close();
        Console.WriteLine("PASS seven help categories and topic navigation");
    }

    private static void VerifySettings()
    {
        var normalize = typeof(MyTextEditor.Services.SettingsService).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var palette in ThemePalette.All)
        {
            var settings = new UserSettings { Theme = palette.Id, EditorFontSize = 6, HighlightColor = "#56B4E9" };
            settings.Diff.FontSize = 72;
            settings = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
            normalize.Invoke(null, [settings]);
            if (settings.Theme != palette.Id || settings.EditorFontSize != 6 || settings.Diff.FontSize != 72 || settings.HighlightColor != "#56B4E9")
                throw new Exception("Appearance settings not restored");
        }
        var legacy = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"Theme\":\"Dark\",\"EditorFontSize\":15}")!;
        normalize.Invoke(null, [legacy]);
        if (legacy.Diff.FontSize != 11 || legacy.EditorFontSize != 15) throw new Exception("Legacy settings changed");
        legacy.EditorFontSize = double.NaN; legacy.Diff.FontSize = double.PositiveInfinity; legacy.HighlightColor = "invalid";
        normalize.Invoke(null, [legacy]);
        if (legacy.EditorFontSize != 15 || legacy.Diff.FontSize != 11 || legacy.HighlightColor != "#F2CC60") throw new Exception("Invalid settings fallback failed");
        Console.WriteLine("PASS five themes / size boundaries / color persistence / legacy defaults");
    }

    private static void VerifyScroll(int changes)
    {
        var left = new StringBuilder(); var right = new StringBuilder();
        for (int i = 0; i < 50000; i++) { left.AppendLine($"line {i}"); right.AppendLine(i % (50000 / changes) == 0 ? $"changed {i}" : $"line {i}"); }
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
        };
        var view = new DiffTabView(
            new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "left", Text = left.ToString(), NewLine = "\r\n" },
            new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "right", Text = right.ToString(), NewLine = "\r\n" },
            new DiffWindowOptions(), callbacks, new DiffAppearance("Consolas", 11, true) { Palette = ThemePalette.Get("OneDark") });
        var window = new Window { Content = view, Width = 1280, Height = 720, ShowInTaskbar = false };
        window.Show();
        try
        {
            view.SetActive(true);
            Pump(() => typeof(DiffTabView).GetField("_result", Private)!.GetValue(view) is TextDiffResult, 30);
            var host = (ScintillaEditorHost)typeof(DiffTabView).GetField("_leftEditor", Private)!.GetValue(view)!;
            var rightHost = (ScintillaEditorHost)typeof(DiffTabView).GetField("_rightEditor", Private)!.GetValue(view)!;
            var render = typeof(DiffTabView).GetMethod("RenderMergeGutter", Private)!;
            var samples = new List<double>();
            for (int i = 0; i < 150; i++)
            {
                var watch = Stopwatch.StartNew();
                host.ScrollToLine((i * 317) % 49000);
                render.Invoke(view, [null, EventArgs.Empty]);
                watch.Stop(); samples.Add(watch.Elapsed.TotalMilliseconds);
                if (Math.Abs(host.FirstVisibleLine - rightHost.FirstVisibleLine) > 1) throw new Exception("Scroll sync mismatch");
            }
            samples.Sort(); double p95 = samples[(int)(samples.Count * .95)];
            if (changes == 5000)
            {
                view.ApplyAppearance(new DiffAppearance("Consolas", 6, false) { Palette = ThemePalette.Get("Light") });
                host.ScrollToLine(10); render.Invoke(view, [null, EventArgs.Empty]);
                window.UpdateLayout();
                var pool = (System.Collections.IEnumerable)typeof(DiffTabView).GetField("_gutterPool", Private)!.GetValue(view)!;
                foreach (System.Windows.Controls.StackPanel panel in pool)
                    if (panel.Visibility == Visibility.Visible && ((System.Windows.Controls.Button)panel.Children[0]).ActualHeight > 25)
                        throw new Exception("Small font gutter button height ignored");
                Capture(window, "merge-6pt");
            }
            var check = (System.Windows.Controls.CheckBox)typeof(DiffTabView).GetField("ScrollSyncCheck", Private)!.GetValue(view)!;
            check.IsChecked = false;
            int unchanged = rightHost.FirstVisibleLine;
            host.ScrollToLine(25000);
            if (rightHost.FirstVisibleLine != unchanged) throw new Exception("Scroll sync OFF ignored");
            check.IsChecked = true;
            view.SetActive(false);
            host.ScrollToLine(500);
            if (rightHost.FirstVisibleLine != unchanged) throw new Exception("Inactive tab performed sync");
            Console.WriteLine($"50k lines / {changes} changes: scroll+gutter p95={p95:F2}ms");
            if (p95 > 16) throw new Exception("Scroll p95 exceeded 16ms");
        }
        finally { view.ReleaseResources(); window.Close(); }
    }

    private static void PumpDelay() { var watch = Stopwatch.StartNew(); Pump(() => watch.ElapsedMilliseconds > 400); }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var renderWait = Stopwatch.StartNew();
        Pump(() => renderWait.ElapsedMilliseconds >= 80);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var rect);
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(hwnd, dc, 2)) throw new Exception("Window capture failed"); }
            finally { graphics.ReleaseHdc(dc); }
        }
        System.IO.Directory.CreateDirectory("artifacts/v1.8/visual");
        bitmap.Save($"artifacts/v1.8/visual/{name}.png", System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Captured {name}, actual DPI={GetDpiForWindow(hwnd)}");
    }
    private static void Pump(Func<bool> condition, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        }
    }
}
