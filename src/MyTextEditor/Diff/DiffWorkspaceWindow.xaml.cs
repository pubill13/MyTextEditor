using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MyTextEditor.Diff;

public partial class DiffWorkspaceWindow : Window
{
    private readonly DiffWindowCallbacks _callbacks;
    private DiffWindowOptions _defaults;
    private DiffAppearance _appearance;
    private bool _closingApproved;
    private bool _closePromptRunning;
    private bool _resourcesReleased;

    public DiffWorkspaceWindow(DiffWindowOptions options, DiffWindowCallbacks callbacks, DiffAppearance appearance)
    {
        InitializeComponent();
        _defaults = options;
        _callbacks = callbacks;
        _appearance = appearance;
        Width = Math.Max(MinWidth, options.Width);
        Height = Math.Max(MinHeight, options.Height);
        if (options.Left is { } left && options.Top is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    public event EventHandler? ResourcesReleased;
    public bool HasUnsavedChanges => Views.Any(view => view.HasUnsavedChanges);
    public int ComparisonCount => ComparisonTabs.Items.Count;
    private IEnumerable<DiffTabView> Views => ComparisonTabs.Items.OfType<TabItem>().Select(item => (DiffTabView)item.Content);
    private DiffTabView? CurrentView => (ComparisonTabs.SelectedItem as TabItem)?.Content as DiffTabView;

    public DiffTabView OpenEmptyTab(bool reuseExisting = true)
    {
        if (reuseExisting)
        {
            var existing = Views.FirstOrDefault(view => view.IsCompletelyEmpty);
            if (existing is not null) { Select(existing); return existing; }
        }
        return AddTab(DiffEndpoint.Empty("왼쪽"), DiffEndpoint.Empty("오른쪽"));
    }

    public DiffTabView OpenComparison(DiffEndpoint left, DiffEndpoint right) => AddTab(left, right);

    public Task OpenDroppedFilesAsync(IReadOnlyList<string> paths) => LoadDroppedFilesAsync(paths);

    public void ApplyAppearance(DiffAppearance appearance)
    {
        var dpiScale = IsLoaded ? (float)VisualTreeHelper.GetDpi(this).DpiScaleX : appearance.DpiScale;
        _appearance = appearance with { DpiScale = dpiScale };
        foreach (var view in Views) view.ApplyAppearance(_appearance);
    }

    public void RefreshSourceStates()
    {
        foreach (var view in Views) view.RefreshSourceState();
    }

    public void CancelPreparedClose()
    {
        if (!_resourcesReleased) _closingApproved = false;
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_closingApproved) return true;
        var views = Views.ToArray();
        foreach (var view in views)
            if (!await view.RequestCloseAsync()) return false;
        _closingApproved = true;
        return true;
    }

    private DiffTabView AddTab(DiffEndpoint left, DiffEndpoint right)
    {
        var view = new DiffTabView(left, right, _defaults, _callbacks, _appearance);
        var headerText = new TextBlock { Text = view.TabTitle, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new System.Windows.Controls.Button { Content = "×", Width = 23, Height = 23, Padding = new Thickness(0), Margin = new Thickness(7, 0, 0, 0), ToolTip = "비교 탭 닫기" };
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        header.Children.Add(headerText); header.Children.Add(close);
        var item = new TabItem { Header = header, Content = view, Tag = headerText };
        close.Click += async (_, e) => { e.Handled = true; await CloseTabAsync(item); };
        view.StateChanged += (_, _) => headerText.Text = view.TabTitle;
        view.OptionsChanged += (_, _) => UpdateDefaults(view.GetOptions());
        view.NewComparisonRequested += (_, e) => OpenComparison(e.Left, e.Right);
        view.FilesDropped += async (_, e) => await LoadDroppedFilesAsync(e.Paths);
        view.CloseRequested += async (_, _) => await CloseTabAsync(item);
        view.CycleTabRequested += (_, e) => CycleTab(e.Direction);
        ComparisonTabs.Items.Add(item);
        ComparisonTabs.SelectedItem = item;
        StatusText.Text = view.IsReady ? "비교 탭을 열었습니다." : "좌우에 파일을 놓거나 소스를 선택하세요.";
        return view;
    }

    private void Select(DiffTabView view)
    {
        ComparisonTabs.SelectedItem = ComparisonTabs.Items.OfType<TabItem>().First(item => ReferenceEquals(item.Content, view));
    }

    private void UpdateDefaults(DiffWindowOptions options)
    {
        _defaults = _defaults with
        {
            IgnoreWhitespace = options.IgnoreWhitespace, IgnoreCase = options.IgnoreCase,
            IgnoreEmptyLines = options.IgnoreEmptyLines, ScrollSync = options.ScrollSync
        };
        SaveWindowOptions();
    }

    private async Task<bool> CloseTabAsync(TabItem item)
    {
        var view = (DiffTabView)item.Content;
        if (!await view.RequestCloseAsync()) return false;
        view.ReleaseResources();
        ComparisonTabs.Items.Remove(item);
        if (ComparisonTabs.Items.Count == 0) Close();
        return true;
    }

    private async Task CloseTabsAsync(IReadOnlyList<TabItem> items)
    {
        var views = items.Select(item => (DiffTabView)item.Content).ToArray();
        foreach (var view in views)
            if (!await view.RequestCloseAsync()) return;
        foreach (var item in items)
        {
            ((DiffTabView)item.Content).ReleaseResources();
            ComparisonTabs.Items.Remove(item);
        }
        if (ComparisonTabs.Items.Count == 0) Close();
    }

    private async Task LoadDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(path => !Directory.Exists(path)).Take(2).ToArray();
        var excluded = paths.Count - files.Length;
        if (files.Length == 0) { StatusText.Text = "폴더는 열지 않습니다. 비교할 파일을 놓으세요."; return; }
        if (files.Length == 1)
        {
            var view = CurrentView ?? OpenEmptyTab();
            if (!view.IsReady)
            {
                BusyProgress.Visibility = Visibility.Visible;
                StatusText.Text = "파일을 불러오는 중…";
                try
                {
                    var endpoint = await DiffTabView.LoadFileEndpointAsync(files[0]);
                    await view.SetEndpointAsync(!view.LeftIsReady, endpoint);
                }
                catch (Exception exception) { StatusText.Text = $"파일을 열지 못했습니다: {exception.Message}"; }
                finally { BusyProgress.Visibility = Visibility.Collapsed; }
            }
            else StatusText.Text = "한 파일은 왼쪽 또는 오른쪽 영역에 직접 놓으세요.";
            if (excluded > 0) StatusText.Text += $" {excluded}개 항목은 제외했습니다.";
            return;
        }

        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = "두 파일을 불러오는 중…";
        try
        {
            var endpoints = await Task.WhenAll(files.Select(DiffTabView.LoadFileEndpointAsync));
            var view = Views.FirstOrDefault(candidate => candidate.IsCompletelyEmpty) ?? AddTab(DiffEndpoint.Empty("왼쪽"), DiffEndpoint.Empty("오른쪽"));
            Select(view);
            await view.SetEndpointAsync(true, endpoints[0], false);
            await view.SetEndpointAsync(false, endpoints[1], false);
            StatusText.Text = excluded > 0 ? $"두 파일을 비교했습니다. {excluded}개 항목은 제외했습니다." : "두 파일을 비교했습니다.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"두 파일을 열지 못해 기존 비교를 유지했습니다: {exception.Message}";
        }
        finally { BusyProgress.Visibility = Visibility.Collapsed; }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e) => OpenEmptyTab(false);
    private async void CloseOthers_Click(object sender, RoutedEventArgs e)
    {
        var current = ComparisonTabs.SelectedItem;
        await CloseTabsAsync(ComparisonTabs.Items.OfType<TabItem>().Where(item => !ReferenceEquals(item, current)).ToArray());
    }
    private async void CloseAll_Click(object sender, RoutedEventArgs e) => await CloseTabsAsync(ComparisonTabs.Items.OfType<TabItem>().ToArray());

    private void ComparisonTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentView is { } view) StatusText.Text = view.IsReady ? view.TabTitle : "좌우에 파일을 놓거나 소스를 선택하세요.";
    }

    private static bool TryGetFiles(System.Windows.DragEventArgs e, out IReadOnlyList<string> paths)
    {
        paths = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] items ? items : Array.Empty<string>();
        return paths.Count > 0;
    }
    private void Window_DragOver(object sender, System.Windows.DragEventArgs e) { e.Effects = TryGetFiles(e, out _) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, System.Windows.DragEventArgs e) { if (TryGetFiles(e, out var paths)) await LoadDroppedFilesAsync(paths); e.Handled = true; }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F1 && modifiers == ModifierKeys.None) { _callbacks.ShowHelp?.Invoke(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && e.Key is Key.W or Key.F4)
        {
            if (ComparisonTabs.SelectedItem is TabItem item) await CloseTabAsync(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { CycleTab(-1); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.Tab or Key.PageDown) { CycleTab(1); e.Handled = true; }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.PageUp) { CycleTab(-1); e.Handled = true; }
    }

    private void CycleTab(int delta)
    {
        if (ComparisonTabs.Items.Count == 0) return;
        ComparisonTabs.SelectedIndex = (ComparisonTabs.SelectedIndex + delta + ComparisonTabs.Items.Count) % ComparisonTabs.Items.Count;
    }

    private void Window_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e) => ApplyAppearance(_appearance with { DpiScale = (float)e.NewDpi.DpiScaleX });

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApproved) { ReleaseResources(); return; }
        if (_closePromptRunning) { e.Cancel = true; return; }
        if (!HasUnsavedChanges) { ReleaseResources(); return; }
        e.Cancel = true;
        _closePromptRunning = true;
        try { if (await RequestCloseAsync()) Close(); }
        finally { _closePromptRunning = false; }
    }

    private void ReleaseResources()
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        foreach (var view in Views.ToArray()) view.ReleaseResources();
        ResourcesReleased?.Invoke(this, EventArgs.Empty);
        ResourcesReleased = null;
    }

    private void Window_PlacementChanged(object? sender, EventArgs e) { if (IsLoaded) SaveWindowOptions(); }
    private void SaveWindowOptions()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _callbacks.SettingsChanged(_defaults with { Width = bounds.Width, Height = bounds.Height, Left = bounds.Left, Top = bounds.Top });
    }
}


