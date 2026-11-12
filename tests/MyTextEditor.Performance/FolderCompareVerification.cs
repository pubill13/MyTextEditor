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
using ProgressBar = System.Windows.Controls.ProgressBar;

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
    private static T? FindVisual<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match)) return match;
            if (FindVisual(child, predicate) is { } found) return found;
        }
        return null;
    }

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

    public static int Run(bool inputOnly = false)
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
            Directory.CreateDirectory(Path.Combine(left, "sub", "deep"));
            File.WriteAllText(Path.Combine(left, "sub", "deep", "grandchild.txt"), "nested file", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(right, "only-right.txt"), "right only", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(left, "binary.bin"), [0, 1, 2, 255]);
            main = new MainWindow { ShowInTaskbar = false };
            typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(main, false);
            Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
            Call(main, "StopFileSync");
            main.Show();
            workspace = (DiffWorkspaceWindow)Call(main, "EnsureDiffWorkspace")!;
            var tabs = Field<TabControl>(workspace, "ComparisonTabs");
            if (inputOnly)
            {
                VerifyPathInput(workspace, tabs, left, right);
                VerifyPathHistory(workspace, tabs, left, right);
                Console.WriteLine("PASS folder path Enter, quoted paths, left/right drops, paired drops, mixed rejection, file buffer preservation and file input regression");
                return 0;
            }
            var folder = workspace.OpenFolderComparison();
            var initial = folder.CompareAsync(left, right);
            Check(((ProgressBar)folder.FindName("CompareProgress")).Visibility == Visibility.Visible, "Progress bar must appear immediately");
            Capture(folder, "loading");
            Await(initial);
            Check(((ProgressBar)folder.FindName("CompareProgress")).Visibility == Visibility.Collapsed, "Completed progress bar not hidden");
            Check(folder.Entries.Count == 4 && folder.Entries.All(entry => !entry.RelativePath.Contains(Path.DirectorySeparatorChar)), "Initial folder comparison must not scan descendants");
            Check(folder.Entries.Single(e => e.RelativePath == "changed.txt").Status == FolderCompareStatus.Different, "Changed row");
            var folderItem = (TabItem)tabs.SelectedItem;
            var list = (DataGrid)folder.FindName("ResultsList");
            var rightList = (DataGrid)folder.FindName("RightResultsList");
            var statusList = (DataGrid)folder.FindName("StatusList");
            var folderPaths = (ComparisonPathBar)folder.FindName("PathBar");
            VerifySorting(folder, list, rightList, statusList);
            Check(list.Items.Cast<FolderCompareTabView.Row>().First().Entry.IsDirectory, "Folders must sort first");
            Check(list.Columns.Count == 4, "Metadata columns missing");
            Call(folder, "Results_Sorting", list, new DataGridSortingEventArgs(list.Columns[3]));
            Check(list.Items.Cast<FolderCompareTabView.Row>().First().Entry.IsDirectory, "Size sort must keep folders first");
            Check(ReferenceEquals(list.ItemsSource, rightList.ItemsSource) && ReferenceEquals(list.ItemsSource, statusList.ItemsSource), "Sorting split paired views");
            Call(folder, "Results_Sorting", list, new DataGridSortingEventArgs(list.Columns[3]));
            Check(list.Items.Cast<FolderCompareTabView.Row>().First().Entry.IsDirectory, "Descending sort must keep folders first");
            Call(folder, "Results_Sorting", list, new DataGridSortingEventArgs(list.Columns[0]));
            Check(list.Columns[0].Header.ToString()!.Contains('↑') && rightList.Columns[0].Header.ToString()!.Contains('↑'), "Name sort indicators must agree");
            folder.UpdateLayout();
            Check(folderPaths.LeftInput.TranslatePoint(new System.Windows.Point(), folder).X < folder.ActualWidth / 2 &&
                folderPaths.RightInput.TranslatePoint(new System.Windows.Point(), folder).X > folder.ActualWidth / 2, "Folder paths must be above their own left/right panes");
            var missingRight = list.Items.Cast<FolderCompareTabView.Row>().Single(row => row.RelativePath == "sub");
            Check(missingRight.LeftName.Length > 0 && missingRight.RightName == "", "Missing right file must be an aligned blank");
            var missingLeft = list.Items.Cast<FolderCompareTabView.Row>().Single(row => row.RelativePath == "only-right.txt");
            Check(missingLeft.LeftName == "" && missingLeft.RightName.Length > 0, "Missing left file must be an aligned blank");
            Capture(folder, "light");
            Call(folder, "Results_Sorting", rightList, new DataGridSortingEventArgs(rightList.Columns[3]));
            var originalRows = folder.Entries;
            var canceled = folder.CompareAsync();
            Call(folder, "Cancel_Click", folder, new RoutedEventArgs());
            Await(canceled);
            Check(folder.Entries.SequenceEqual(originalRows), "Canceled comparison replaced previous results");
            Check(((ProgressBar)folder.FindName("CompareProgress")).Visibility == Visibility.Collapsed, "Canceled progress bar not hidden");
            Await(folder.CompareAsync(Path.Combine(root, "missing"), right));
            Check(folder.Entries.SequenceEqual(originalRows) && ((TextBlock)folder.FindName("SummaryText")).Text.Contains("비교하지 못했습니다"), "Root failure replaced previous results or hid error");
            var superseded = folder.CompareAsync(right, left);
            var latest = folder.CompareAsync(left, right);
            Await(Task.WhenAll(superseded, latest));
            Check(folder.Entries.Single(e => e.RelativePath == "sub").Status == FolderCompareStatus.LeftOnly, "Superseded result overwrote latest comparison");
            Check(rightList.Columns[3].Header.ToString()!.Contains('↑') && Field<string>(folder, "_sortMember") == "RightLength", "Refresh lost tab sort state");
            Call(folder, "Results_Sorting", list, new DataGridSortingEventArgs(list.Columns[0]));
            var selected = list.Items.Cast<FolderCompareTabView.Row>().Single(r => r.RelativePath == "changed.txt");
            list.SelectedItem = selected;
            Check(ReferenceEquals(rightList.SelectedItem, selected) && ReferenceEquals(statusList.SelectedItem, selected), "Paired selection not synchronized");
            var before = workspace.ComparisonCount;
            list.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => workspace.ComparisonCount == before + 1 && !Field<bool>(folder, "_opening"));
            Check(workspace.ComparisonCount == before + 1 && !ReferenceEquals(tabs.SelectedItem, folderItem), "Opening file must add and select a new tab");
            Check(ReferenceEquals(folderItem.Content, folder) && ReferenceEquals(list.SelectedItem, selected), "Folder listing/selection replaced");
            var file = (DiffTabView)((TabItem)tabs.SelectedItem).Content;
            PumpUntil(() => Field<bool>(file, "_initialized"));
            var editor = Field<ScintillaEditorHost>(file, "_leftEditor");
            var filePaths = (ComparisonPathBar)file.FindName("PathBar");
            Check(filePaths.LeftInput.Text == Path.Combine(left, "changed.txt") && filePaths.RightInput.Text == Path.Combine(right, "changed.txt"), "Shared file path header missing source paths");
            Await((Task)Call(file, "LoadPathAsync", true)!);
            Check(editor.GetText().Replace("\r\n", "\n") == "left\ntext", "Typed file path did not load left endpoint");
            filePaths.LeftInput.Text = Path.Combine(left, "does-not-exist.txt");
            Await((Task)Call(file, "LoadPathAsync", true)!);
            Check(editor.GetText().Replace("\r\n", "\n") == "left\ntext", "Invalid path replaced existing comparison");
            filePaths.LeftInput.Text = Path.Combine(left, "changed.txt");
            editor.ReplaceAll("edited\ntext");
            editor.Undo();
            Check(editor.GetText().Replace("\r\n", "\n") == "left\ntext", "File comparison Undo regression");
            editor.Redo();
            Await((Task<bool>)Call(file, "SaveEndpointAsync", true)!);
            Check(File.ReadAllText(Path.Combine(left, "changed.txt")) == editor.GetText(), "File save regression");
            tabs.SelectedItem = folderItem;
            PumpUntil(() => Field<CancellationTokenSource?>(folder, "_operation") is null);
            Check(list.SelectedItem is FolderCompareTabView.Row { RelativePath: "changed.txt" }, "Return to folder lost selection");
            var rootFolder = folder;
            var rootItem = folderItem;
            var rootList = list;
            var rootPaths = folderPaths;
            list.SelectedItem = list.Items.Cast<FolderCompareTabView.Row>().Single(row => row.RelativePath == "sub");
            before = workspace.ComparisonCount;
            Await(folder.OpenEntryAsync(folder.Entries.Single(entry => entry.RelativePath == "sub")));
            Check(workspace.ComparisonCount == before + 1 && !ReferenceEquals(tabs.SelectedItem, rootItem), "Child folder must open a new tab");
            Check(rootPaths.LeftInput.Text == left && rootFolder.Entries.Count == 4, "Child folder replaced parent contents");
            folderItem = (TabItem)tabs.SelectedItem;
            folder = (FolderCompareTabView)folderItem.Content;
            var childFolder = folder;
            list = (DataGrid)folder.FindName("ResultsList");
            folderPaths = (ComparisonPathBar)folder.FindName("PathBar");
            Check(folderPaths.LeftInput.Text == Path.Combine(left, "sub") && folderPaths.RightInput.Text == Path.Combine(right, "sub"), "Child folder paths were not updated together");
            Check(!Directory.Exists(Path.Combine(right, "sub")), "Navigation created a missing folder on disk");
            list.SelectedItem = list.Items.Cast<FolderCompareTabView.Row>().Single(row => row.RelativePath == "deep");
            Await(folder.OpenEntryAsync(folder.Entries.Single(entry => entry.RelativePath == "deep")));
            var grandchildItem = (TabItem)tabs.SelectedItem;
            var grandchild = (FolderCompareTabView)grandchildItem.Content;
            Check(grandchild.Entries.Single().RelativePath == "grandchild.txt" && workspace.ComparisonCount == before + 2, "Grandchild must open its own tab");
            Call(grandchild, "ParentFolder_Click", grandchild, new RoutedEventArgs());
            Check(ReferenceEquals(tabs.SelectedItem, folderItem) && grandchild.Entries.Single().RelativePath == "grandchild.txt", "Parent button must select existing parent without replacing child");
            Check(list.SelectedItem is FolderCompareTabView.Row { RelativePath: "deep" }, "Nested parent return lost selection");
            var oneSided = folder.Entries.Single(e => e.RelativePath == "new.txt");
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
            Call(folder, "ParentFolder_Click", folder, new RoutedEventArgs());
            Check(ReferenceEquals(tabs.SelectedItem, rootItem), "Parent button did not return to root tab");
            Check(folderPaths.LeftInput.Text == Path.Combine(left, "sub"), "Parent button replaced child paths");
            folder = rootFolder;
            folderItem = rootItem;
            list = rootList;
            folderPaths = rootPaths;
            Check(list.SelectedItem is FolderCompareTabView.Row { RelativePath: "sub" }, "Returning to parent lost the folder selection");
            Check(folder.Entries.Single(entry => entry.RelativePath == "sub").Status == FolderCompareStatus.LeftOnly, "Returning replaced parent listing instead of preserving it");
            Await(folder.CompareAsync());
            Check(folder.Entries.Single(entry => entry.RelativePath == "sub").Status == FolderCompareStatus.NotCompared, "Refresh must show newly created counterpart folder without claiming contents are equal");
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
            var oldWidth = workspace.Width;
            workspace.Width = workspace.MinWidth;
            Capture(folder, "minimum-width");
            workspace.Width = oldWidth;
            for (var i = 0; i < 80; i++)
            {
                File.WriteAllText(Path.Combine(left, $"scroll-{i:D3}.txt"), $"row {i}");
                File.WriteAllText(Path.Combine(right, $"scroll-{i:D3}.txt"), $"row {i}");
            }
            Await(folder.CompareAsync(left, right));
            folder.UpdateLayout();
            var leftScroll = (ScrollViewer)CallStaticScrollViewer(list);
            var rightScroll = (ScrollViewer)CallStaticScrollViewer(rightList);
            var statusScroll = (ScrollViewer)CallStaticScrollViewer(statusList);
            rightScroll.ScrollToVerticalOffset(40);
            PumpUntil(() => Math.Abs(leftScroll.VerticalOffset - rightScroll.VerticalOffset) < 0.01 && Math.Abs(statusScroll.VerticalOffset - rightScroll.VerticalOffset) < 0.01 && rightScroll.VerticalOffset > 0);
            leftScroll.ScrollToVerticalOffset(10);
            PumpUntil(() => leftScroll.VerticalOffset == 10 && Math.Abs(rightScroll.VerticalOffset - leftScroll.VerticalOffset) < 0.01 && Math.Abs(statusScroll.VerticalOffset - leftScroll.VerticalOffset) < 0.01);
            Check(leftScroll.VerticalOffset == 10, "Left-originated folder scrolling failed");
            main.Close();
            PumpUntil(() => !app.Windows.OfType<MainWindow>().Contains(main));
            Check(workspace.IsVisible && workspace.Owner is null && !folder.IsReleased, "Main close released mixed workspace");
            File.WriteAllText(Path.Combine(left, "added.txt"), "new external file");
            Await(folder.CompareAsync(left, right));
            Check(folder.Entries.Count == 85, "Detached folder comparison failed");
            tabs.SelectedItem = folderItem;
            Await((Task<bool>)Call(workspace, "CloseTabAsync", folderItem)!);
            Check(folder.IsReleased && workspace.ComparisonCount > 0, "Folder close affected file tabs");
            var childEntries = childFolder.Entries;
            Call(childFolder, "ParentFolder_Click", childFolder, new RoutedEventArgs());
            Check(childFolder.Entries.SequenceEqual(childEntries) && !((System.Windows.Controls.Button)childFolder.FindName("ParentFolderButton")).IsEnabled,
                "Closing parent must leave child usable and disable unavailable parent navigation");
            Await((Task)Call(workspace, "CloseTabsAsync", (object)tabs.Items.OfType<TabItem>().ToArray())!);
            Check(!workspace.IsVisible, "Closing all mixed tabs failed");
            Console.WriteLine("PASS current-folder listing, child and grandchild new tabs, parent tab return and preserved selection, parent close independence, paired selection/scroll, new file tabs, Undo/save, missing counterpart, refresh, binary, theme, detached workspace, close");
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

    private static void VerifyPathInput(DiffWorkspaceWindow workspace, TabControl tabs, string left, string right)
    {
        workspace.Show();
        var file = workspace.OpenEmptyTab();
        PumpUntil(() => Field<bool>(file, "_initialized"));
        workspace.UpdateLayout();
        var bar = (ComparisonPathBar)file.FindName("PathBar");
        bar.LeftInput.Text = $"\"{left}\"";
        var enter = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
        bar.LeftInput.RaiseEvent(enter);
        PumpUntil(() => ((TabItem)tabs.SelectedItem).Content is FolderCompareTabView);
        var folder = (FolderCompareTabView)((TabItem)tabs.SelectedItem).Content;
        var folderBar = (ComparisonPathBar)folder.FindName("PathBar");
        Check(folderBar.LeftInput.Text == left && folderBar.RightInput.Text == "", "First Enter lost folder side");
        Check(!tabs.Items.OfType<TabItem>().Any(item => ReferenceEquals(item.Content, file)), "Empty source tab not replaced");
        folderBar.RightInput.Text = $"\"{right}\"";
        folderBar.RightInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        PumpUntil(() => folder.Entries.Count == 4);

        var folderTab = tabs.SelectedItem;
        var countBefore = tabs.Items.Count;
        folderBar.RightInput.Text = left;
        folderBar.RightInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => Field<CancellationTokenSource?>(folder, "_operation") is null);
        Check(tabs.Items.Count == countBefore && ReferenceEquals(tabs.SelectedItem, folderTab), "Folder path Enter opened another tab");
        Check(folder.Entries.Where(entry => !entry.IsDirectory).All(entry => entry.Status == FolderCompareStatus.Equal), "Folder path Enter did not recompare");
        folderBar.LeftInput.Text = right;
        folderBar.LeftInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => Field<CancellationTokenSource?>(folder, "_operation") is null);
        Check(tabs.Items.Count == countBefore && ReferenceEquals(tabs.SelectedItem, folderTab) && folderBar.RightInput.Text == left,
            "Left folder path Enter changed tabs or the opposite path");
        Await(folder.SetPathsAsync(left, right));

        // Route an actual WPF drop event through the path textbox, which normally consumes drops.
        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { right, left });
        var drop = (System.Windows.DragEventArgs)Activator.CreateInstance(typeof(System.Windows.DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.None, System.Windows.DragDropEffects.Copy, folderBar.LeftInput, new System.Windows.Point()], null)!;
        drop.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent;
        folderBar.LeftInput.RaiseEvent(drop);
        PumpUntil(() => folderBar.LeftInput.Text == right && folderBar.RightInput.Text == left && Field<CancellationTokenSource?>(folder, "_operation") is null);
        Check(folder.Entries.Single(entry => entry.RelativePath == "sub").Status == FolderCompareStatus.RightOnly, "Paired path drop did not compare");
        Await(folder.LoadDroppedPathsAsync(true, [left]));
        Check(folderBar.LeftInput.Text == left, "Left drop targeted wrong side");
        Await(folder.LoadDroppedPathsAsync(false, [right]));
        Check(folderBar.RightInput.Text == right, "Right drop targeted wrong side");
        Await(workspace.OpenDroppedFilesAsync([left, Path.Combine(right, "changed.txt")]));
        Check(((TextBlock)workspace.FindName("StatusText")).Text.Contains("함께 비교할 수 없습니다"), "Mixed input silently accepted");
        Await(workspace.OpenDroppedFilesAsync([right, left]));
        Check(folderBar.LeftInput.Text == right && folderBar.RightInput.Text == left, "Workspace paired drop failed");

        file = workspace.OpenEmptyTab(false);
        PumpUntil(() => Field<bool>(file, "_initialized"));
        Await((Task)Call(file, "LoadDroppedSideAsync", false, (object)new[] { right })!);
        folder = (FolderCompareTabView)((TabItem)tabs.SelectedItem).Content;
        folderBar = (ComparisonPathBar)folder.FindName("PathBar");
        Check(folderBar.RightInput.Text == right && folderBar.LeftInput.Text == "", "Native right-side folder drop lost side");
        Await(folder.LoadDroppedPathsAsync(true, [left]));
        Check(folder.Entries.Count == 4, "Sequential side drops failed");

        file = workspace.OpenEmptyTab(false);
        PumpUntil(() => Field<bool>(file, "_initialized"));
        var editor = Field<ScintillaEditorHost>(file, "_leftEditor");
        Await(file.SetEndpointAsync(true, new DiffEndpoint { Kind = DiffEndpointKind.Scratch, DisplayName = "buffer", Text = "keep this buffer" }));
        bar = (ComparisonPathBar)file.FindName("PathBar");
        bar.RightInput.Text = right;
        Await((Task)Call(file, "LoadDroppedSideAsync", true, (object)new[] { left })!);
        Check(tabs.Items.OfType<TabItem>().Any(item => ReferenceEquals(item.Content, file)) && editor.GetText() == "keep this buffer", "Folder routing discarded file buffer");
        folder = (FolderCompareTabView)((TabItem)tabs.SelectedItem).Content;
        Check(folder.Entries.Count == 4, "Folder routing did not preserve opposite typed path");

        file = workspace.OpenEmptyTab(false);
        PumpUntil(() => Field<bool>(file, "_initialized"));
        bar = (ComparisonPathBar)file.FindName("PathBar");
        bar.LeftInput.Text = Path.Combine(left, "changed.txt");
        Await((Task)Call(file, "LoadPathAsync", true)!);
        Check(file.LeftIsReady && Field<ScintillaEditorHost>(file, "_leftEditor").GetText() == "left\ntext", "File path regression");
        Await(file.SetEndpointAsync(false, LoadEndpoint(Path.Combine(right, "changed.txt"))));
        var fileTab = tabs.SelectedItem;
        countBefore = tabs.Items.Count;
        bar.LeftInput.Text = Path.Combine(right, "changed.txt");
        bar.LeftInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => Field<ScintillaEditorHost>(file, "_leftEditor").GetText() == "right\ntext");
        Check(tabs.Items.Count == countBefore && ReferenceEquals(tabs.SelectedItem, fileTab), "File path Enter opened another tab");
        Check(Field<ScintillaEditorHost>(file, "_rightEditor").GetText() == "right\ntext", "File path Enter changed the opposite side");
        bar.RightInput.Text = Path.Combine(left, "changed.txt");
        bar.RightInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpUntil(() => Field<ScintillaEditorHost>(file, "_rightEditor").GetText() == "left\ntext");
        Check(tabs.Items.Count == countBefore && ReferenceEquals(tabs.SelectedItem, fileTab) &&
            Field<ScintillaEditorHost>(file, "_leftEditor").GetText() == "right\ntext", "Right file path Enter changed tabs or the opposite side");
        bar.RightInput.Text = Path.Combine(right, "missing");
        Await((Task)Call(file, "LoadPathAsync", false)!);
        Check(((TextBlock)file.FindName("StatusText")).Text.Contains("열지 못했습니다"), "Missing path error hidden");
        foreach (var view in tabs.Items.OfType<TabItem>().Select(item => item.Content).OfType<DiffTabView>())
        {
            typeof(DiffTabView).GetField("_leftDirty", Private)!.SetValue(view, false);
            typeof(DiffTabView).GetField("_rightDirty", Private)!.SetValue(view, false);
        }
        DiffEndpoint LoadEndpoint(string path)
        {
            var load = DiffTabView.LoadFileEndpointAsync(path);
            Await(load);
            return load.Result;
        }
    }

    private static void VerifyPathHistory(DiffWorkspaceWindow workspace, TabControl tabs, string left, string right)
    {
        var saved = new UserSettings();
        var bar = (ComparisonPathBar)((DiffTabView)((TabItem)tabs.SelectedItem).Content).FindName("PathBar");
        bar.ConfigureHistory(folders => folders ? saved.RecentComparisonFolders : saved.RecentComparisonFiles,
            (folders, paths) =>
            {
                if (folders) saved.RecentComparisonFolders = ComparisonPathHistory.Remember(saved.RecentComparisonFolders, paths);
                else saved.RecentComparisonFiles = ComparisonPathHistory.Remember(saved.RecentComparisonFiles, paths);
            });
        var path = Path.Combine(left, "changed.txt");
        bar.RememberPaths(path);
        bar.RememberPaths(path.ToUpperInvariant());
        Check(saved.RecentComparisonFiles.Count == 1, "History did not deduplicate Windows paths");
        var button = (System.Windows.Controls.Button)bar.FindName("RightHistoryButton");
        var oldLeft = bar.LeftInput.Text;
        var oldCount = tabs.Items.Count;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var menu = button.ContextMenu!;
        Check(menu.Items.Count == 1, "History menu is missing entries");
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        Check(StringComparer.OrdinalIgnoreCase.Equals(bar.RightInput.Text, path) && bar.LeftInput.Text == oldLeft,
            "History selection changed the opposite input");
        Check(tabs.Items.Count == oldCount, "History selection opened a comparison");
        bar.SetKind("폴더");
        bar.RememberPaths(right);
        Check(saved.RecentComparisonFolders.SequenceEqual([right]) && saved.RecentComparisonFiles.Count == 1, "File/folder history was mixed");
        bar.SetKind("파일");
        var historyFile = Path.Combine(Path.GetDirectoryName(left)!, "history-settings.json");
        File.WriteAllText(historyFile, System.Text.Json.JsonSerializer.Serialize(saved));
        var restored = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(historyFile))!;
        Check(restored.RecentComparisonFiles.SequenceEqual(saved.RecentComparisonFiles) && restored.RecentComparisonFolders.SequenceEqual(saved.RecentComparisonFolders), "History settings round trip failed");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{}")!;
        Check(legacy.RecentComparisonFiles.Count == 0 && legacy.RecentComparisonFolders.Count == 0, "Legacy settings need migration");
        var capped = ComparisonPathHistory.Normalize(Enumerable.Range(0, 40).Select(index => Path.Combine(left, index.ToString())));
        Check(capped.Count == 30, "History limit failed");
        var promoted = ComparisonPathHistory.Remember(capped, [capped[10]]);
        Check(promoted.Count == 30 && promoted[0] == capped[10], "History recency failed");
        Check(ComparisonPathHistory.Normalize(["", "\0", "\"" + path + "\""]).SequenceEqual([path]), "Invalid/quoted history normalization failed");
        Check(bar.LeftInput.FontFamily.Source == "Segoe UI" && bar.LeftInput.FontSize == 13, "Path input inherited editor font settings");
        var submitted = 0;
        void Submitted(bool _) => submitted++;
        bar.SubmitRequested += Submitted;
        var composition = new TextComposition(InputManager.Current, bar.LeftInput, "한");
        bar.LeftInput.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
        bar.LeftInput.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(workspace)!, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(submitted == 0, "IME commit Enter submitted a comparison");
        bar.LeftInput.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        bar.SubmitRequested -= Submitted;
        bar.LeftInput.Text = Path.Combine(left, "한글 경로", "file name.txt");
        Capture(bar, "path-input");
        Console.WriteLine("PASS same-tab file/folder Enter, history selection, recency, limit, separation, settings round trip and legacy settings");
    }

    private static object CallStaticScrollViewer(DataGrid list) => typeof(FolderCompareTabView)
        .GetMethod("FindScrollViewer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [list])!;

    private static void VerifySorting(FolderCompareTabView folder, DataGrid left, DataGrid right, DataGrid status)
    {
        var original = Field<System.Collections.ObjectModel.ObservableCollection<FolderCompareTabView.Row>>(folder, "_rows");
        var now = DateTime.UtcNow;
        FolderCompareTabView.Row Make(string name, FolderCompareStatus state, long? a, long? b, int days, bool directory = false) =>
            new(new(name, name, name, state, a, b, IsDirectory: directory,
                LeftWrittenUtc: state == FolderCompareStatus.RightOnly ? null : now.AddDays(days),
                RightWrittenUtc: state == FolderCompareStatus.LeftOnly ? null : now.AddDays(-days)));
        var rows = new System.Collections.ObjectModel.ObservableCollection<FolderCompareTabView.Row>
        {
            Make("file10.txt", FolderCompareStatus.RightOnly, null, 1, 3),
            Make("file2.bin", FolderCompareStatus.Different, 20, 2, 1),
            Make("file1.txt", FolderCompareStatus.Equal, 10, 30, 2),
            Make("folder10", FolderCompareStatus.NotCompared, null, null, 0, true),
            Make("folder2", FolderCompareStatus.NotCompared, null, null, 0, true)
        };
        try
        {
            Call(folder, "SetRows", rows);
            string Names() => string.Join(",", left.Items.Cast<FolderCompareTabView.Row>().Select(r => r.RelativePath));
            void Sort(DataGrid grid, int column)
            {
                grid.UpdateLayout();
                var header = FindVisual<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid,
                    item => ReferenceEquals(item.Column, grid.Columns[column]));
                Check(header is not null, "Column header is missing");
                typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)
                    .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(header, null);
            }
            void Files(string expected)
            {
                Check(left.Items.Cast<FolderCompareTabView.Row>().Take(2).All(r => r.Entry.IsDirectory), "Sort put files above folders");
                Check(string.Join(",", left.Items.Cast<FolderCompareTabView.Row>().Skip(2).Select(r => r.RelativePath)) == expected, "Unexpected sorted files: " + Names());
                Check(left.Items.Cast<object>().SequenceEqual(right.Items.Cast<object>()) && left.Items.Cast<object>().SequenceEqual(status.Items.Cast<object>()), "Paired sort order diverged");
            }
            Check(Names() == "folder2,folder10,file1.txt,file2.bin,file10.txt", "Natural default ordering failed");
            Sort(left, 0); Files("file10.txt,file2.bin,file1.txt");
            Sort(left, 3); Files("file1.txt,file2.bin,file10.txt");
            Sort(left, 3); Files("file2.bin,file1.txt,file10.txt");
            Sort(right, 3); Files("file10.txt,file2.bin,file1.txt");
            Sort(right, 3); Files("file1.txt,file2.bin,file10.txt");
            Sort(left, 1); Files("file2.bin,file1.txt,file10.txt");
            Sort(left, 1); Files("file1.txt,file2.bin,file10.txt");
            Sort(right, 1); Files("file10.txt,file1.txt,file2.bin");
            Sort(right, 1); Files("file2.bin,file1.txt,file10.txt");
            Sort(left, 2); Files("file2.bin,file1.txt,file10.txt");
            Sort(left, 2); Files("file1.txt,file2.bin,file10.txt");
            Sort(right, 2); Files("file2.bin,file1.txt,file10.txt");
            Sort(right, 2); Files("file1.txt,file10.txt,file2.bin");
            Sort(status, 0); Files("file1.txt,file2.bin,file10.txt");
            Sort(status, 0); Files("file10.txt,file2.bin,file1.txt");
            var filter = (System.Windows.Controls.CheckBox)folder.FindName("DifferencesOnly");
            filter.IsChecked = true;
            Check(left.Items.Cast<FolderCompareTabView.Row>().All(r => r.Status != FolderCompareStatus.Equal), "Sorted filter retained equal rows");
            filter.IsChecked = false;
            Sort(left, 0);
            Check(Names() == "folder2,folder10,file1.txt,file2.bin,file10.txt", "Name sort reset failed");
            var selected = rows[1];
            left.SelectedItem = selected;
            Sort(left, 3);
            Check(ReferenceEquals(left.SelectedItem, selected) && ReferenceEquals(right.SelectedItem, selected), "Sorting lost selection");
            Check(rows[1].LeftSize == "20.0 B" || rows[1].LeftSize == "20,0 B", "Readable size missing");
            Check(rows[1].Detail.Contains("20") && rows[1].Detail.Contains("바이트"), "Exact byte tooltip missing");
        }
        finally
        {
            Call(folder, "SetRows", original);
            // Use a different column first so returning to name always selects ascending order.
            Call(folder, "Results_Sorting", status, new DataGridSortingEventArgs(status.Columns[0]));
            Call(folder, "Results_Sorting", left, new DataGridSortingEventArgs(left.Columns[0]));
        }
    }

    public static int RunStress(bool baseline, int count = 10000, string fixture = "empty")
    {
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL folder comparison exceeded 90 seconds");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Dark").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var root = Path.Combine(Path.GetTempPath(), "OmniEdit-FolderStress-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false), SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (_, _) => Task.CompletedTask, SettingsChanged = _ => { }
        };
        var window = new DiffWorkspaceWindow(new(), callbacks, new("Consolas", 11, true));
        var folder = window.OpenFolderComparison();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
        var elapsed = Stopwatch.StartNew();
        double lastTick = 0, maxUiGap = 0;
        var ticks = 0;
        heartbeat.Tick += (_, _) => { var now = elapsed.Elapsed.TotalMilliseconds; maxUiGap = Math.Max(maxUiGap, now - lastTick); lastTick = now; ticks++; };
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (fixture == "large")
                {
                    using var a = File.Create(Path.Combine(left, $"item-{i:D5}.txt"));
                    using var b = File.Create(Path.Combine(right, $"item-{i:D5}.txt"));
                    a.SetLength(64L * 1024 * 1024); b.SetLength(64L * 1024 * 1024);
                }
                else
                {
                    var text = fixture == "small" ? new string('x', 1024) : "";
                    File.WriteAllText(Path.Combine(left, $"item-{i:D5}.txt"), text);
                    File.WriteAllText(Path.Combine(right, $"item-{i:D5}.txt"), text);
                }
            }
            var contentBytes = count * (fixture == "large" ? 64L * 1024 * 1024 : fixture == "small" ? 1024L : 0L);
            Console.WriteLine($"Fixture ready: {count:N0} paired {fixture} files ({contentBytes:N0} bytes per side)");
            window.Show(); window.UpdateLayout();
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            elapsed.Restart(); heartbeat.Start();
            var comparing = folder.CompareAsync(left, right);
            while (!comparing.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(75))
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(comparing.IsCompleted, "Folder comparison exceeded 75 seconds");
            comparing.GetAwaiter().GetResult();
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            heartbeat.Stop();
            maxUiGap = Math.Max(maxUiGap, elapsed.Elapsed.TotalMilliseconds - lastTick);
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            var result = new { Files = count, ContentBytes = contentBytes, Milliseconds = elapsed.Elapsed.TotalMilliseconds, MaximumUiGapMs = maxUiGap, Heartbeats = ticks, AllocatedBytes = allocated, PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, Rows = folder.Entries.Count };
            Directory.CreateDirectory("artifacts/folder-compare");
            File.WriteAllText($"artifacts/folder-compare/stress-{fixture}-{(baseline ? "before" : "after")}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json", System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            Check(folder.Entries.Count == count && folder.Entries.All(entry => entry.Status == FolderCompareStatus.Equal), "Large folder result mismatch");
            Check(maxUiGap < 1500, $"UI blocked for {maxUiGap:N0}ms while comparing empty files");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { heartbeat.Stop(); window.Close(); app.Shutdown(); Directory.Delete(root, true); }
    }

    public static int RunDemo(string root)
    {
        var left = Path.GetFullPath(Path.Combine(root, "left"));
        var right = Path.GetFullPath(Path.Combine(root, "right"));
        if (!Directory.Exists(left) || !Directory.Exists(right)) throw new DirectoryNotFoundException("시연할 left / right 폴더가 필요합니다.");
        var app = new TestApp { ShutdownMode = ShutdownMode.OnLastWindowClose };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var palette = ThemePalette.Get("Dark");
        app.Resources.MergedDictionaries.Add(palette.CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var main = new MainWindow();
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(main, false);
        Field<DispatcherTimer>(main, "_settingsSaveTimer").Stop();
        Call(main, "StopFileSync");
        main.Show();
        var workspace = (DiffWorkspaceWindow)Call(main, "EnsureDiffWorkspace")!;
        workspace.Title = "비교 / 병합 — 좌우 폴더·파일 비교 시연";
        workspace.Resources.MergedDictionaries.Add(palette.CreateResources());
        workspace.ApplyAppearance(new DiffAppearance("Consolas", 13, true) { Palette = palette });
        var folder = workspace.OpenFolderComparison();
        workspace.Activate();
        workspace.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await folder.CompareAsync(left, right);
            Capture(folder, "split-demo");
        }));
        Console.WriteLine($"시연 경로: {left} ↔ {right}");
        return app.Run();
    }
}
