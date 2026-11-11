using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MyTextEditor.Core;

namespace MyTextEditor.Diff;

public partial class FolderCompareTabView : System.Windows.Controls.UserControl
{
    public sealed record Row(FolderCompareEntry Entry)
    {
        public string RelativePath => Entry.RelativePath;
        public string LeftName => Status == FolderCompareStatus.RightOnly ? "" : (Entry.IsDirectory ? "📁 " : "") + RelativePath;
        public string RightName => Status == FolderCompareStatus.LeftOnly ? "" : (Entry.IsDirectory ? "📁 " : "") + RelativePath;
        public string Symbol => Status switch
        {
            FolderCompareStatus.Equal => "=", FolderCompareStatus.Different => "≠",
            FolderCompareStatus.LeftOnly => "←", FolderCompareStatus.RightOnly => "→",
            FolderCompareStatus.NotCompared => "▸", _ => "!"
        };
        public FolderCompareStatus Status => Entry.Status;
        public long? LeftLength => Entry.LeftLength;
        public long? RightLength => Entry.RightLength;
        public string StatusText => Status switch
        {
            FolderCompareStatus.Equal => "동일", FolderCompareStatus.Different => "내용 다름",
            FolderCompareStatus.LeftOnly => "왼쪽에만 있음", FolderCompareStatus.RightOnly => "오른쪽에만 있음",
            FolderCompareStatus.NotCompared => "폴더 · 열어서 비교", _ => "오류"
        };
        public string Detail => $"{StatusText}\n{Entry.Error ?? $"{Entry.LeftPath}\n{Entry.RightPath}"}\n더블클릭: {(Entry.IsDirectory ? "새 폴더 비교 탭" : "새 파일 비교 탭")}";
    }

    private readonly FolderCompareService _service = new();
    private ObservableCollection<Row> _rows = [];
    private sealed record FolderLocation(string Left, string Right, bool AllowMissing);
    private FolderLocation? _location;
    private Func<bool>? _selectParentTab;
    private readonly HashSet<string> _openedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<FolderCompareTabView, FolderCompareEntry, Task> _openEntry;
    private CancellationTokenSource? _operation;
    private bool _released;
    private bool _opening;
    private bool _syncingSelection;
    private bool _syncingScroll;
    private IEnumerable<DataGrid> Lists => new[] { ResultsList, StatusList, RightResultsList };
    private System.Windows.Controls.TextBox LeftPathInput => PathBar.LeftInput;
    private System.Windows.Controls.TextBox RightPathInput => PathBar.RightInput;
    public bool IsReleased => _released;
    public string TabTitle { get; private set; } = "폴더 비교";
    public event EventHandler? StateChanged;
    public IReadOnlyList<FolderCompareEntry> Entries => _rows.Select(row => row.Entry).ToArray();

    public FolderCompareTabView(Func<FolderCompareTabView, FolderCompareEntry, Task> openEntry)
    {
        InitializeComponent();
        _openEntry = openEntry;
        PathBar.SetKind("폴더");
        PathBar.SubmitRequested += async _ => await CompareAsync();
        PathBar.BrowseRequested += left => Browse(left ? LeftPathInput : RightPathInput);
        PathBar.PathsDropped += async (left, paths) => await LoadDroppedPathsAsync(left, paths);
        SetRows(_rows);
    }

    public async Task SetPathsAsync(string left, string right)
    {
        LeftPathInput.Text = ComparisonPathBar.NormalizePath(left);
        RightPathInput.Text = ComparisonPathBar.NormalizePath(right);
        await CompareAsync();
    }

    public async Task OpenChildAsync(string left, string right, Func<bool> selectParentTab)
    {
        await CompareCoreAsync(left, right, true, true);
        if (_released) return;
        _selectParentTab = selectParentTab;
        ParentFolderButton.IsEnabled = true;
    }

    public async Task LoadDroppedPathsAsync(bool? left, IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > 2 || paths.Any(path => !Directory.Exists(path)))
        {
            SummaryText.Text = "비교할 폴더를 한 개 또는 두 개 놓으세요. 파일과 폴더를 함께 비교할 수 없습니다.";
            return;
        }
        if (paths.Count == 2) await SetPathsAsync(paths[0], paths[1]);
        else if (left ?? string.IsNullOrWhiteSpace(LeftPathInput.Text))
            await SetPathsAsync(paths[0], RightPathInput.Text);
        else await SetPathsAsync(LeftPathInput.Text, paths[0]);
    }

    private void Folder_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths &&
            paths.Length is 1 or 2 && paths.All(Directory.Exists)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Folder_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            await LoadDroppedPathsAsync(e.GetPosition(this).X < ActualWidth / 2, paths);
    }

    public void ApplyAppearance(DiffAppearance appearance)
    {
        foreach (var list in Lists)
        {
            list.FontSize = appearance.FontSize;
            list.RowHeight = Math.Max(32, Math.Ceiling(appearance.FontSize * 1.5 + 8));
        }
        var dark = appearance.Palette?.IsDark ?? appearance.DarkTheme;
        Resources["FolderChangedBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#E0BC60" : "#8A6200"));
        Resources["FolderDeletedBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#FF8C94" : "#B63744"));
        Resources["FolderAddedBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#72D39B" : "#257244"));
    }

    public async Task CompareAsync(string? left = null, string? right = null)
    {
        var leftPath = ComparisonPathBar.NormalizePath(left ?? LeftPathInput.Text);
        var rightPath = ComparisonPathBar.NormalizePath(right ?? RightPathInput.Text);
        var sameLocation = _location is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(leftPath.Trim(), _location.Left) &&
            StringComparer.OrdinalIgnoreCase.Equals(rightPath.Trim(), _location.Right);
        await CompareCoreAsync(leftPath, rightPath, sameLocation && _location!.AllowMissing, !sameLocation);
    }

    private async Task<bool> CompareCoreAsync(string left, string right, bool allowMissing, bool resetHistory)
    {
        if (_released) return false;
        LeftPathInput.Text = left;
        RightPathInput.Text = right;
        if (string.IsNullOrWhiteSpace(LeftPathInput.Text) || string.IsNullOrWhiteSpace(RightPathInput.Text))
        {
            SummaryText.Text = "왼쪽과 오른쪽 폴더 경로를 모두 입력하세요.";
            return false;
        }
        var operation = StartOperation();
        SummaryText.Text = "현재 폴더의 항목을 비교하는 중…";
        try
        {
            var leftRoot = Path.GetFullPath(LeftPathInput.Text.Trim());
            var rightRoot = Path.GetFullPath(RightPathInput.Text.Trim());
            var lastProgress = Environment.TickCount64;
            var progress = new Progress<FolderCompareProgress>(value =>
            {
                if (IsCurrent(operation) && Environment.TickCount64 - lastProgress >= 100)
                {
                    lastProgress = Environment.TickCount64;
                    SummaryText.Text = $"{value.ComparedFiles:N0}개 비교 · {value.RelativePath}";
                }
            });
            var entries = await _service.CompareAsync(leftRoot, rightRoot, progress, operation.Token, allowMissing);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(operation)) return false;
            // Bind once; per-item collection notifications stall all three grids on large folders.
            SetRows(new ObservableCollection<Row>(entries.Select(entry => new Row(entry))));
            _openedPaths.Clear();
            if (resetHistory) _selectParentTab = null;
            _location = new(leftRoot, rightRoot, allowMissing);
            LeftPathInput.Text = leftRoot;
            RightPathInput.Text = rightRoot;
            ParentFolderButton.IsEnabled = _selectParentTab is not null;
            TabTitle = $"폴더: {Path.GetFileName(Path.TrimEndingDirectorySeparator(leftRoot))} ↔ {Path.GetFileName(Path.TrimEndingDirectorySeparator(rightRoot))}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            UpdateSummary();
            return true;
        }
        catch (OperationCanceledException) { if (IsCurrent(operation)) SummaryText.Text = "비교를 취소했습니다. 이전 결과를 유지합니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { if (IsCurrent(operation)) SummaryText.Text = $"폴더를 비교하지 못했습니다: {ex.Message}"; }
        finally { EndOperation(operation); }
        return false;
    }

    public async Task RefreshOpenedFilesAsync()
    {
        if (_released || _operation is not null || _openedPaths.Count == 0) return;
        var operation = StartOperation();
        try
        {
            foreach (var path in _openedPaths.ToArray())
            {
                var index = _rows.ToList().FindIndex(row => StringComparer.OrdinalIgnoreCase.Equals(row.RelativePath, path));
                if (index < 0) continue;
                var old = _rows[index].Entry;
                var updated = await Task.Run(() => _service.CompareFileAsync(path, old.LeftPath, old.RightPath, operation.Token), operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(operation)) return;
                var selected = ReferenceEquals(ResultsList.SelectedItem, _rows[index]);
                _rows[index] = new(updated);
                if (selected) ResultsList.SelectedItem = _rows[index];
            }
            UpdateSummary();
        }
        catch (OperationCanceledException) { }
        finally { EndOperation(operation); }
    }

    public async Task OpenEntryAsync(FolderCompareEntry entry)
    {
        if (_released || _opening) return;
        if (entry.Status == FolderCompareStatus.Error) { SummaryText.Text = entry.Error; return; }
        _opening = true;
        try
        {
            await _openEntry(this, entry);
            if (!_released && !entry.IsDirectory) _openedPaths.Add(entry.RelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or ArgumentException or System.Security.SecurityException)
        { if (!_released) SummaryText.Text = $"{(entry.IsDirectory ? "폴더" : "파일")} 비교를 열지 못했습니다: {ex.Message}"; }
        finally { _opening = false; }
    }

    public void ReleaseResources()
    {
        _released = true;
        _operation?.Cancel();
    }

    private CancellationTokenSource StartOperation()
    {
        _operation?.Cancel();
        _operation = new();
        CancelButton.IsEnabled = true;
        return _operation;
    }

    private bool IsCurrent(CancellationTokenSource operation) => !_released && ReferenceEquals(_operation, operation);
    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operation, operation)) { _operation = null; CancelButton.IsEnabled = false; }
        operation.Dispose();
    }

    private void SetRows(ObservableCollection<Row> rows)
    {
        _syncingSelection = true;
        _syncingScroll = true;
        try
        {
            _rows = rows;
            var view = CollectionViewSource.GetDefaultView(rows);
            view.Filter = DifferencesOnly.IsChecked == true ? item => ((Row)item).Status != FolderCompareStatus.Equal : null;
            foreach (var list in Lists) list.ItemsSource = view;
        }
        finally { _syncingSelection = false; _syncingScroll = false; }
    }

    private void ParentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectParentTab?.Invoke() == true) return;
        _selectParentTab = null;
        ParentFolderButton.IsEnabled = false;
        SummaryText.Text = "상위 폴더 탭이 닫혔습니다. 현재 비교는 계속 사용할 수 있습니다.";
    }

    private void UpdateSummary() => SummaryText.Text = $"파일 {_rows.Count(row => !row.Entry.IsDirectory):N0}개 · 폴더 {_rows.Count(row => row.Entry.IsDirectory):N0}개 · 동일 {_rows.Count(r => r.Status == FolderCompareStatus.Equal):N0} · 차이 {_rows.Count(r => r.Status is not (FolderCompareStatus.Equal or FolderCompareStatus.Error or FolderCompareStatus.NotCompared)):N0} · 오류 {_rows.Count(r => r.Status == FolderCompareStatus.Error):N0} · 폴더는 열 때 비교합니다";
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(_rows);
        if (view is not null) view.Filter = DifferencesOnly.IsChecked == true ? item => ((Row)item).Status != FolderCompareStatus.Equal : null;
    }
    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void Results_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Enter && sender is DataGrid { SelectedItem: Row row }) { e.Handled = true; await OpenEntryAsync(row.Entry); } }
    private async void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid list && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(list, source) is DataGridRow { Item: Row row })
        { e.Handled = true; await OpenEntryAsync(row.Entry); }
    }
    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || sender is not DataGrid source) return;
        _syncingSelection = true;
        try { foreach (var list in Lists.Where(list => list is not null && list != source)) list.SelectedItem = source.SelectedItem; }
        finally { _syncingSelection = false; }
    }

    private void Results_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || e.VerticalChange == 0 || e.OriginalSource is not ScrollViewer source) return;
        _syncingScroll = true;
        try
        {
            foreach (var list in Lists.Where(list => list is not null && list != sender))
                FindScrollViewer(list)?.ScrollToVerticalOffset(source.VerticalOffset);
        }
        finally { _syncingScroll = false; }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private void Browse(System.Windows.Controls.TextBox input)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "비교할 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FolderName;
    }
}
