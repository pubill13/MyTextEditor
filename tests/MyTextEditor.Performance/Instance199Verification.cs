using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class Instance199Verification
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr parameter);
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);

    public static int Run(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Executable required", executable);
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Length != 0)
            throw new InvalidOperationException("Close existing OmniEdit processes before the isolated instance test.");
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-instance199-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "첫 번째 문서.txt");
        var secondPath = Path.Combine(directory, "second file.txt");
        var takeoverPath = Path.Combine(directory, "수신 인계.txt");
        var concurrentPaths = new[] { Path.Combine(directory, "동시 A.txt"), Path.Combine(directory, "동시 B.txt") };
        var validAfterMissing = Path.Combine(directory, "오류 뒤 정상.txt");
        var afterCancel = Path.Combine(directory, "종료 취소 뒤.txt");
        var fixtures = new[] { firstPath, secondPath, takeoverPath, validAfterMissing, afterCancel }.Concat(concurrentPaths).ToArray();
        foreach (var path in fixtures) File.WriteAllText(path, "unmodified fixture\r\n");
        var processes = new List<Process>();
        Process Start(params string[] paths)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
            foreach (var path in paths) start.ArgumentList.Add(path);
            var process = Process.Start(start)!;
            processes.Add(process);
            return process;
        }
        try
        {
            var primary = Start();
            Wait(() => Window(primary) != IntPtr.Zero, "first window");
            Wait(() => DocumentTabCount(primary) == 1, "initial blank tab");
            var secondary = Start();
            Wait(() => Window(secondary) != IntPtr.Zero, "independent no-args window");
            ShowWindow(Window(primary), 6);
            var request = Start(firstPath, secondPath);
            Wait(() => request.HasExited, "forwarding process exit");
            Check(request.ExitCode == 0, "File request failed");
            Wait(() => HasTab(primary, firstPath) && HasTab(primary, secondPath), "both files in first window");
            Check(DocumentTabCount(primary) == 3, "External open removed the user's initial blank tab");
            Check(!HasTab(secondary, firstPath), "Files were routed to the newer window");
            Check(!IsIconic(Window(primary)), "Receiver remained minimized");
            var tabsBefore = DocumentTabCount(primary);
            var duplicate = Start(firstPath);
            Wait(() => duplicate.HasExited, "duplicate forwarding process exit");
            Check(duplicate.ExitCode == 0, "Duplicate request failed");
            Thread.Sleep(300);
            Check(DocumentTabCount(primary) == tabsBefore, "Duplicate request created another tab");

            var concurrent = concurrentPaths.Select(path => Start(path)).ToArray();
            Wait(() => concurrent.All(process => process.HasExited), "concurrent forwarding processes");
            Check(concurrent.All(process => process.ExitCode == 0), "Concurrent request failed");
            Wait(() => concurrentPaths.All(path => HasTab(primary, path)), "concurrent files in receiver");

            var partialFailure = Start(Path.Combine(directory, "missing.txt"), validAfterMissing);
            Console.WriteLine("Stage: missing + valid batch");
            Wait(() => Dialog(primary, "파일 열기") != null, "missing file warning");
            Check(HasTab(primary, validAfterMissing), "Missing file prevented subsequent valid file opening");
            InvokeDialogButton(primary, "파일 열기", "1");
            Wait(() => partialFailure.HasExited, "partial failure forwarding exit");
            Check(partialFailure.ExitCode == 0, "Accepted batch was reported as a delivery failure");

            var editor = VisibleEditor(primary);
            Console.WriteLine("Stage: modified document shutdown and rejection");
            Check(editor != IntPtr.Zero, "Active Scintilla HWND missing");
            SendMessage(editor, 0x0102, (IntPtr)'x', IntPtr.Zero);
            Check(primary.CloseMainWindow(), "Could not request modified primary shutdown");
            Wait(() => Dialog(primary, "프로그램 종료") != null, "modified document exit confirmation");
            var refused = Start(afterCancel);
            Wait(() => Dialog(refused, "OmniEdit 파일 열기") != null, "closing receiver rejects incoming request");
            InvokeDialogButton(refused, "OmniEdit 파일 열기", "1");
            Wait(() => refused.HasExited, "refused forwarding exit");
            Check(refused.ExitCode != 0, "Rejected request exited successfully");
            InvokeDialogButton(primary, "프로그램 종료", "2");
            Wait(() => Dialog(primary, "프로그램 종료") == null, "cancel exit confirmation");
            Check(!primary.HasExited && !HasTab(primary, afterCancel), "Canceled shutdown or rejected request changed documents");
            var accepted = Start(afterCancel);
            Wait(() => accepted.HasExited, "forwarding after canceled exit");
            Check(accepted.ExitCode == 0, "Receiver did not resume after canceled shutdown");
            Wait(() => HasTab(primary, afterCancel), "file received after exit canceled");
            Check(primary.CloseMainWindow(), "Could not request primary shutdown");
            Wait(() => Dialog(primary, "프로그램 종료") != null, "final modified document confirmation");
            InvokeDialogButton(primary, "프로그램 종료", "7");
            Wait(() => primary.HasExited, "primary shutdown");
            var takeover = Start(takeoverPath);
            Wait(() => takeover.HasExited, "takeover forwarding process exit");
            Check(takeover.ExitCode == 0, "Takeover request failed");
            Wait(() => HasTab(secondary, takeoverPath), "remaining window receiver takeover");
            Check(secondary.CloseMainWindow(), "Could not request secondary shutdown");
            Wait(() => secondary.HasExited, "secondary shutdown");
            Console.WriteLine("PASS external processes: independent windows; first receiver; blank tab retained; Unicode/multiple/duplicate/concurrent paths; partial file failure; minimized restore; closing rejection/cancel/resume; takeover; normal shutdown.");
            return 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(true); }
                process.Dispose();
            }
            foreach (var path in fixtures) File.Delete(path);
            Directory.Delete(directory);
        }
    }
    private static IntPtr Window(Process process) { process.Refresh(); return process.HasExited ? IntPtr.Zero : process.MainWindowHandle; }
    private static AutomationElement? Dialog(Process process, string title)
    {
        if (process.HasExited) return null;
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var id);
            var name = new StringBuilder(512);
            GetWindowText(handle, name, name.Capacity);
            if (id == process.Id && name.ToString() == title && IsWindowVisible(handle))
            { found = handle; return false; }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : AutomationElement.FromHandle(found);
    }
    private static void InvokeDialogButton(Process process, string title, string id)
    {
        var dialog = Dialog(process, title) ?? throw new InvalidOperationException("Dialog missing: " + title);
        var handle = new IntPtr(dialog.Current.NativeWindowHandle);
        var button = GetDlgItem(handle, int.Parse(id));
        if (button == IntPtr.Zero)
        {
            var expected = id == "1" ? "확인" : id == "2" ? "취소" : "아니";
            EnumChildWindows(handle, (child, _) =>
            {
                var label = new StringBuilder(256);
                GetWindowText(child, label, label.Capacity);
                if (label.ToString().Contains(expected)) { button = child; return false; }
                return true;
            }, IntPtr.Zero);
        }
        if (button == IntPtr.Zero) throw new InvalidOperationException("Native dialog button missing: " + title + " / " + id);
        PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static IntPtr VisibleEditor(Process process)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(Window(process), (handle, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            if (name.ToString().Contains("Scintilla", StringComparison.OrdinalIgnoreCase) && IsWindowVisible(handle))
            { found = handle; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private static AutomationElement? Tabs(Process process)
    {
        var handle = Window(process);
        return handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "DocumentTabs"));
    }
    private static bool HasTab(Process process, string path)
    {
        var tabs = Tabs(process);
        if (tabs == null) return false;
        var texts = tabs.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        return texts.Cast<AutomationElement>().Any(e => e.Current.Name.Contains(Path.GetFileName(path), StringComparison.Ordinal));
    }
    private static int DocumentTabCount(Process process) => Tabs(process)?.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Count ?? 0;
    private static void Wait(Func<bool> condition, string operation)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException(operation); Thread.Sleep(100); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
