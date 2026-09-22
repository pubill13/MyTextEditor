using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using StackPanel = System.Windows.Controls.StackPanel;

internal static class MergeClarityVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run()
    {
        var describe = typeof(DiffTabView).GetMethod("DescribeMerge", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Describe(DiffBlock block, DiffSide side) => (string)describe.Invoke(null, [block, side])!;
        var added = new DiffBlock(0, DiffBlockKind.Added, 3, 0, 3, 2, []);
        if (!Describe(added, DiffSide.Left).Contains("오른쪽 3–4행 (2행) 삭제")) throw new Exception("Deletion direction/range incorrect");
        if (!Describe(added, DiffSide.Right).Contains("왼쪽 2행 뒤 삽입 위치에 추가")) throw new Exception("Insertion range incorrect");
        var modified = new DiffBlock(0, DiffBlockKind.Modified, 2, 1, 2, 3, []);
        if (!Describe(modified, DiffSide.Left).Contains("왼쪽 2행 → 오른쪽 2–4행 (3행)를 대체")) throw new Exception("Replacement range incorrect");
        var terminal = modified with { IsTerminalNewLineChange = true };
        if (!Describe(terminal, DiffSide.Right).Contains("오른쪽의 파일 끝 개행 상태를 왼쪽에 반영")) throw new Exception("Terminal newline description incorrect");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("OneDark").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
        };
        var window = new DiffWorkspaceWindow(new(), callbacks, new("Consolas", 11, true));
        var view = window.OpenEmptyTab();
        window.Show();
        try
        {
            window.UpdateLayout();
            Pump(() => view.IsLoaded);
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            left.ReplaceAll("같음\n원본\n끝");
            right.ReplaceAll("같음\n새 값\n추가 행\n끝");
            Pump(() => view.DifferenceCount > 0);
            view.UpdateLayout();
            typeof(DiffTabView).GetMethod("RenderMergeGutter", Private)!.Invoke(view, [null, EventArgs.Empty]);
            var panels = Field<List<StackPanel>>(view, "_gutterPool");
            var connectors = Field<List<System.Windows.Shapes.Polygon>>(view, "_gutterConnections");
            if (panels.Count == 0 || connectors.Count != panels.Count) throw new Exception("No pooled block connectors");
            var button = (System.Windows.Controls.Button)panels[0].Children[1];
            if (!button.ToolTip.ToString()!.Contains("왼쪽 2행 → 오른쪽 2–3행 (2행)를 대체")) throw new Exception("Gutter tooltip differs from merge range");
            if (connectors[0].Points.Count != 4 || connectors[0].Visibility != Visibility.Visible) throw new Exception("Connector range not displayed");
            var summary = Field<TextBlock>(view, "BlockRangeText");
            if (!summary.Text.Contains("왼쪽 2행") || !summary.Text.Contains("오른쪽 2–3행")) throw new Exception("Current block range not displayed");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Pump(() => Field<TextDiffResult?>(view, "_result") is { Blocks.Count: 0 });
            if (right.GetText() != "같음\r\n원본\r\n끝") throw new Exception("Merge must preserve target CRLF line endings");
            right.Undo();
            Pump(() => view.DifferenceCount > 0);
            if (right.GetText() != "같음\n새 값\n추가 행\n끝") throw new Exception("Merge Undo did not restore exact replacement");
            Console.WriteLine("PASS Merge direction/ranges/insertion/deletion/end-newline descriptions, rendered connectors, block action and Undo");
            return 0;
        }
        finally
        {
            typeof(DiffTabView).GetField("_leftDirty", Private)!.SetValue(view, false);
            typeof(DiffTabView).GetField("_rightDirty", Private)!.SetValue(view, false);
            window.Close();
            app.Shutdown();
        }
    }

    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static void Pump(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Diff did not settle");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }
}
