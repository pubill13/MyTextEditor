using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using ScintillaNET;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;

internal static class MergeScrollVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var callbacks = new DiffWindowCallbacks { GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { } };
        var window = new DiffWorkspaceWindow(new(), callbacks, new("Consolas", 11, false));
        var view = window.OpenEmptyTab();
        void Pump() { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        window.Show(); Pump();
        try
        {
            foreach (var side in new[] { "_leftEditor", "_rightEditor" })
            {
                var host = Field<ScintillaEditorHost>(view, side);
                var native = Field<Scintilla>(host, "_editor");
                var bar = Field<Forms.Control>(host, "_horizontalBar");
                host.ReplaceAll(new string('x', 3000) + " END"); native.Refresh(); Pump();
                Check(native.HScrollBar && native.ScrollWidth > native.ClientSize.Width, "Native horizontal scrollbar has no long-line range");
                native.XOffset = native.ScrollWidth; Pump();
                Check(native.XOffset > 0, "Horizontal viewport did not move");
                native.XOffset = 0; Pump();
                Check(native.XOffset == 0, "Horizontal viewport did not return");                host.Undo(); Check(host.GetText() == "", "Scrollbar movement changed Undo");
                host.Redo(); Check(host.GetText().EndsWith(" END"), "Scrollbar movement changed Redo");
            }
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            left.ReplaceAll("cat cats CAT 한글 cat"); right.ReplaceAll("right cat");
            Field<System.Windows.Controls.TextBox>(view, "FindInput").Text = "cat";
            Field<System.Windows.Controls.TextBox>(view, "ReplaceInput").Text = "dog";
            Field<System.Windows.Controls.CheckBox>(view, "FindWholeWord").IsChecked = true;
            Field<System.Windows.Controls.ComboBox>(view, "FindSide").SelectedIndex = 0;
            typeof(DiffTabView).GetMethod("ReplaceAll_Click", Hidden)!.Invoke(view, [view, new RoutedEventArgs()]);
            Check(left.GetText() == "dog cats dog 한글 dog" && right.GetText() == "right cat", "Whole-word replace or target isolation failed");
            left.Undo(); Check(left.GetText() == "cat cats CAT 한글 cat", "Replace all must use one Undo");
            left.Redo(); Check(left.GetText() == "dog cats dog 한글 dog", "Replace all Redo failed");
            Console.WriteLine("PASS Merge search whole-word replacement, side isolation, Undo/Redo");
            Console.WriteLine("PASS Merge left/right horizontal viewport, native scrolling range and Undo/Redo");
            return 0;
        }
        finally
        {
            typeof(DiffTabView).GetField("_leftDirty", Hidden)!.SetValue(view, false);
            typeof(DiffTabView).GetField("_rightDirty", Hidden)!.SetValue(view, false);
            view.ReleaseResources(); window.Close(); app.Shutdown();
        }
    }
}
