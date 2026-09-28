using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Controls;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

internal static class LargeWorkspaceVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Marker = "WORKSPACE_MATCH_가나다😀";

    public static int RunNativeMemory(int megabytes, bool styleStorage)
    {
        if (megabytes is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(megabytes));
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-native-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fixture.log");
            CreateFixture(path, megabytes * 1_000_000L);
            var buffer = new DocumentFileService().LoadBufferAsync(path).GetAwaiter().GetResult();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var editor = new ScintillaEditorHost();
            if (styleStorage)
            {
                var native = Field<object>(editor, "_editor");
                var direct = native.GetType().GetMethod("DirectMessage", [typeof(int), typeof(IntPtr), typeof(IntPtr)])!;
                var document = (IntPtr)direct.Invoke(native, [2375, IntPtr.Zero, IntPtr.Zero])!;
                Require(document != IntPtr.Zero, "Default-styles document allocation failed");
                direct.Invoke(native, [2358, IntPtr.Zero, document]);
                direct.Invoke(native, [2377, IntPtr.Zero, document]);
            }
            var window = new Window { Content = editor, Width = 800, Height = 600, ShowInTaskbar = false };
            try
            {
                window.Show();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                ReportMemory("native-before");
                editor.LoadUtf8(buffer.Utf8Buffer);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Console.WriteLine($"native-memory bytes={buffer.Utf8Buffer.Length} default-style-storage={styleStorage}");
                ReportMemory("native-loaded");
                GC.KeepAlive(buffer);
            }
            finally { editor.ReleaseResources(); window.Close(); app.Shutdown(); }
        }
        finally
        {
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
        return 0;
    }

    public static int Run(int count = 4, int megabytes = 8)
    {
        if (count is < 2 or > 100 || megabytes is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(count), "Use 2–100 documents and 1–1000 decimal MB each.");
        long bytes = megabytes * 1_000_000L;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception();
        // Native text, line structures, one UTF-16 worker snapshot/result and native Undo reserve.
        ulong reserve = (ulong)(count * bytes * 1.4 + bytes * 14 + 1_000_000_000L);
        Console.WriteLine($"workspace docs={count} bytes-per-file={bytes} total={count * bytes} available-physical={memory.AvailablePhysical} estimated-reserve={reserve}");
        if (memory.AvailablePhysical < reserve)
            throw new InvalidOperationException("Insufficient available physical RAM for this benchmark; close other applications or use smaller arguments.");

        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(0, count).Select(i => Path.Combine(directory, $"fixture-{i:D2}.log")).ToArray();
        var exitCode = 0;
        try
        {
            CreateFixture(paths[0], bytes);
            for (var index = 1; index < paths.Length; index++)
                if (!CreateHardLink(paths[index], paths[0], IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Benchmark hardlink creation failed");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
            var window = new MainWindow { ShowInTaskbar = false };
            Set(window, "_settingsReady", false);
            Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
            Call(window, "StopFileSync");
            window.Loaded += async (_, _) =>
            {
                try
                {
                    using (var pulse = new UiPulse("load"))
                        await window.OpenStartupFilesAsync(paths);
                    Require(window.Documents.Count == count, "Multiple-file load lost documents");
                    foreach (var document in window.Documents)
                    {
                        Require(!string.IsNullOrEmpty(document.TabTitle), "Missing document tab title");
                        var native = Field<object>(document.Editor, "_editor");
                        var direct = native.GetType().GetMethod("DirectMessage", [typeof(int), typeof(IntPtr), typeof(IntPtr)])!;
                        var documentOptions = (IntPtr)direct.Invoke(native, [2379, IntPtr.Zero, IntPtr.Zero])!;
                        Require((documentOptions.ToInt64() & 1) == 1, "Plain document still allocates per-byte style data");
                    }
                    ReportMemory("loaded");
                    Field<CheckBox>(window, "FolderSearchCheck").IsChecked = false;
                    Field<CheckBox>(window, "UseConditionSearchCheck").IsChecked = false;
                    Field<TextBox>(window, "LiteralFindBox").Text = Marker;
                    var sessions = window.SearchSessions.Count;
                    using (var pulse = new UiPulse("search-all-open"))
                    {
                        Call(window, "SearchOpenDocuments_Click", window, new RoutedEventArgs());
                        await WaitUntilAsync(() => window.SearchSessions.Count > sessions);
                    }
                    var result = window.SearchSessions.Last();
                    Require(result.Rows.Count == count && result.Rows.All(row => row.Text == Marker), "Search lost or duplicated match text");
                    Require(result.Rows.Select(row => row.Source!.Document).Distinct().Count() == count &&
                        result.Rows.All(row => row.Source!.Snapshot is null), "Search retained full multi-file snapshots or lost source identities");
                    ReportMemory("searched");
                    var target = window.Documents.Last();
                    Call(window, "SelectDocument", target);
                    Func<string, TextTransformResult> transform = text => new TextTransformService().Replace(text, Marker, "WORKSPACE_REPLACED", false, target.NewLine);
                    using (var pulse = new UiPulse("replace-one-document"))
                        await (Task)Call(window, "RunTransformAsync", transform, "workspace benchmark", false)!;
                    Require(target.IsModified, "Transform was not applied");
                    Require(target.Editor.FindOccurrence("WORKSPACE_REPLACED", true, false, false, out _), "Transformed text missing");
                    target.Editor.Undo();
                    Require(target.Editor.FindOccurrence(Marker, true, false, false, out _), "Single Undo did not restore content");
                    Require(result.Rows.All(row => row.Text == Marker), "Result snapshot changed with source edits");
                    ReportMemory("undo");
                    Console.WriteLine("PASS multi-file load, styleless buffers, complete matching snapshots, async search/transform, Undo");
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); exitCode = 1; }
                finally
                {
                    Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
                    window.Hide();
                    foreach (var document in window.Documents) document.Editor.ReleaseResources();
                    Set(window, "_allowClose", true);
                    window.Close();
                    app.Shutdown();
                }
            };
            window.Show();
            app.Run();
        }
        finally
        {
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
        return exitCode;
    }

    private static void CreateFixture(string path, long bytes)
    {
        var tail = Encoding.UTF8.GetBytes("\n" + Marker);
        var block = Encoding.UTF8.GetBytes(new string('x', 255) + "\n");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
        var bodyBytes = bytes - tail.Length;
        while (stream.Position < bodyBytes)
            stream.Write(block, 0, (int)Math.Min(block.Length, bodyBytes - stream.Position));
        stream.Write(tail);
    }

    private sealed class UiPulse : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(20), };
        private double _last, _max;
        private int _ticks;
        public UiPulse(string name)
        {
            _name = name;
            _timer.Tick += (_, _) => { var now = _watch.Elapsed.TotalMilliseconds; _max = Math.Max(_max, now - _last); _last = now; _ticks++; };
            _timer.Start();
        }
        public void Dispose()
        {
            _timer.Stop();
            _max = Math.Max(_max, _watch.Elapsed.TotalMilliseconds - _last);
            Console.WriteLine($"{_name} seconds={_watch.Elapsed.TotalSeconds:F3} ui-heartbeats={_ticks} ui-max-gap-ms={_max:F1}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromMinutes(15)) throw new TimeoutException("Workspace operation exceeded 15 minutes");
            await Task.Delay(20);
        }
    }
    private static void ReportMemory(string phase)
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"{phase} working-MiB={process.WorkingSet64 / 1048576d:F1} private-MiB={process.PrivateMemorySize64 / 1048576d:F1} peak-MiB={process.PeakWorkingSet64 / 1048576d:F1}");
    }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
