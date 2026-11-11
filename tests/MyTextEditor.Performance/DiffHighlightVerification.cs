using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using ScintillaNET;
using Application = System.Windows.Application;

internal static class DiffHighlightVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint DiffMask = (1u << 20) | (1u << 21) | (1u << 22);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new ScintillaEditorHost();
        var native = (Scintilla)typeof(ScintillaEditorHost).GetField("_editor", Hidden)!.GetValue(host)!;
        var compared = typeof(ScintillaEditorHost).GetMethod("GetComparedLineRange", Hidden)!;
        var window = new Window { Content = host, Width = 550, Height = 320, ShowInTaskbar = false };
        void Pump() { window.UpdateLayout(); native.Refresh(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        EditorTextRange Cached(int line, string text, int start, int length) =>
            (EditorTextRange)compared.Invoke(host, [line, text, start, length])!;
        int BytePosition(int position) => Encoding.UTF8.GetByteCount(host.GetText().AsSpan(0, position));
        int Indicator(int indicator, int bytePosition) => (int)native.DirectMessage(2507, (IntPtr)indicator, (IntPtr)bytePosition);
        void Paint(int line, EditorTextRange span, DiffHighlightKind kind) =>
            host.SetDiffHighlights([new(line, 1, kind)], [new(span.Start, span.Length, kind)]);
        void VerifyOnly(int markedLine, int indicator, EditorTextRange span)
        {
            var expectedStart = BytePosition(span.Start); var expectedEnd = BytePosition(span.End);
            var byteLength = (int)native.DirectMessage(2006);
            for (var line = 0; line < host.LineCount; line++)
                Check((native.Lines[line].MarkerGet() & DiffMask) == (line == markedLine ? 1u << indicator : 0),
                    $"Unexpected diff marker after repaint on line {line}");
            for (var position = 0; position < byteLength; position++)
                for (var mark = 20; mark <= 22; mark++)
                    Check((Indicator(mark, position) != 0) ==
                        (mark == indicator && position >= expectedStart && position < expectedEnd),
                        $"Unexpected indicator {mark} at UTF-8 byte {position}");
        }
        void Reject(int line, string text, int start, int length)
        {
            try { Cached(line, text, start, length); }
            catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException) { return; }
            throw new InvalidOperationException("Cached line mapping accepted an invalid UTF-16 boundary or range");
        }
        try
        {
            window.Show();
            var original = "가😀나\r\n\r\n둘째 é😀\n" + string.Join('\n', Enumerable.Range(0, 80).Select(i => $"행 {i} 😀"));
            host.LoadUtf8(Encoding.UTF8.GetBytes(original)); Pump();
            foreach (var (line, text, start, length) in new[]
            {
                (0, "가😀나", 1, 2), (1, "", 0, 0), (2, "둘째 é😀", 4, 2), (3, "행 0 😀", 0, 6)
            })
                Check(Cached(line, text, start, length) == host.GetLineTextRange(line, start, length),
                    "Cached mapping differs from native line mapping for Unicode/CRLF/empty lines");
            Reject(0, "가😀나", 2, 1); Reject(0, "가😀나", 1, 1);
            Reject(0, "가😀나", -1, 1); Reject(0, "가😀나", 0, 5); Reject(host.LineCount, "", 0, 0);

            var first = Cached(0, "가😀나", 1, 2);
            Check(host.GetTextRange(first) == "😀", "Cached emoji mapping selected different text");
            Paint(0, first, DiffHighlightKind.Modified); VerifyOnly(0, 22, first);
            var destination = Cached(50, "행 47 😀", 5, 2);
            host.SetSelection(destination); host.SetFirstVisibleLine(45); Pump();
            var selection = host.SelectionRange; var top = host.FirstVisibleLine;
            var caretLine = host.CurrentLine;
            Check((native.Lines[caretLine].MarkerGet() & (1u << 24)) != 0, "Current-line marker fixture is missing");
            native.IndicatorCurrent = 23; native.IndicatorFillRange(first.Start, first.Length);
            Paint(50, destination, DiffHighlightKind.Added); VerifyOnly(50, 20, destination);
            Check(Indicator(23, BytePosition(first.Start)) != 0, "Viewport repaint erased user indicator 23");
            Check((native.Lines[caretLine].MarkerGet() & (1u << 24)) != 0, "Viewport repaint erased current-line marker");
            Check(host.SelectionRange == selection && host.FirstVisibleLine == top, "Highlight repaint moved selection or viewport");
            host.ClearDiffHighlights(); VerifyOnly(-1, -1, default);
            Check(Indicator(23, BytePosition(first.Start)) != 0, "Full diff clear erased user indicator 23");

            Paint(50, destination, DiffHighlightKind.Deleted);
            host.ReplaceRange(new(0, 0), "앞😀\r\n");
            var afterInsert = Cached(0, "앞😀", 1, 2);
            Paint(0, afterInsert, DiffHighlightKind.Modified); VerifyOnly(0, 22, afterInsert);
            host.ReplaceRange(new(0, "앞😀\r\n".Length), "");
            Paint(0, first, DiffHighlightKind.Added); VerifyOnly(0, 20, first);
            Check(host.GetText() == original, "Highlight reset changed edit contents");

            host.LoadUtf8(Encoding.UTF8.GetBytes(original));
            Check(host.ContentRevision == 0, "Reload fixture must reset native revision");
            Paint(50, destination, DiffHighlightKind.Deleted);
            host.LoadUtf8(Encoding.UTF8.GetBytes("끝😀\r\n\r\n"));
            Check(host.ContentRevision == 0, "Second load must reuse revision zero");
            var shortSpan = Cached(0, "끝😀", 1, 2);
            Paint(0, shortSpan, DiffHighlightKind.Modified); VerifyOnly(0, 22, shortSpan);
            host.ReloadUtf8(Encoding.UTF8.GetBytes("😀"));
            Paint(0, Cached(0, "😀", 0, 2), DiffHighlightKind.Added); VerifyOnly(0, 20, new(0, 2));
            host.ClearDiffHighlights(); VerifyOnly(-1, -1, default);
            Console.WriteLine("PASS bounded diff markers/indicators, user/current-line isolation, viewport/selection, Unicode/emoji/CRLF/empty mapping, insert/delete reset, revision-zero reuse and shorter reload");
            return 0;
        }
        finally { host.ReleaseResources(); window.Close(); app.Shutdown(); }
    }
}
