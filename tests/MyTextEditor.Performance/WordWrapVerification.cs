using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using ScintillaNET;
using Application = System.Windows.Application;

internal static class WordWrapVerification
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var editor = new ScintillaEditorHost();
        var window = new Window { Width = 420, Height = 260, Content = editor, ShowInTaskbar = false };
        var native = (Scintilla)typeof(ScintillaEditorHost).GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        void Pump() { window.UpdateLayout(); native.Refresh(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        try
        {
            VerifyNativeExtraction();
            window.Show(); Pump();
            var original = string.Join("\r\n", Enumerable.Repeat(new string('x', 4000), 40));
            editor.LoadUtf8(Encoding.UTF8.GetBytes(original));
            editor.ReplaceAll(original + "\r\n끝");
            editor.SetSelection(new EditorTextRange(25, 20));
            var revision = editor.ContentRevision;
            editor.SetWordWrap(true); Pump();
            Check(editor.DisplayLineCount > editor.LineCount * 10, "Wrapped display line count must include all sublines");
            var bar = typeof(ScintillaEditorHost).GetField("_verticalBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            var maximum = (int)bar.GetType().GetField("_maximum", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
            Check(maximum == editor.DisplayLineCount - editor.LinesOnScreen, "Custom scrollbar must use display lines");
            bar.GetType().GetMethod("MoveTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, [maximum]); Pump();
            Check(editor.FirstVisibleLine == maximum && editor.FirstVisibleLine > editor.LineCount, "Scrollbar must reach wrapped document bottom");
            editor.ScrollToLine(3); Pump();
            Check(native.DocLineFromVisible(editor.FirstVisibleLine) == 3, "Document scroll target must map to a display line");
            editor.SetWordWrap(false); Pump();
            Check(editor.FirstVisibleLine == 3, "Disabling wrap must retain the top document line");
            Check(editor.SelectionRange == new EditorTextRange(25, 20) && editor.ContentRevision == revision,
                "Wrap toggles must retain selection and content revision");
            editor.SetWordWrap(true); Pump();
            window.Width = 800; Pump();
            editor.SetFirstVisibleLine(editor.DisplayLineCount); Pump();
            Check(editor.FirstVisibleLine > editor.LineCount, "Resized wrapping must remain scrollable");
            Check(editor.GetText() == original + "\r\n끝" && editor.IsModified, "Wrapping must not change newlines or dirty state");
            editor.Undo();
            Check(editor.GetText() == original && !editor.IsModified, "Wrapping must preserve Undo and save point");
            editor.LoadUtf8(Encoding.UTF8.GetBytes(new string('x', 10000))); Pump();
            editor.SetFirstVisibleLine(40); Pump();
            Check(editor.LineCount == 1 && editor.FirstVisibleLine == 40, "A single long wrapped line must scroll past physical line zero");
            editor.ReloadUtf8(Encoding.UTF8.GetBytes(new string('y', 12000)));
            editor.SetFirstVisibleLine(40); Pump();
            Check(editor.FirstVisibleLine == 40, "Reload must restore wrapped scroll before the next paint");
            Console.WriteLine("PASS wrap display coordinates, custom scrollbar bottom, resize, selection, revision, newlines and Undo");
            return 0;
        }
        finally { editor.ReleaseResources(); window.Close(); app.Shutdown(); }
    }

    private static void VerifyNativeExtraction()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"OmniEdit-native-{Guid.NewGuid():N}.dll");
        try
        {
            var extract = typeof(ScintillaEditorHost).GetMethod("ExtractResource", BindingFlags.Static | BindingFlags.NonPublic)!;
            extract.Invoke(null, ["MyTextEditor.Native.win-x64.Scintilla.dll", path]);
            var length = new System.IO.FileInfo(path).Length;
            System.IO.File.WriteAllText(path, "invalid native file");
            extract.Invoke(null, ["MyTextEditor.Native.win-x64.Scintilla.dll", path]);
            if (new System.IO.FileInfo(path).Length != length) throw new InvalidOperationException("Native extraction did not replace the incomplete file");
        }
        finally { System.IO.File.Delete(path); }
    }
}
