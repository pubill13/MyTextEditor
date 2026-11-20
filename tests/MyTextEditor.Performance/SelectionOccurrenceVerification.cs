using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using ScintillaNET;
using Application = System.Windows.Application;

internal static class SelectionOccurrenceVerification
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr word, IntPtr parameter);

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new ScintillaEditorHost();
        host.EnableSelectionOccurrences();
        var other = new ScintillaEditorHost();
        other.EnableSelectionOccurrences();
        var native = (Scintilla)typeof(ScintillaEditorHost)
            .GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var tabs = new System.Windows.Controls.TabControl();
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "First", Content = host });
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "Second", Content = other });
        var window = new Window { Width = 740, Height = 280, Content = tabs, ShowInTaskbar = false };
        void Pump()
        {
            native.Refresh();
            System.Windows.Forms.Application.DoEvents();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        void Mouse(uint message, int position, bool down)
        {
            int x = native.PointXFromPosition(position), y = native.PointYFromPosition(position) + 3;
            SendMessage(native.Handle, message, down ? (IntPtr)1 : IntPtr.Zero, (IntPtr)(x | (y << 16)));
        }
        void Drag(int start, int length)
        {
            Mouse(0x201, start, true);
            Mouse(0x200, start + length, true);
            Mouse(0x202, start + length, false);
            Pump();
        }
        void Expect(string text, int index, bool highlighted)
        {
            int bytePosition = Encoding.UTF8.GetByteCount(text.AsSpan(0, index));
            bool actual = native.DirectMessage(2507, (IntPtr)24, (IntPtr)bytePosition) != IntPtr.Zero;
            if (actual != highlighted)
                throw new InvalidOperationException($"Expected highlight={highlighted} at UTF-16 position {index} with '{native.SelectedText}' selected.");
        }
        try
        {
            window.Show();
            const string text = "cat cat catalog Cat 한글 한글 # # 가 나 가 나\r\ncat";
            host.LoadUtf8(Encoding.UTF8.GetBytes(text));
            window.Activate();
            host.FocusEditor();
            Pump();
            Mouse(0x201, 1, true);
            Mouse(0x202, 1, false);
            Mouse(0x201, 1, true);
            Mouse(0x202, 1, false);
            Pump();
            Expect(text, 4, true);
            Expect(text, 8, false);
            Thread.Sleep(System.Windows.Forms.SystemInformation.DoubleClickTime + 20);
            Drag(0, 3);
            Expect(text, 4, true);
            Expect(text, 8, true);
            Expect(text, 16, false);
            foreach (var term in new[] { "한글", "#", "가 나" })
            {
                Drag(text.IndexOf(term, StringComparison.Ordinal), term.Length);
                Expect(text, text.LastIndexOf(term, StringComparison.Ordinal), true);
                Expect(text, 4, false);
            }
            foreach (var (start, length) in new[] { (3, 1), (0, text.Length), (0, 0) })
            {
                host.SelectUtf16Range(start, length);
                Pump();
                for (int i = 0; i < text.Length; i++) Expect(text, i, false);
            }
            host.SelectUtf16Range(0, 3);
            native.AppendText(" cat");
            Pump();
            Expect(text + " cat", text.Length + 1, true);
            tabs.SelectedIndex = 1;
            Pump();
            other.LoadUtf8("dog dog"u8.ToArray());
            other.SelectUtf16Range(0, 3);
            var otherNative = (Scintilla)typeof(ScintillaEditorHost)
                .GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(other)!;
            otherNative.Refresh();
            Pump();
            if (otherNative.DirectMessage(2507, (IntPtr)24, (IntPtr)4) == IntPtr.Zero)
                throw new InvalidOperationException("Second tab did not highlight its own selection.");
            tabs.SelectedIndex = 0;
            Pump();
            Expect(text, 4, true);
            Expect(text, 16, false);
            string large = string.Concat(Enumerable.Repeat("x x 한글\n", 160_000));
            host.LoadUtf8(Encoding.UTF8.GetBytes(large));
            host.SelectUtf16Range(0, 0);
            host.SetFirstVisibleLine(0);
            Pump();
            var watch = Stopwatch.StartNew();
            Drag(0, 1);
            Expect(large, 2, true);
            host.SetFirstVisibleLine(100_000);
            Pump();
            Expect(large, native.Lines[100_000].Position, true);
            if (watch.Elapsed.TotalSeconds > 3)
                throw new InvalidOperationException($"Selection and scroll took {watch.Elapsed}.");
            Console.WriteLine($"PASS whole-word double-click, substring drag, Unicode, symbols, phrases, clear and viewport refresh in {watch.ElapsedMilliseconds} ms");
            return 0;
        }
        finally { host.ReleaseResources(); other.ReleaseResources(); window.Close(); app.Shutdown(); }
    }
}
