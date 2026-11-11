using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyTextEditor;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Diff;
using MyTextEditor.Models;
using TabControl = System.Windows.Controls.TabControl;

internal static class FolderCompareVerification
{
    private sealed class TestApp : App { protected override void OnStartup(StartupEventArgs e) { } }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void PumpUntil(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(30))
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(done(), "Timed out waiting for folder comparison");
    }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Capture(FrameworkElement view, string name)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/folder-compare");
        using var stream = File.Create($"artifacts/folder-compare/{name}.png");
        encoder.Save(stream);
    }

    public static int Run()
    {
        var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var root = Path.Combine(Path.GetTempPath(), "OmniEdit-FolderUi-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(Path.Combine(left, "sub"));
        Directory.CreateDirectory(right);
        DiffWorkspaceWindow? workspace = null;
        MainWindow? main = null;
        try
        {
            File.WriteAllText(Path.Combine(left, "changed.txt"), "left\ntext", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(right, "changed.txt"), "right\ntext", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(left, "sub", "new.txt"), "new\ntext", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(right, "only-right.txt"), "right only", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(left, "binary.bin"), [0, 1, 2, 255]);
            main = new MainWindow { ShowInTaskbar = false };
            typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(main, false);
            Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
            Call(main, "StopFileSync");
            main.Show();
            workspace = (DiffWorkspaceWindow)Call(main, "EnsureDiffWorkspace")!;
            var tabs = Field<TabControl>(workspace, "ComparisonTabs");
            var folder = workspace.OpenFolderComparison();
            Await(folder.CompareAsync(left, right));
            Check(folder.Entries.Count == 4, "Recursive folder listing failed");
            Check(folder.Entries.Single(e => e.RelativePath == "changed.txt").Status == FolderCompareStatus.Different, "Changed row");
            var folderItem = (TabItem)tabs.SelectedItem;
            var list = (DataGrid)folder.FindName("ResultsList");
            Capture(folder, "light");
            var originalRows = folder.Entries;
            var canceled = folder.CompareAsync();
            Call(folder, "Cancel_Click", folder, new RoutedEventArgs());
            Await(canceled);
            Check(folder.Entries.SequenceEqual(originalRows), "Canceled comparison replaced previous results");
            var superseded = folder.CompareAsync(right, left);
            var latest = folder.CompareAsync(left, right);
            Await(Task.WhenAll(superseded, latest));
            Check(folder.Entries.Single(e => e.RelativePath == Path.Combine("sub", "new.txt")).Status == FolderCompareStatus.LeftOnly, "Superseded result overwrote latest comparison");
            var selected = list.Items.Cast<FolderCompareTabView.Row>().Single(r => r.RelativePath == "changed.txt");
            list.SelectedItem = selected;
            var before = workspace.ComparisonCount;
            list.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => workspace.ComparisonCount == before + 1 && !Field<bool>(folder, "_opening"));
            Check(workspace.ComparisonCount == before + 1 && !ReferenceEquals(tabs.SelectedItem, folderItem), "Opening file must add and select a new tab");
            Check(ReferenceEquals(folderItem.Content, folder) && ReferenceEquals(list.SelectedItem, selected), "Folder listing/selection replaced");
            var file = (DiffTabView)((TabItem)tabs.SelectedItem).Content;
            PumpUntil(() => Field<bool>(file, "_initialized"));
            var editor = Field<ScintillaEditorHost>(file, "_leftEditor");
            editor.ReplaceAll("edited\ntext");
            editor.Undo();
            Check(editor.GetText().Replace("\r\n", "\n") == "left\ntext", "File comparison Undo regression");
            editor.Redo();
            Await((Task<bool>)Call(file, "SaveEndpointAsync", true)!);
            Check(File.ReadAllText(Path.Combine(left, "changed.txt")) == editor.GetText(), "File save regression");
            tabs.SelectedItem = folderItem;
            PumpUntil(() => Field<CancellationTokenSource?>(folder, "_operation") is null);
            Check(list.SelectedItem is FolderCompareTabView.Row { RelativePath: "changed.txt" }, "Return to folder lost selection");
            var oneSided = folder.Entries.Single(e => e.RelativePath == Path.Combine("sub", "new.txt"));
            var oneSidedRow = list.Items.Cast<FolderCompareTabView.Row>().Single(row => row.RelativePath == oneSided.RelativePath);
            list.ScrollIntoView(oneSidedRow);
            list.UpdateLayout();
            var container = (DataGridRow)list.ItemContainerGenerator.ContainerFromItem(oneSidedRow);
            before = workspace.ComparisonCount;
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = container };
            Call(folder, "Results_DoubleClick", list, doubleClick);
            PumpUntil(() => workspace.ComparisonCount == before + 1 && !Field<bool>(folder, "_opening"));
            file = (DiffTabView)((TabItem)tabs.SelectedItem).Content;
            PumpUntil(() => Field<bool>(file, "_initialized"));
            var target = Path.Combine(right, "sub", "new.txt");
            Check(!File.Exists(target) && !Directory.Exists(Path.GetDirectoryName(target)), "Opening created destination on disk");
            var targetEditor = Field<ScintillaEditorHost>(file, "_rightEditor");
            Check(targetEditor.GetText() == "", "Missing side must be empty");
            Call(file, "CopyAll", MyTextEditor.Core.Models.DiffSide.Left);
            Await((Task<bool>)Call(file, "SaveEndpointAsync", false)!);
            Check(File.Exists(target) && File.ReadAllText(target).Replace("\r\n", "\n") == "new\ntext", "Saving missing side must create counterpart and directories");
            tabs.SelectedItem = folderItem;
            PumpUntil(() => Field<CancellationTokenSource?>(folder, "_operation") is null);
            Check(folder.Entries.Single(e => e.RelativePath == oneSided.RelativePath).Status == FolderCompareStatus.Equal, "Returning must refresh saved file");
            var filter = (System.Windows.Controls.CheckBox)folder.FindName("DifferencesOnly");
            filter.IsChecked = true;
            Check(list.Items.Cast<FolderCompareTabView.Row>().All(row => row.Status != FolderCompareStatus.Equal), "Difference filter shows equal files");
            filter.IsChecked = false;
            before = workspace.ComparisonCount;
            Await(folder.OpenEntryAsync(folder.Entries.Single(e => e.RelativePath == "binary.bin")));
            Check(workspace.ComparisonCount == before && ((TextBlock)folder.FindName("SummaryText")).Text.Contains("열지 못했습니다"), "Binary opened as editable text");
            Await(folder.OpenEntryAsync(folder.Entries.Single(e => e.RelativePath == "only-right.txt")));
            file = (DiffTabView)((TabItem)tabs.SelectedItem).Content;
            PumpUntil(() => Field<bool>(file, "_initialized"));
            Check(Field<ScintillaEditorHost>(file, "_leftEditor").GetText() == "", "Right-only file missing left side");
            var dark = ThemePalette.Get("Dark");
            workspace.Resources.MergedDictionaries.Add(dark.CreateResources());
            workspace.ApplyAppearance(new DiffAppearance("Consolas", 14, true) { Palette = dark });
            Check(list.FontSize == 14, "Folder font setting not applied");
            tabs.SelectedItem = folderItem;
            PumpUntil(() => Field<CancellationTokenSource?>(folder, "_operation") is null);
            Capture(folder, "dark");
            main.Close();
            PumpUntil(() => !app.Windows.OfType<MainWindow>().Contains(main));
            Check(workspace.IsVisible && workspace.Owner is null && !folder.IsReleased, "Main close released mixed workspace");
            File.WriteAllText(Path.Combine(left, "added.txt"), "new external file");
            Await(folder.CompareAsync(left, right));
            Check(folder.Entries.Count == 5, "Detached folder comparison failed");
            tabs.SelectedItem = folderItem;
            Await((Task<bool>)Call(workspace, "CloseTabAsync", folderItem)!);
            Check(folder.IsReleased && workspace.ComparisonCount > 0, "Folder close affected file tabs");
            Await((Task)Call(workspace, "CloseTabsAsync", (object)tabs.Items.OfType<TabItem>().ToArray())!);
            Check(!workspace.IsVisible, "Closing all mixed tabs failed");
            Console.WriteLine("PASS folder comparison: recursive listing, new tabs, selection, Undo/save, missing counterpart, refresh, binary, theme, detached workspace, close");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            workspace?.Close();
            main?.Close();
            app.Shutdown();
            Directory.Delete(root, true);
        }
    }
}
