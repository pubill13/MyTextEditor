using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using Forms = System.Windows.Forms;

internal static class Merge110Verification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
        };
        var original = "a\nold1\nb\nc\nold2\nd";
        var view = new DiffTabView(Endpoint("left", original), Endpoint("right", "a\nnew1\nb\nc\nnew2\nd"), new(), callbacks, new("Consolas", 11, false));
        var window = new Window { Content = view, Width = 1100, Height = 700, ShowInTaskbar = false };
        view.SetActive(true);
        try
        {
            window.Show();
            PumpUntil(() => view.DifferenceCount == 2);
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            Check(Field<System.Windows.Controls.MenuItem>(view, "MergeNextCheck").IsChecked == false, "New tab comparison defaults");
            var map = Field<DifferenceMap>(view, "DifferenceMapView");
            map.Navigate!(1);
            Check(Field<int>(view, "_currentBlockIndex") == 1, "Map navigation");
            var pixels = Field<int[]>(map, "_pixels");
            Check(pixels.Length <= Math.Ceiling(map.ActualHeight) + 1 && pixels.Any(p => p != 0), "Map pixel aggregation");
            map.SetViewport(1, 2);
            Check(ReferenceEquals(pixels, Field<int[]>(map, "_pixels")), "Map rebuilt on scroll");
            Field<CheckBox>(view, "LeftReadOnlyCheck").IsChecked = true;
            Call(view, "MergeCurrent", DiffSide.Right);
            Check(left.GetText() == original && left.IsReadOnly, "Locked target was overwritten");
            Field<CheckBox>(view, "LeftReadOnlyCheck").IsChecked = false;

            left.ReplaceAll(original + "\nextra");
            Check(Field<object?>(view, "_result") is null, "Edit retained stale result");
            PumpUntil(() => Field<object?>(view, "_result") is not null);
            Check(SendNative(left, Forms.Keys.F5), "F5 not consumed in Merge");
            PumpUntil(() => Field<object?>(view, "_result") is not null);
            left.Undo();
            SendNative(left, Forms.Keys.F5);
            PumpUntil(() => view.DifferenceCount == 2);
            map.Navigate!(0);
            Field<System.Windows.Controls.MenuItem>(view, "MergeNextCheck").IsChecked = true;
            Call(view, "MergeCurrent", DiffSide.Right);
            PumpUntil(() => view.DifferenceCount == 1);
            Check(Field<int>(view, "_currentBlockIndex") == 0 && left.GetText().Contains("new1"), "Merge-next failed");
            left.Undo();
            Check(left.GetText() == original, "Block merge is not one Undo");
            left.Redo();
            Check(left.GetText().Contains("new1"), "Block merge Redo failed");

            var before = left.GetText();
            SendNative(left, Forms.Keys.F5);
            view.SetActive(false);
            PumpFor(150);
            Check(left.GetText() == before && Field<object?>(view, "_result") is null, "Canceled comparison changed content or published old result");
            view.SetActive(true);
            SendNative(right, Forms.Keys.F5);
            PumpUntil(() => Field<object?>(view, "_result") is not null);
            foreach (var theme in ThemePalette.All)
            {
                app.Resources.MergedDictionaries[0] = theme.CreateResources();
                view.ApplyAppearance(new("Consolas", 11, theme.IsDark) { Palette = theme });
                window.UpdateLayout(); PumpFor(20);
                Check(map.Surface == app.Resources["SurfaceAltBrush"], "Map theme did not update");
            }
            Console.WriteLine("PASS Merge 1.10: automatic updates, native F5, inactive-tab cancellation, lock, map/cache/theme, merge-next, single Undo/Redo (programmatic WPF/native verification)");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Merge status: " + Field<System.Windows.Controls.TextBlock>(view, "StatusText").Text);
            throw;
        }
        finally { view.ReleaseResources(); window.Close(); app.Shutdown(); }
    }

    private static DiffEndpoint Endpoint(string name, string text) => new() { Kind = DiffEndpointKind.Clipboard, DisplayName = name, Text = text, NewLine = "\n", IsReadOnly = false };
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static bool SendNative(ScintillaEditorHost host, Forms.Keys keys) => (bool)typeof(ScintillaEditorHost).GetMethod("ProcessShortcut", Private)!.Invoke(host, [keys])!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PumpFor(int milliseconds) { var timer = Stopwatch.StartNew(); PumpUntil(() => timer.ElapsedMilliseconds >= milliseconds); }
    private static void PumpUntil(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Merge 1.10 verification timed out");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Forms.Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}

