using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;

internal static class OmniMergeVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("OneDark").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
        };
        var window = new DiffWorkspaceWindow(new(), callbacks, new("Consolas", 11, true));
        var view = window.OpenEmptyTab();
        window.Show();
        try
        {
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            left.ReplaceAll("첫 줄\n왼쪽 😀");
            right.ReplaceAll("첫 줄\n오른쪽 😀");
            Pump(() => view.DifferenceCount > 0);
            if (!view.IsReady || !view.HasUnsavedChanges) throw new Exception("Typing did not promote empty endpoints");
            var result = Field<TextDiffResult>(view, "_result");
            if (result.Blocks.Count != 1) throw new Exception("Unexpected typed diff");
            right.Undo(); Pump(() => view.DifferenceCount > 0);
            if (!view.RightIsReady || right.GetText() != "") throw new Exception("Undo lost scratch endpoint");
            right.Redo(); Pump(() => view.DifferenceCount > 0);
            var other = window.OpenEmptyTab(false);
            Pump(() => Field<ScintillaEditorHost>(other, "_leftEditor") is not null);
            Field<ScintillaEditorHost>(other, "_leftEditor").ReplaceAll("독립 탭");
            using (var answer = AnswerPrompt(2)) window.Close(); // IDCANCEL
            Pump(() => window.IsVisible && window.ComparisonCount == 2);
            if (!window.IsVisible || window.ComparisonCount != 2 || left.GetText() != "첫 줄\n왼쪽 😀")
                throw new Exception("Close cancellation lost tabs/text");
            var timer = Stopwatch.StartNew();
            using (var answer = AnswerPrompt(7))
            {
                window.Close(); // IDNO, each modified tab
                Pump(() => !window.IsVisible);
            }
            if (window.IsVisible || !Field<bool>(view, "_resourcesReleased") || !Field<bool>(other, "_resourcesReleased"))
                throw new Exception("Discard did not close/release both tabs");
            Console.WriteLine($"PASS direct typing/debounce/Undo/Redo and two-tab cancel/discard close {timer.Elapsed.TotalMilliseconds:F0} ms (including automated prompt answers)");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private sealed class PromptAnswer(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static IDisposable AnswerPrompt(int answer)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var hwnd = FindWindow("#32770", "비교 탭 닫기");
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x111, (IntPtr)answer, IntPtr.Zero);
        };
        timer.Start();
        return new PromptAnswer(timer);
    }
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static void Pump(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("Diff did not finish");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }
}
