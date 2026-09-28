using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;

internal static class TransformWorkVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted) { Check(watch.Elapsed.TotalSeconds < 20, "Transform timed out"); Pump(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult(); Pump();
    }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        try
        {
            window.Show(); Pump();
            var source = new CountingPreview();
            var all = new TransformPreviewList(source, null);
            var changed = new TransformPreviewList(source, TextChangeStatus.Changed);
            Check(all.Count == 100_002 && changed.Count == 33_334 && source.Materialized == 0,
                "Constructing/filtering lazy preview materialized line strings");
            Call(window, "ShowTransformPreview", new TextTransformResult("preview output", source, new(33_334, 33_334, 0)), "lazy test");
            Pump();
            Check(source.Materialized < 2_000, $"WPF eagerly materialized {source.Materialized} preview rows");
            var combo = Field<ComboBox>(window, "PreviewFilterCombo");
            combo.SelectedIndex = 1; Pump();
            Check(Field<DataGrid>(window, "PreviewGrid").Items.Count == 33_334, "Changed filter count mismatch");
            combo.SelectedIndex = 2; Pump();
            Check(Field<DataGrid>(window, "PreviewGrid").Items.Count == 33_334, "Skipped filter count mismatch");
            Call(window, "ClearTransformPreview"); combo.SelectedIndex = 0;

            var original = string.Concat(Enumerable.Repeat(" keep 한글 😀 \n", 100_000));
            Call(window, "NewDocument", original, "transform-test"); Pump();
            var document = window.Documents.Last();
            using var release = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            var uiThread = Environment.CurrentManagedThreadId;
            var workerThread = uiThread;
            Func<string, TextTransformResult> blocked = text =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Worker release timed out");
                return new TextTransformService().TrimWhitespace(text, "\n");
            };
            var pending = (Task)Call(window, "RunTransformAsync", blocked, "stale edit", false)!;
            Check(!pending.IsCompleted && started.Wait(TimeSpan.FromSeconds(5)) && workerThread != uiThread,
                "Large transformation did not leave the UI thread");
            document.Editor.ReplaceAll("newer edit"); release.Set(); Wait(pending);
            Check(document.Text == "newer edit", "Stale transform overwrote newer edits");

            document.Editor.ReplaceAll(original); release.Reset(); started.Reset();
            pending = (Task)Call(window, "RunTransformAsync", blocked, "stale tab", false)!;
            Check(started.Wait(TimeSpan.FromSeconds(5)), "Worker did not start");
            Call(window, "NewDocument", "other tab", "other"); release.Set(); Wait(pending);
            Check(document.Text == original && window.Documents.Last().Text == "other tab", "Stale transform overwrote a switched tab");

            Call(window, "SelectDocument", document); Pump();
            Func<string, TextTransformResult> cleanup = text => new TextTransformService().TrimWhitespace(text, "\n");
            Wait((Task)Call(window, "RunTransformAsync", cleanup, "apply", false)!);
            Check(document.Text == string.Concat(Enumerable.Repeat("keep 한글 😀\n", 100_000)), "Async cleanup output mismatch");
            document.Editor.Undo(); Check(document.Text == original, "Async cleanup did not undo in one step");
            document.Editor.ReplaceAll(" a \nb");
            Wait((Task)Call(window, "RunTransformAsync", cleanup, "preview apply", true)!);
            Call(window, "ApplyTransform_Click", window, new RoutedEventArgs());
            Check(document.Text == "a\nb", "Preview apply output mismatch");
            document.Editor.Undo(); Check(document.Text == " a \nb", "Preview apply did not undo in one step");
            Console.WriteLine("PASS async transform thread, stale edits/tabs, lazy WPF preview filters and single-step Undo");
            return 0;
        }
        finally
        {
            window.Hide();
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close(); app.Shutdown();
        }
    }

    private sealed class CountingPreview : ITextChangePreviewList
    {
        public int Materialized { get; private set; }
        public int Count => 100_002;
        public TextChangeStatus GetStatus(int index) => (TextChangeStatus)(index % 3);
        public TextChangePreview this[int index]
        {
            get { Materialized++; return new(index + 1, "before", "after", GetStatus(index)); }
        }
        public IEnumerator<TextChangePreview> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
