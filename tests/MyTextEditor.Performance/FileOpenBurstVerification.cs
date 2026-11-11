using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Models;
using MyTextEditor.Services;
using Application = System.Windows.Application;

internal static class FileOpenBurstVerification
{
    private sealed class VerificationApplication : MyTextEditor.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PumpUntil(Func<bool> complete, string operation, int timeoutMilliseconds = 15000)
    {
        var watch = Stopwatch.StartNew();
        while (!complete())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException(operation);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void Wait(Task operation, string description)
    {
        PumpUntil(() => operation.IsCompleted, description);
        operation.GetAwaiter().GetResult();
    }

    public static int Run()
    {
        var app = new VerificationApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-file-open-burst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var scope = "verification-" + Guid.NewGuid().ToString("N");
        using var receiver = new FileOpenCoordinator(app.Dispatcher, scope);
        using var sender = new FileOpenCoordinator(app.Dispatcher, scope);
        typeof(MyTextEditor.App).GetField("_fileOpenCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, receiver);
        var received = new List<string>();
        var errors = new List<Exception>();
        var allow = true;
        var attempts = 0;
        string PathFor(int number) => Path.Combine(directory, $"file-{number:D3}" + (number % 2 == 0 ? ".csv" : ".cfg"));
        try
        {
            Check(receiver.TryBecomeReceiver() && !sender.TryBecomeReceiver(), "Isolated receiver election failed");

            var coldPaths = new[] { PathFor(0), PathFor(1) };
            var cold = Task.Run(async () =>
            {
                var watch = Stopwatch.StartNew();
                await sender.ForwardAsync(coldPaths);
                return watch.Elapsed.TotalMilliseconds;
            });
            // No dispatcher pumping and no Start callback for longer than the old three-second deadline.
            Thread.Sleep(5000);
            Check(cold.IsCompleted, "Cold-start forwarding waited for UI initialization");
            var coldMilliseconds = cold.GetAwaiter().GetResult();
            Check(coldMilliseconds < 3000, $"Cold-start ACK exceeded three seconds: {coldMilliseconds:F0}ms");
            Check(receiver.PendingCount == 1, "A pre-Start ACK did not retain the complete request");
            Check(!receiver.TryPauseIfIdle(), "A receiver with acknowledged pending work was allowed to pause");
            receiver.Start(paths =>
            {
                attempts++;
                if (!allow) return false;
                received.AddRange(paths);
                return true;
            }, errors.Add);
            PumpUntil(() => receiver.PendingCount == 0 && received.Count == coldPaths.Length, "Cold-start request did not drain");
            Check(received.SequenceEqual(coldPaths), "Cold-start batch paths changed order or were duplicated");
            Console.WriteLine($"PASS pre-Start receiver ACK {coldMilliseconds:F1}ms during five seconds without UI initialization; pending retained and drained in order");

            var burstPaths = Enumerable.Range(2, 30).Select(PathFor).ToArray();
            var countBeforeBurst = received.Count;
            var burst = Task.Run(async () =>
            {
                var total = Stopwatch.StartNew();
                var maximum = 0d;
                foreach (var path in burstPaths)
                {
                    var delivery = Stopwatch.StartNew();
                    await sender.ForwardAsync([path]);
                    maximum = Math.Max(maximum, delivery.Elapsed.TotalMilliseconds);
                }
                return (TotalMilliseconds: total.Elapsed.TotalMilliseconds, MaximumMilliseconds: maximum);
            });
            Thread.Sleep(5000);
            Check(burst.IsCompleted, "Burst forwarding stalled behind a busy UI thread");
            var timing = burst.GetAwaiter().GetResult();
            Check(timing.TotalMilliseconds < 3000 && timing.MaximumMilliseconds < 3000,
                $"Thirty transport ACKs exceeded three seconds: total={timing.TotalMilliseconds:F0}ms, max={timing.MaximumMilliseconds:F0}ms");
            Check(received.Count == countBeforeBurst && receiver.PendingCount == burstPaths.Length,
                "Busy-UI requests were not independently acknowledged and retained");
            PumpUntil(() => receiver.PendingCount == 0 && received.Count == coldPaths.Length + burstPaths.Length, "Busy-UI burst did not drain");
            Check(received.SequenceEqual(coldPaths.Concat(burstPaths)), "Queued requests were lost, duplicated, or reordered");
            Console.WriteLine($"PASS thirty CSV/CFG requests ACKed during five seconds of blocked UI: total={timing.TotalMilliseconds:F1}ms, max={timing.MaximumMilliseconds:F1}ms; exact FIFO drain");

            allow = false;
            attempts = 0;
            var retainedPath = PathFor(32);
            Wait(Task.Run(() => sender.ForwardAsync([retainedPath])), "Temporarily unavailable UI request ACK");
            PumpUntil(() => attempts > 0, "Unavailable UI callback was not attempted");
            Check(receiver.PendingCount == 1 && !received.Contains(retainedPath), "An acknowledged request was lost when the UI declined temporarily");
            Check(!receiver.TryPauseIfIdle(), "Pending declined work was treated as idle");
            allow = true;
            receiver.ResumeRequests();
            PumpUntil(() => receiver.PendingCount == 0 && received.Contains(retainedPath), "Retained request did not retry after UI resumed");
            Check(received.Count(path => path == retainedPath) == 1, "Retried request was dispatched more than once");

            Check(receiver.TryPauseIfIdle(), "Idle receiver could not pause before shutdown");
            var refused = Task.Run(() => sender.ForwardAsync([PathFor(33)]));
            PumpUntil(() => refused.IsCompleted, "Paused receiver did not promptly reject a request");
            try
            {
                refused.GetAwaiter().GetResult();
                throw new InvalidOperationException("Paused receiver acknowledged a request it would not keep");
            }
            catch (IOException) { }
            Check(receiver.PendingCount == 0 && !received.Contains(PathFor(33)), "Rejected shutdown request changed the queue");
            receiver.ResumeRequests();
            Wait(Task.Run(() => sender.ForwardAsync([PathFor(33)])), "Forwarding after shutdown canceled");
            PumpUntil(() => receiver.PendingCount == 0 && received.Contains(PathFor(33)), "Canceled shutdown did not resume delivery");
            Check(received.Count == 34 && received.Distinct().Count() == 34, "Final queue contents contained omissions or duplicates");
            Check(errors.Count == 0, "Healthy queue processing reported errors: " + string.Join("; ", errors.Select(error => error.Message)));
            Console.WriteLine("PASS acknowledged-work retention, retry once available, pending-close guard, explicit idle-close rejection, and cancel/resume");
            VerifyLateLaunchOrder(app, directory);
            VerifyWindowCloseGates(receiver, sender, received, directory);
            return 0;
        }
        finally
        {
            app.Shutdown();
            Directory.Delete(directory);
        }
    }

    private static void VerifyWindowCloseGates(FileOpenCoordinator receiver, FileOpenCoordinator sender,
        List<string> received, string directory)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        MainWindow MakeWindow()
        {
            var window = new MainWindow { ShowInTaskbar = false };
            typeof(MainWindow).GetField("_settingsReady", hidden)!.SetValue(window, false);
            ((DispatcherTimer)typeof(MainWindow).GetField("_settingsSaveTimer", hidden)!.GetValue(window)!).Stop();
            typeof(MainWindow).GetMethod("StopFileSync", hidden)!.Invoke(window, null);
            window.Show();
            return window;
        }
        bool Closed(MainWindow window) => (bool)typeof(MainWindow).GetField("_windowClosed", hidden)!.GetValue(window)!;
        void ForceClose(MainWindow window)
        {
            if (Closed(window)) return;
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", hidden)!.SetValue(window, true);
            window.Close();
        }
        var first = MakeWindow();
        var remaining = MakeWindow();
        var saving = (HashSet<DocumentViewModel>)typeof(MainWindow).GetField("_savingDocuments", hidden)!.GetValue(remaining)!;
        try
        {
            saving.Add(remaining.Documents[0]);
            Check(receiver.TryPauseIfIdle(), "Close-gate test did not begin with an idle queue");
            first.Close();
            PumpUntil(() => Closed(first), "First clean window did not close while the remaining window was busy");
            var path = Path.Combine(directory, "after-busy-window-close.cfg");
            Wait(Task.Run(() => sender.ForwardAsync([path])), "Closing another window left the temporarily busy receiver paused");
            PumpUntil(() => receiver.PendingCount == 0 && received.Contains(path), "Remaining-window request did not drain");
            Check(!Closed(remaining), "Closing first window also closed the temporarily busy remaining window");

            saving.Clear();
            remaining.Close();
            PumpUntil(() => Closed(remaining), "Last clean window did not close normally");
            var rejected = Task.Run(() => sender.ForwardAsync([Path.Combine(directory, "after-last-window-close.csv")]));
            PumpUntil(() => rejected.IsCompleted, "Last-window shutdown did not reject a new request");
            try
            {
                rejected.GetAwaiter().GetResult();
                throw new InvalidOperationException("Closed last window reopened file reception before application exit");
            }
            catch (IOException) { }
            Check(receiver.PendingCount == 0, "Last-window shutdown acknowledged work with no remaining receiver window");
            Console.WriteLine("PASS first-window close resumes reception with a temporarily busy remaining window; final-window close keeps reception paused");
        }
        finally
        {
            saving.Clear();
            ForceClose(first);
            ForceClose(remaining);
        }
    }

    private static void VerifyLateLaunchOrder(Application app, string directory)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative)
        });
        var paths = Enumerable.Range(0, 7)
            .Select(index => Path.Combine(directory, $"delayed-{index:D2}" + (index % 2 == 0 ? ".csv" : ".cfg"))).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "key,value\n한글,😀\n", new UTF8Encoding(false));
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), hidden, null, [false], null)!;
        typeof(MainWindow).GetField("_settingsReady", hidden)!.SetValue(window, false);
        ((DispatcherTimer)typeof(MainWindow).GetField("_settingsSaveTimer", hidden)!.GetValue(window)!).Stop();
        typeof(MainWindow).GetMethod("StopFileSync", hidden)!.Invoke(window, null);
        var started = DateTime.UtcNow.AddMinutes(-1).Ticks;
        void Open(long launch, params string[] files)
        {
            // The dispatcher begins the request before returning its in-flight task.
            var request = new FileOpenRequest(files, started + launch, (int)launch);
            var opening = app.Dispatcher.Invoke(
                () => (Task)typeof(MainWindow).GetMethod("OpenStartupRequestAsync", hidden)!.Invoke(window, [request])!,
                DispatcherPriority.Normal);
            Wait(opening, "Delayed launch request did not finish opening");
        }
        void Expect(params string[] expected) => Check(
            window.LeftDocuments.Select(document => document.FilePath).SequenceEqual(expected),
            "Late earlier launch did not insert tabs in creation order: " +
            string.Join(", ", window.LeftDocuments.Select(document => Path.GetFileName(document.FilePath))));
        try
        {
            window.Show();
            // The process winning receiver election need not be the earliest Explorer launch.
            Open(20, paths[2]);
            Expect(paths[2]);
            Open(10, paths[0], paths[1]);
            Expect(paths[0], paths[1], paths[2]);
            Open(40, paths[4]);
            Open(30, paths[3]);
            Expect(paths.Take(5).ToArray());
            Open(50, paths[2]);
            Check(window.Documents.Count == 5, "Duplicate delayed request created a second document");
            Expect(paths.Take(5).ToArray());
            Open(60, paths[5], paths[6]);
            Expect(paths);
            Console.WriteLine("PASS late earlier launch inserts before the receiver's startup tab; out-of-order requests and within-request batch order are preserved; duplicate reuses its tab");
        }
        finally
        {
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", hidden)!.SetValue(window, true);
            window.Close();
            foreach (var path in paths) File.Delete(path);
        }
    }

    public static int RunProcesses(string executable)
    {
        executable = Path.GetFullPath(executable);
        Check(File.Exists(executable), "The actual OmniEdit executable is required");
        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try { Check(existing.Length == 0, "Close existing OmniEdit windows before the isolated process test; none were changed"); }
        finally { foreach (var process in existing) process.Dispose(); }
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-file-open-processes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new FileOpenTestSettingsSnapshot(directory, executable);
        var paths = Enumerable.Range(0, 40)
            .Select(index => Path.Combine(directory, $"burst-{index:D3}" + (index % 2 == 0 ? ".csv" : ".cfg"))).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "key,value\r\n한글,😀\r\n", new UTF8Encoding(false));
        var processes = new List<Process>();
        Process Start(string path)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
            start.ArgumentList.Add(path);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to launch test process");
            processes.Add(process);
            return process;
        }
        void NoErrorDialogs()
        {
            var ids = processes.Where(process => !process.HasExited).Select(process => (uint)process.Id).ToHashSet();
            var failures = new List<string>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (ids.Contains(pid) && IsWindowVisible(window))
                {
                    var title = WindowText(window);
                    var windowClass = new StringBuilder(128);
                    GetClassName(window, windowClass, windowClass.Capacity);
                    if (windowClass.ToString() == "#32770") failures.Add(pid + ": " + title);
                }
                return true;
            }, IntPtr.Zero);
            Check(failures.Count == 0, "Unexpected native dialog: " + string.Join("; ", failures));
        }
        void Until(Func<bool> condition, string operation)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                NoErrorDialogs();
                if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(operation);
                Thread.Sleep(50);
            }
            NoErrorDialogs();
        }
        try
        {
            var coldWatch = Stopwatch.StartNew();
            foreach (var path in paths.Take(20)) { Start(path); Thread.Sleep(50); }
            Process? owner = null;
            Until(() =>
            {
                var owners = processes.Where(process => !process.HasExited && MainWindow(process) != IntPtr.Zero).ToArray();
                if (owners.Length == 1 && TabNames(owners[0], paths).Count == 20) { owner = owners[0]; return true; }
                return false;
            }, "Cold burst did not produce one window containing all twenty files");
            var receiver = owner!;
            Until(() => processes.Where(process => process != receiver).All(process => process.HasExited), "Cold forwarding processes did not exit");
            Check(processes.Where(process => process != receiver).All(process => process.ExitCode == 0), "Cold forwarding process failed");
            Check(TabNames(receiver, paths).SequenceEqual(paths.Take(20).Select(Path.GetFileName)), "Cold burst tabs did not follow process launch order");
            Console.WriteLine($"PASS actual EXE cold burst: twenty CSV/CFG launches at 50ms intervals; one window, exact tab order, no loss/duplicates/dialogs; all tabs visible in {coldWatch.Elapsed.TotalMilliseconds:F0}ms");
            ReportProcessTiming("cold forwarding", processes.Where(process => process != receiver));

            var warmWatch = Stopwatch.StartNew();
            var warm = new List<Process>();
            foreach (var path in paths.Skip(20)) { warm.Add(Start(path)); Thread.Sleep(50); }
            Until(() => warm.All(process => process.HasExited), "Warm forwarding processes did not exit");
            Check(warm.All(process => process.ExitCode == 0), "Warm forwarding process failed");
            Until(() => TabNames(receiver, paths).Count == paths.Length, "Warm burst tabs were omitted");
            Check(TabNames(receiver, paths).SequenceEqual(paths.Select(Path.GetFileName)), "Warm burst reordered or duplicated document tabs");
            Console.WriteLine($"PASS actual EXE warm burst: twenty additional CSV/CFG launches at 50ms intervals; forty exact ordered tabs; burst complete in {warmWatch.Elapsed.TotalMilliseconds:F0}ms");
            ReportProcessTiming("warm forwarding", warm);

            var duplicate = Start(paths[7]);
            Until(() => duplicate.HasExited, "Duplicate forwarding process did not exit");
            Check(duplicate.ExitCode == 0, "Duplicate file forwarding failed");
            Thread.Sleep(300);
            Check(TabNames(receiver, paths).SequenceEqual(paths.Select(Path.GetFileName)), "Duplicate file open changed tab count or order");
            Check(receiver.CloseMainWindow(), "Could not close the test receiver normally");
            Until(() => receiver.HasExited, "Test receiver did not shut down normally");
            Check(receiver.ExitCode == 0, "Test receiver normal shutdown failed");
            Console.WriteLine("PASS duplicate external open reuses its tab; test receiver shuts down normally");
            return 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                process.Dispose();
            }
            foreach (var path in paths) File.Delete(path);
            settings.RestoreAfterProcessesExit();
            Directory.Delete(directory);
        }
    }

    private static void ReportProcessTiming(string stage, IEnumerable<Process> processes)
    {
        var durations = processes.Select(process => (process.ExitTime.ToUniversalTime() - process.StartTime.ToUniversalTime()).TotalMilliseconds)
            .Order().ToArray();
        if (durations.Length == 0) return;
        var middle = durations.Length / 2;
        var median = durations.Length % 2 == 0 ? (durations[middle - 1] + durations[middle]) / 2 : durations[middle];
        Console.WriteLine($"{stage} process lifetime: count={durations.Length}, median={median:F1}ms, max={durations[^1]:F1}ms (OS start to exit; excludes receiver/UI observation)");
    }

    private static IntPtr MainWindow(Process process)
    {
        process.Refresh();
        return process.HasExited ? IntPtr.Zero : process.MainWindowHandle;
    }

    private static List<string> TabNames(Process process, IEnumerable<string> paths)
    {
        var handle = MainWindow(process);
        if (handle == IntPtr.Zero) return [];
        var names = paths.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var window = AutomationElement.FromHandle(handle);
        var tabs = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "DocumentTabs"));
        if (tabs is null) return [];
        var items = tabs.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        var result = new List<string>();
        foreach (AutomationElement item in items)
        {
            var labels = item.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var name = labels.Cast<AutomationElement>().Select(label => label.Current.Name).FirstOrDefault(names.Contains);
            if (name is not null) result.Add(name);
        }
        return result;
    }

    private static string WindowText(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
}

// Real EXE runs use the normal settings path. Preserve it without restoring over a new user session.
internal sealed class FileOpenTestSettingsSnapshot
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTextEditor", "settings.json");
    private readonly string _backupPath;
    private readonly string _processName;
    private readonly bool _existed;

    public FileOpenTestSettingsSnapshot(string directory, string executable)
    {
        _processName = Path.GetFileNameWithoutExtension(executable);
        _existed = File.Exists(_settingsPath);
        _backupPath = Path.Combine(directory, _existed ? "settings.original.json" : "settings.original.absent");
        if (_existed) File.WriteAllBytes(_backupPath, File.ReadAllBytes(_settingsPath));
        else File.WriteAllText(_backupPath, "The settings file did not exist before this test.");
    }

    public void RestoreAfterProcessesExit()
    {
        var running = Process.GetProcessesByName(_processName);
        try
        {
            if (running.Any(process => !process.HasExited))
                throw new IOException("Settings were not restored because an OmniEdit process is still running. Original settings are preserved at " + _backupPath);
        }
        finally { foreach (var process in running) process.Dispose(); }
        if (_existed)
        {
            var temporaryPath = _settingsPath + ".verification-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, RemoveOldTestRecentFiles(File.ReadAllBytes(_backupPath)));
                File.Move(temporaryPath, _settingsPath, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        else if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
        File.Delete(_backupPath);
    }

    private static byte[] RemoveOldTestRecentFiles(byte[] original)
    {
        JsonObject? settings;
        try { settings = JsonNode.Parse(Encoding.UTF8.GetString(original).TrimStart('\uFEFF')) as JsonObject; }
        catch (JsonException) { return original; } // Preserve an originally invalid settings file byte-for-byte.
        if (settings?["RecentFiles"] is not JsonArray recent) return original;
        var removed = 0;
        for (var index = recent.Count - 1; index >= 0; index--)
        {
            if (recent[index] is JsonValue value && value.TryGetValue<string>(out var path) && IsDeletedTestFixture(path))
            {
                recent.RemoveAt(index);
                removed++;
            }
        }
        if (removed == 0) return original;
        Console.WriteLine($"Removed {removed} stale verification fixture entries from RecentFiles; all other original settings restored");
        return Encoding.UTF8.GetBytes(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsDeletedTestFixture(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return false;
            var directory = Path.GetDirectoryName(full);
            if (directory is null || !string.Equals(Path.GetDirectoryName(directory),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)) return false;
            var folder = Path.GetFileName(directory);
            const string burstPrefix = "OmniEdit-file-open-processes-";
            const string instancePrefix = "OmniEdit-instance199-";
            var name = Path.GetFileName(full);
            if (folder.StartsWith(burstPrefix, StringComparison.Ordinal) && Guid.TryParseExact(folder[burstPrefix.Length..], "N", out _))
                return name.Length == 13 && name.StartsWith("burst-", StringComparison.Ordinal) &&
                    int.TryParse(name.AsSpan(6, 3), out var number) && number is >= 0 and < 40 &&
                    name.EndsWith(number % 2 == 0 ? ".csv" : ".cfg", StringComparison.Ordinal);
            if (folder.StartsWith(instancePrefix, StringComparison.Ordinal) && Guid.TryParseExact(folder[instancePrefix.Length..], "N", out _))
                return new[] { "첫 번째 문서.txt", "second file.txt", "수신 인계.txt", "오류 뒤 정상.txt", "종료 취소 뒤.txt", "활성 창.txt", "동시 A.txt", "동시 B.txt" }
                    .Contains(name, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        return false;
    }
}
