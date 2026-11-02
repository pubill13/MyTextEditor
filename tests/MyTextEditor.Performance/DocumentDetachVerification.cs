using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

internal static class DocumentDetachVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Isolate(MainWindow window)
    {
        Set(window, "_settingsReady", false);
        ((DispatcherTimer)typeof(MainWindow).GetField("_settingsSaveTimer", Private)!.GetValue(window)!).Stop();
        Call(window, "StopFileSync");
    }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var source = new MainWindow { ShowInTaskbar = false, Topmost = true, WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, Width = 1040, Height = 680 };
        Isolate(source);
        MainWindow? target = null;
        try
        {
            source.Show(); source.Activate(); Pump();
            var document = source.Documents[0];
            var editor = document.Editor;
            editor.LoadUtf8(Encoding.UTF8.GetBytes("original"));
            editor.ReplaceAll("미저장 문서 😀");
            editor.SetWordWrap(true);
            editor.SelectUtf16Range(0, 3);
            document.ChangeEncoding(DocumentEncodingKind.Utf8Bom);
            var selection = editor.SelectionRange;
            var position = source.PointToScreen(new Point(source.ActualWidth + 40, 100));
            Set(source, "_dragDocument", document);
            Set(source, "_tabDetachPosition", position);
            Call(source, "FinishDocumentTabDrag", false);
            Check(source.Documents.Contains(document) && app.Windows.OfType<MainWindow>().Count() == 1, "Cancelled drag detached a document");
            var grid = (System.Windows.Controls.Grid)typeof(MainWindow).GetField("DocumentPanesGrid", Private)!.GetValue(source)!;
            var drop = grid.PointFromScreen(position);
            typeof(DocumentPanesVerification).GetMethod("DragTab", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { source, document, drop });
            target = app.Windows.OfType<MainWindow>().SingleOrDefault(window => window != source);
            Check(target is not null, $"Detach failed: window={source.Left},{source.Top},{source.ActualWidth},{source.ActualHeight}; drop={position}; state={typeof(MainWindow).GetField("StatusMessage", Private)!.GetValue(source)}");
            Isolate(target!); Pump();
            Check(source.Documents.Count == 0 && target!.Documents.Single() == document, "Document was copied or lost");
            Check(ReferenceEquals(editor, document.Editor) && document.Text == "미저장 문서 😀", "Native editor/text not preserved");
            Check(document.EncodingModified && document.HasByteOrderMark && editor.WordWrap, "Encoding or wrap lost");
            Check(editor.SelectionRange == selection, "Selection lost");
            editor.Undo(); Check(document.Text == "original", "Undo history lost"); editor.Redo();
            Call(target!, "ApplyTheme", "Dark");
            Check(!ReferenceEquals(source.Resources.MergedDictionaries[0], target!.Resources.MergedDictionaries[0]), "Windows share theme state");
            Set(source, "_allowClose", true); source.Close(); Pump();
            Check(target.IsVisible && document.Text == "미저장 문서 😀", "Closing source invalidated detached document");
            editor.ReplaceAll("new window edit");
            Check(document.IsModified, "Detached editor events not routed");
            editor.Undo(); Check(document.Text == "미저장 문서 😀", "Editing detached window lost Undo");
            Console.WriteLine("PASS detach: same editor, unsaved text, BOM/encoding dirty, wrap, selection, Undo/Redo, independent theme and source close");
            return 0;
        }
        finally
        {
            foreach (var window in app.Windows.OfType<MainWindow>().ToArray())
            {
                Isolate(window);
                foreach (var document in window.Documents) document.Editor.ReleaseResources();
                Set(window, "_allowClose", true); window.Close();
            }
            app.Shutdown();
        }
    }
}
