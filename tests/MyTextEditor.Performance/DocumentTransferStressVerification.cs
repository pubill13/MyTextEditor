using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;

internal static class DocumentTransferStressVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, Hidden)!.Invoke(target, args);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Isolate(MainWindow window)
    {
        typeof(MainWindow).GetField("_settingsReady", Hidden)!.SetValue(window, false);
        ((DispatcherTimer)typeof(MainWindow).GetField("_settingsSaveTimer", Hidden)!.GetValue(window)!).Stop();
        Call(window, "StopFileSync");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static int Run(bool windowLifetimeOnly = false)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        try
        {
            if (windowLifetimeOnly)
            {
                VerifyWindowLifetime();
                return 0;
            }
            var source = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
            var target = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
            Isolate(source); Isolate(target);
            source.Show(); target.Show(); Pump();
            var document = source.Documents[0];
            var editor = document.Editor;
            editor.SetWordWrap(false);
            var bytes = new byte[300_000_000];
            Array.Fill(bytes, (byte)'x');
            for (var index = 99; index < bytes.Length; index += 100) bytes[index] = (byte)'\n';
            editor.LoadUtf8(bytes); bytes = null!;
            using var process = Process.GetCurrentProcess();
            var samples = new List<(int Handles, long Memory)>();
            var watch = Stopwatch.StartNew();
            for (var round = 0; round < 1000; round++)
            {
                var from = round % 2 == 0 ? source : target;
                var to = round % 2 == 0 ? target : source;
                Check((bool)Call(from, "MoveDocumentToWindow", document, to, false, 0)!, "Transfer rejected");
                Pump();
                Check(ReferenceEquals(document.Editor, editor) && editor.Utf8ByteLength == 300_000_000, "Document changed during stress");
                if ((round + 1) % 100 != 0) continue;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
                samples.Add((process.HandleCount, process.PrivateMemorySize64));
                Console.WriteLine($"stress transfers={round + 1} elapsedMs={watch.Elapsed.TotalMilliseconds:F0} handles={process.HandleCount} privateBytes={process.PrivateMemorySize64}");
            }
            Check(samples[^1].Handles <= samples[1].Handles + 5, "Native handles grew after warmup");
            Check(samples[^1].Memory <= samples[1].Memory + 50_000_000, "Private memory grew over 50MB after warmup");
            Console.WriteLine("PASS 1000 transfers of 300MB, document identity, stable post-warmup handles and memory");
            return 0;
        }
        finally
        {
            foreach (var window in app.Windows.OfType<MainWindow>().ToArray())
            {
                Isolate(window);
                window.Close(); Pump();
            }
            app.Shutdown();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (MainWindow Current, List<WeakReference> Retired, int Handles) RetireWindows()
    {
        var source = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Normal };
        Isolate(source);
        source.Show(); Pump();
        var document = source.Documents[0];
        var editor = document.Editor;
        var retired = new List<WeakReference>();
        using var process = Process.GetCurrentProcess();
        var initialHandles = process.HandleCount;
        for (var round = 0; round < 50; round++)
        {
            var destination = (MainWindow?)Call(source, "DetachDocumentWindow", document, new System.Windows.Point(1100, 120));
            Check(destination is not null, "Detached window was not created");
            Isolate(destination!);
            retired.Add(new WeakReference(source));
            source.Close(); Pump();
            source = destination!;
            Check(source.Documents.Contains(document) && ReferenceEquals(editor, document.Editor), "Document ownership lost after closing source");
            var logicalOwner = editor.Parent is DependencyObject parent ? Window.GetWindow(parent) : null;
            Check(editor.Parent is null || ReferenceEquals(logicalOwner, source), "Editor logical parent belongs to a retired window");
        }
        return (source, retired, initialHandles);
    }

    private static void VerifyWindowLifetime()
    {
        var releasedEditors = Enumerable.Range(0, 10).Select(_ => CreateReleasedEditor()).ToArray();
        using var process = Process.GetCurrentProcess();
        // End the loop's stack frame before collecting, so its locals cannot retain retired windows.
        var (current, retired, initialHandles) = RetireWindows();
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(25); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Pump();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
        var retained = retired.Count(reference => reference.IsAlive);
        Console.WriteLine($"window-lifetime retired=50 retained={retained} handleDelta={process.HandleCount - initialHandles}");
        GC.KeepAlive(current);
        Check(releasedEditors.All(reference => !reference.IsAlive), "Released editor hosts are still retained");
        Check(retained <= 1, "Closed source windows are still retained");
        Check(process.HandleCount <= initialHandles + 10, "Native handles accumulated while retiring windows");
        Console.WriteLine("PASS 50 new-window detach/normal source close cycles and retired-window collection");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedEditor()
    {
        var editor = new MyTextEditor.Controls.ScintillaEditorHost();
        editor.ReleaseResources();
        editor.ReleaseResources();
        return new WeakReference(editor);
    }
}
