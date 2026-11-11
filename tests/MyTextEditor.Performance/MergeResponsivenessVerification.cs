using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Help;
using MyTextEditor.Models;
using ScintillaNET;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

internal static class MergeResponsivenessVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Samples = 120;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Hidden)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Hidden)!.Invoke(value, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static double Milliseconds(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    private static double P95(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed record Result(string Fixture, string Input, string Source, int Bytes, int Blocks, int Samples,
        int ChangedInputs, int NativeInputObservations, int MissingPositionNotifications,
        int MismatchesAtNativeReturn, int MismatchesAtSendReturn, int MismatchesAfterPaint,
        double InputP95Ms, double NativeHandlerP95Ms, double NotifyLagP95Ms,
        double NativePaintP95Ms, double GutterP95Ms, double DispatcherDrainP95Ms, double DisplayP95Ms);

    public static int Run(bool baseline = false, bool large = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var results = new List<Result>();
        try
        {
            VerifyDense(results);
            if (large)
                foreach (var megabytes in new[] { 30, 100, 300 })
                {
                    VerifyLarge(megabytes, results);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                }
            var failures = results.Where(result => result.ChangedInputs < 100 || result.NativeInputObservations < Samples ||
                result.MissingPositionNotifications != 0 || result.MismatchesAtSendReturn != 0 ||
                result.MismatchesAtNativeReturn != 0 || result.MismatchesAfterPaint != 0 || result.InputP95Ms > 16).ToArray();
            Console.WriteLine($"{(baseline ? "BASELINE" : "VERIFY")} scroll responsiveness: groups={results.Count}, gateFailures={failures.Length}, input samples={results.Sum(result => result.Samples)}");
            return baseline || failures.Length == 0 ? 0 : 1;
        }
        finally
        {
            var directory = Path.GetFullPath("artifacts/merge-responsiveness");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, (baseline ? "baseline" : "after") + (large ? "-large" : "") + ".json"),
                JsonSerializer.Serialize(new
                {
                    generatedUtc = DateTime.UtcNow, baseline, large,
                    productAssembly = typeof(DiffTabView).Assembly.Location,
                    method = "Win32 wheel/PageDown/PageUp/custom-thumb messages to real HWNDs, observed immediately after native WndProc and SendMessage return. Genuine TextDiffEngine range results. Native Refresh and direct RenderMergeGutter timings are separate observations, not the 16ms input gate. No physical keyboard/mouse input claimed.",
                    results
                }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
    }

    private static void VerifyDense(List<Result> results)
    {
        var left = new StringBuilder();
        var right = new StringBuilder();
        for (var line = 0; line < 12000; line++)
        {
            var text = $"{line:D8} common payload " + new string('x', 72);
            left.Append(text).Append('\n');
            if (line % 19 == 0) right.Append($"inserted {line:D8}").Append('\n');
            right.Append(line % 3 == 0 ? text.Replace("payload", "changed", StringComparison.Ordinal) : text).Append('\n');
        }
        var a = left.ToString(); var b = right.ToString();
        var engine = new TextDiffEngine();
        var watch = Stopwatch.StartNew();
        var result = engine.CompareRanges(a, b);
        Check(result.Blocks.Count > 1000, "Dense fixture did not generate genuine independent difference blocks");
        Console.WriteLine($"Fixture dense: blocks={result.Blocks.Count}, anchors={result.ScrollAnchors.Count}, compare={watch.ElapsedMilliseconds}ms");
        VerifyFixture("dense", a, b, Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b), result, results);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyLarge(int megabytes, List<Result> results)
    {
        const int lineBytes = 1000;
        var bytes = new byte[megabytes * 1_000_000];
        Array.Fill(bytes, (byte)'x');
        for (var offset = 0; offset < bytes.Length; offset += lineBytes)
        {
            Utf8Formatter.TryFormat(offset / lineBytes, bytes.AsSpan(offset, 8), out _, new StandardFormat('D', 8));
            bytes[offset + lineBytes - 1] = (byte)'\n';
        }
        var text = Encoding.UTF8.GetString(bytes);
        var engine = new TextDiffEngine();
        var watch = Stopwatch.StartNew();
        var snapshot = engine.CreateSnapshot(text);
        var result = engine.CompareRanges(snapshot, snapshot);
        Console.WriteLine($"Fixture {megabytes}MB: identical scalar buffer, genuine compare, anchors={result.ScrollAnchors.Count}, compare={watch.ElapsedMilliseconds}ms");
        VerifyFixture(megabytes + "MB", text, text, bytes, bytes, result, results);
    }

    private static void VerifyFixture(string name, string a, string b, byte[] leftBytes, byte[] rightBytes,
        TextDiffResult result, List<Result> results)
    {
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
        };
        var view = new DiffTabView(new() { Kind = DiffEndpointKind.Scratch, DisplayName = "left", Text = "", NewLine = "\n" },
            new() { Kind = DiffEndpointKind.Scratch, DisplayName = "right", Text = "", NewLine = "\n" },
            new(), callbacks, new("Consolas", 11, false));
        var window = new Window { Content = view, Width = 1200, Height = 760, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        try
        {
            view.SetActive(false); window.Show(); window.UpdateLayout(); Pump();
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            typeof(DiffTabView).GetField("_updatingEditors", Hidden)!.SetValue(view, true);
            try
            {
                // Large cases reuse one UTF-8 array for both native loads; no second encoding allocation.
                left.LoadUtf8(leftBytes); right.LoadUtf8(rightBytes);
                Field<DiffEndpoint>(view, "_left").Text = a;
                Field<DiffEndpoint>(view, "_right").Text = b;
            }
            finally { typeof(DiffTabView).GetField("_updatingEditors", Hidden)!.SetValue(view, false); }
            Check(left.Utf8ByteLength == leftBytes.Length && right.Utf8ByteLength == rightBytes.Length,
                "Native benchmark buffer size differs from the compared fixture");
            typeof(DiffTabView).GetField("_result", Hidden)!.SetValue(view, result);
            typeof(DiffTabView).GetField("_needsComparison", Hidden)!.SetValue(view, false);
            view.SetActive(true);
            Call(view, "RefreshResultState");
            window.UpdateLayout(); Pump();
            foreach (var sourceIsLeft in new[] { true, false })
                foreach (var input in new[] { "wheel", "page-key", "thumb" })
                    results.Add(Measure(view, window, sourceIsLeft ? left : right, sourceIsLeft ? right : left,
                        sourceIsLeft, input, name, Math.Max(leftBytes.Length, rightBytes.Length), result.Blocks.Count));
        }
        finally { view.ReleaseResources(); window.Close(); }
    }

    private static Result Measure(DiffTabView view, Window window, ScintillaEditorHost source,
        ScintillaEditorHost target, bool sourceIsLeft, string input, string fixture, int bytes, int blocks)
    {
        var native = Field<Scintilla>(source, "_editor");
        var targetNative = Field<Scintilla>(target, "_editor");
        var bar = Field<Forms.Control>(source, "_verticalBar");
        var inputTimes = new List<double>(); var handlerTimes = new List<double>(); var notifyTimes = new List<double>();
        var paintTimes = new List<double>(); var gutterTimes = new List<double>(); var drainTimes = new List<double>(); var displayTimes = new List<double>();
        var nativeMismatches = 0; var sendMismatches = 0; var paintMismatches = 0; var changed = 0;
        var missingNotifications = 0;
        long started = 0; double? firstNotify = null;
        int Expected() => Math.Clamp((int)Call(view, "MapScrollLine", source.FirstVisibleLine + 1, sourceIsLeft)! - 1,
            0, Math.Max(0, target.LineCount - target.LinesOnScreen));
        void Notified(object? sender, EventArgs args) { if (started != 0 && firstNotify is null) firstNotify = Milliseconds(started); }
        source.VerticalScrolled += Notified;
        using var probe = new InputProbe(input == "thumb" ? bar.Handle : native.Handle,
            message => input == "wheel" ? message == 0x020A : input == "page-key" ? message == 0x0100 : message == 0x0200,
            duration => { if (started == 0) return; handlerTimes.Add(duration); if (target.FirstVisibleLine != Expected()) nativeMismatches++; });
        source.GoToLine(source.LineCount / 2, focusEditor: false);
        source.SetFirstVisibleLine(source.LineCount / 2); native.Focus(); window.UpdateLayout(); Pump();
        var thumbStart = (int)bar.GetType().GetProperty("ThumbStart", Hidden)!.GetValue(bar)!;
        var thumbLength = (int)bar.GetType().GetProperty("ThumbLength", Hidden)!.GetValue(bar)!;
        var thumbY = thumbStart + thumbLength / 2;
        if (input == "thumb") SendMessage(bar.Handle, 0x0201, (IntPtr)1, PointParameter(7, thumbY));
        try
        {
            for (var index = -12; index < Samples; index++)
            {
                var before = source.FirstVisibleLine;
                firstNotify = null;
                started = Stopwatch.GetTimestamp();
                if (input == "wheel")
                {
                    var point = native.PointToScreen(new System.Drawing.Point(native.Width / 2, native.Height / 2));
                    var delta = index % 2 == 0 ? -120 : 120;
                    SendMessage(native.Handle, 0x020A, new IntPtr(unchecked((int)((uint)(ushort)(short)delta << 16))), PointParameter(point.X, point.Y));
                }
                else if (input == "page-key")
                {
                    var key = index % 2 == 0 ? 0x22 : 0x21;
                    SendMessage(native.Handle, 0x0100, (IntPtr)key, IntPtr.Zero);
                    SendMessage(native.Handle, 0x0101, (IntPtr)key, IntPtr.Zero);
                }
                else SendMessage(bar.Handle, 0x0200, (IntPtr)1, PointParameter(7, thumbY + (index % 2 == 0 ? 12 : 0)));
                var inputMs = Milliseconds(started);
                var mismatch = target.FirstVisibleLine != Expected();
                var moved = source.FirstVisibleLine != before;
                var paintStarted = Stopwatch.GetTimestamp(); native.Refresh(); targetNative.Refresh();
                var paintMs = Milliseconds(paintStarted);
                var gutterStarted = Stopwatch.GetTimestamp(); Call(view, "RenderMergeGutter", null, EventArgs.Empty);
                var gutterMs = Milliseconds(gutterStarted);
                var afterPaintMismatch = target.FirstVisibleLine != Expected();
                var drainStarted = Stopwatch.GetTimestamp(); Pump();
                var drainMs = Milliseconds(drainStarted);
                var totalMs = Milliseconds(started);
                var notifyMs = firstNotify ?? double.NaN;
                started = 0;
                if (index >= 0)
                {
                    if (moved) changed++;
                    if (mismatch) sendMismatches++;
                    if (afterPaintMismatch) paintMismatches++;
                    if (moved && double.IsNaN(notifyMs)) missingNotifications++;
                    inputTimes.Add(inputMs); paintTimes.Add(paintMs); gutterTimes.Add(gutterMs); drainTimes.Add(drainMs); displayTimes.Add(totalMs);
                    if (!double.IsNaN(notifyMs)) notifyTimes.Add(notifyMs);
                }
                else { handlerTimes.Clear(); nativeMismatches = 0; }
            }
        }
        finally
        {
            started = 0;
            if (input == "thumb") SendMessage(bar.Handle, 0x0202, IntPtr.Zero, PointParameter(7, thumbY));
            source.VerticalScrolled -= Notified;
        }
        var summary = new Result(fixture, input, sourceIsLeft ? "left" : "right", bytes, blocks, Samples, changed, handlerTimes.Count, missingNotifications,
            nativeMismatches, sendMismatches, paintMismatches, P95(inputTimes), P95(handlerTimes), P95(notifyTimes),
            P95(paintTimes), P95(gutterTimes), P95(drainTimes), P95(displayTimes));
        Console.WriteLine($"{fixture}/{summary.Source}/{input}: changed={changed}/{Samples}, nativeLag={nativeMismatches}, returnLag={sendMismatches}, afterPaintLag={paintMismatches}, input p95={summary.InputP95Ms:F3}ms, native={summary.NativeHandlerP95Ms:F3}ms, notify={summary.NotifyLagP95Ms:F3}ms, paint={summary.NativePaintP95Ms:F3}ms, gutter={summary.GutterP95Ms:F3}ms, display={summary.DisplayP95Ms:F3}ms");
        return summary;
    }

    private sealed class InputProbe : Forms.NativeWindow, IDisposable
    {
        private readonly Func<int, bool> _matches;
        private readonly Action<double> _returned;
        public InputProbe(IntPtr handle, Func<int, bool> matches, Action<double> returned)
        { _matches = matches; _returned = returned; AssignHandle(handle); }
        protected override void WndProc(ref Forms.Message message)
        {
            var measure = _matches(message.Msg);
            var started = measure ? Stopwatch.GetTimestamp() : 0;
            base.WndProc(ref message);
            if (measure) _returned(Milliseconds(started));
        }
        public void Dispose() => ReleaseHandle();
    }

    public static int WindowRun(bool baseline = false, bool skipUnavailablePhysical = true)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var observations = new List<(string Name, bool? Passed, string Evidence)>();
        void Observe(string name, bool passed, string evidence)
        {
            observations.Add((name, passed, evidence));
            Console.WriteLine($"{(passed ? "PASS" : baseline ? "BASELINE-FAIL" : "FAIL")} {name}: {evidence}");
        }
        void Skip(string name, string evidence)
        {
            if (observations.Any(item => item.Name == name)) return;
            observations.Add((name, null, evidence));
            Console.WriteLine($"SKIP {name}: {evidence}");
        }
        var main = new MainWindow();
        typeof(MainWindow).GetField("_settingsReady", Hidden)!.SetValue(main, false);
        Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
        Call(main, "StopFileSync");
        var settings = Field<UserSettings>(main, "_settings");
        settings.Diff.WindowLeft = 100; settings.Diff.WindowTop = 90;
        settings.Diff.WindowWidth = 1100; settings.Diff.WindowHeight = 700;
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.WindowState = WindowState.Normal;
        main.Left = 40; main.Top = 40; main.Width = 1120; main.Height = 720;
        DiffWorkspaceWindow? workspace = null;
        DiffTabView? view = null;
        try
        {
            main.Show(); Pump();
            workspace = (DiffWorkspaceWindow)Call(main, "EnsureDiffWorkspace")!;
            view = workspace.OpenComparison(new() { Kind = DiffEndpointKind.Scratch, DisplayName = "left", Text = "left\ncontent" },
                new() { Kind = DiffEndpointKind.Scratch, DisplayName = "right", Text = "right\ncontent" });
            Pump();
            var mainHandle = new WindowInteropHelper(main).Handle;
            var mergeHandle = new WindowInteropHelper(workspace).Handle;
            Observe("saved placement", Math.Abs(workspace.Left - 100) < 1 && Math.Abs(workspace.Top - 90) < 1 &&
                Math.Abs(workspace.Width - 1100) < 1 && Math.Abs(workspace.Height - 700) < 1,
                $"left={workspace.Left:F0}, top={workspace.Top:F0}, width={workspace.Width:F0}, height={workspace.Height:F0}");
            Observe("independent owner", workspace.Owner is null, "Owner=" + (workspace.Owner?.GetType().Name ?? "null"));

            workspace.Left = 80; workspace.Top = 80; workspace.Width = 1120; workspace.Height = 720;
            // Raise only our normal windows; preserve the baseline's owner-imposed relative order.
            const uint keepBoundsAndActivation = 0x0001 | 0x0002 | 0x0010;
            SetWindowPos(mainHandle, IntPtr.Zero, 0, 0, 0, 0, keepBoundsAndActivation);
            SetWindowPos(mergeHandle, IntPtr.Zero, 0, 0, 0, 0, keepBoundsAndActivation);
            SetForegroundWindow(mainHandle);
            try
            {
                main.Activate(); Pump();
                PhysicalCaptionClick(main);
                var overlap = main.PointToScreen(new Point(300, 220));
                var front = GetAncestor(WindowFromPoint(new NativePoint((int)overlap.X, (int)overlap.Y)), 2);
                Observe("physical click brings main above Merge", GetForegroundWindow() == mainHandle && front == mainHandle,
                    $"foreground={GetForegroundWindow()}, overlapping top window={front}, expected={mainHandle}");
                workspace.Activate(); Pump();
                PhysicalClick(workspace, workspace.ActualWidth - 16, workspace.ActualHeight / 2);
                front = GetAncestor(WindowFromPoint(new NativePoint((int)overlap.X, (int)overlap.Y)), 2);
                Observe("physical click brings Merge above main", GetForegroundWindow() == mergeHandle && front == mergeHandle,
                    $"foreground={GetForegroundWindow()}, overlapping top window={front}, expected={mergeHandle}");

                SetForegroundWindow(mergeHandle); Pump();
                var mergeActivated = OwnForegroundMatches(mergeHandle);
                SetForegroundWindow(mainHandle); Pump();
                var mainActivated = OwnForegroundMatches(mainHandle);
                if (mergeActivated && mainActivated)
                {
                    KeyEvent(0x12, false); KeyEvent(0x09, false); KeyEvent(0x09, true); KeyEvent(0x12, true);
                    Thread.Sleep(100); Pump();
                    Observe("Alt+Tab switches between own windows", GetForegroundWindow() == mergeHandle,
                        $"foreground={GetForegroundWindow()}, expected Merge={mergeHandle}; own windows were placed first in activation history");
                }
                else Observe("Alt+Tab switches between own windows", false,
                    "Own-window activation precondition failed; no Alt+Tab was sent");
            }
            catch (PhysicalInputUnavailableException exception) when (skipUnavailablePhysical)
            {
                Skip("physical click brings main above Merge", exception.Message);
                Skip("physical click brings Merge above main", exception.Message);
                Skip("Alt+Tab switches between own windows", exception.Message + "; no Alt+Tab was sent after this obstruction");
            }

            main.WindowState = WindowState.Minimized; Pump();
            Observe("main minimize leaves Merge visible", IsWindowVisible(mergeHandle) && !IsIconic(mergeHandle),
                $"Merge visible={IsWindowVisible(mergeHandle)}, iconic={IsIconic(mergeHandle)}");
            main.WindowState = WindowState.Normal; Pump();
            workspace.WindowState = WindowState.Minimized; Pump();
            Observe("Merge minimize leaves main visible", IsWindowVisible(mainHandle) && !IsIconic(mainHandle),
                $"main visible={IsWindowVisible(mainHandle)}, iconic={IsIconic(mainHandle)}");
            workspace.WindowState = WindowState.Normal; Pump();

            var saved = (workspace.Left, workspace.Top, workspace.Width, workspace.Height);
            workspace.Close(); Pump();
            workspace = (DiffWorkspaceWindow)Call(main, "EnsureDiffWorkspace")!;
            view = workspace.OpenComparison(new() { Kind = DiffEndpointKind.Scratch, DisplayName = "left", Text = "left\ncontent" },
                new() { Kind = DiffEndpointKind.Scratch, DisplayName = "right", Text = "right\ncontent" });
            Pump();
            Observe("reopened Merge restores placement", Math.Abs(workspace.Left - saved.Left) < 1 && Math.Abs(workspace.Top - saved.Top) < 1 &&
                Math.Abs(workspace.Width - saved.Width) < 1 && Math.Abs(workspace.Height - saved.Height) < 1,
                $"expected={saved}, actual=({workspace.Left}, {workspace.Top}, {workspace.Width}, {workspace.Height})");
            Call(main, "ApplyTheme", "Dark"); Pump();
            var native = Field<Scintilla>(Field<ScintillaEditorHost>(view, "_leftEditor"), "_editor");
            Observe("theme callback while independent", native.Styles[ScintillaNET.Style.Default].ForeColor == ThemePalette.Get("Dark").EditorForeground,
                "Native editor style follows main Dark theme");
            Field<DiffWindowCallbacks>(view, "_callbacks").ShowHelp!(); Pump();
            var help = app.Windows.OfType<HelpWindow>().Single();
            Observe("help callback before main close", help.IsVisible && ReferenceEquals(help.Owner, main), "Main help callback opens the requested topic");
            help.Close();
            var before = Field<ScintillaEditorHost>(view, "_leftEditor").GetText();
            main.Close();
            var closeWatch = Stopwatch.StartNew();
            while (!(bool)typeof(MainWindow).GetField("_windowClosed", Hidden)!.GetValue(main)! && closeWatch.Elapsed < TimeSpan.FromSeconds(10)) Pump();
            Observe("main close keeps independent Merge", !Field<bool>(view, "_resourcesReleased") && workspace.IsVisible && workspace.Owner is null &&
                Field<ScintillaEditorHost>(view, "_leftEditor").GetText() == before,
                "Merge remains visible with unchanged text after normal main Close");
            Field<DiffWindowCallbacks>(view, "_callbacks").ShowHelp!(); Pump();
            help = app.Windows.OfType<HelpWindow>().Single();
            Observe("help callback after main close", help.IsVisible && ReferenceEquals(help.Owner, workspace), "Standalone help is owned by the remaining Merge");
            help.Close();
            Console.WriteLine($"Window verification: passed={observations.Count(item => item.Passed == true)}, failed={observations.Count(item => item.Passed == false)}, skipped={observations.Count(item => item.Passed is null)} (skipped checks are unverified)");
            return baseline || observations.All(item => item.Passed != false) ? 0 : 1;
        }
        finally
        {
            if (workspace is not null)
            {
                foreach (var tab in Field<System.Windows.Controls.TabControl>(workspace, "ComparisonTabs").Items.OfType<System.Windows.Controls.TabItem>())
                    ((DiffTabView)tab.Content).ReleaseResources();
                workspace.Close();
            }
            if (!(bool)typeof(MainWindow).GetField("_windowClosed", Hidden)!.GetValue(main)!)
            {
                foreach (var document in main.Documents) document.Editor.ReleaseResources();
                typeof(MainWindow).GetField("_allowClose", Hidden)!.SetValue(main, true);
                main.Close();
            }
            var directory = Path.GetFullPath("artifacts/merge-responsiveness"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, baseline ? "window-baseline.json" : "window-after.json"),
                JsonSerializer.Serialize(observations.Select(item => new
                {
                    item.Name, item.Passed, Status = item.Passed is null ? "SKIP" : item.Passed == true ? "PASS" : "FAIL", item.Evidence
                }), new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
    }

    private static void PhysicalClick(Window window, double x, double y)
    {
        var point = window.PointToScreen(new Point(x, y));
        PhysicalScreenClick(window, point);
    }
    private static void PhysicalCaptionClick(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Check(GetWindowRect(handle, out var outer), "Could not inspect this test window's caption bounds");
        var client = window.PointToScreen(new Point(0, 0));
        var captionMidpoint = outer.Top + (client.Y - outer.Top) / 2;
        PhysicalScreenClick(window, new Point((outer.Left + outer.Right) / 2d, captionMidpoint));
    }
    private static void PhysicalScreenClick(Window window, Point point)
    {
        var owner = new WindowInteropHelper(window).Handle;
        var covering = GetAncestor(WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)), 2);
        GetWindowThreadProcessId(owner, out var expectedProcess);
        GetWindowThreadProcessId(covering, out var coveringProcess);
        if (covering != owner)
            throw new PhysicalInputUnavailableException(
                $"Physical click point is covered; no click was sent. expected HWND={owner}/PID={expectedProcess}, covering HWND={covering}/PID={coveringProcess}, point=({point.X:F0},{point.Y:F0})");
        SetCursorPos((int)point.X, (int)point.Y);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50); Pump();
    }
    private static bool OwnForegroundMatches(IntPtr expected)
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        if (process != Environment.ProcessId)
            throw new PhysicalInputUnavailableException($"Foreground activation is unavailable; no keyboard input was sent. expected HWND={expected}, foreground HWND={foreground}/PID={process}");
        return foreground == expected;
    }
    private sealed class PhysicalInputUnavailableException(string message) : InvalidOperationException(message) { }
    private static void KeyEvent(byte key, bool release) => keybd_event(key, 0, release ? 2u : 0, UIntPtr.Zero);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    private static IntPtr PointParameter(int x, int y) => new(unchecked((y << 16) | (x & 0xffff)));
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr parameter);
}
