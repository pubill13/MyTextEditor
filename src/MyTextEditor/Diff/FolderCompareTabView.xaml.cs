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
        public FolderCompareStatus Status => Entry.Status;
        public long? LeftLength => Entry.LeftLength;
        public long? RightLength => Entry.RightLength;
        public string StatusText => Status switch
        {
            FolderCompareStatus.Equal => "동일", FolderCompareStatus.Different => "내용 다름",
            FolderCompareStatus.LeftOnly => "왼쪽에만 있음", FolderCompareStatus.RightOnly => "오른쪽에만 있음", _ => "오류"
        };
        public string Detail => Entry.Error ?? $"{Entry.LeftPath}\n{Entry.RightPath}\n더블클릭: 새 파일 비교 탭";
    }

    private readonly FolderCompareService _service = new();
    private readonly ObservableCollection<Row> _rows = [];
    private readonly HashSet<string> _openedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<FolderCompareTabView, FolderCompareEntry, Task> _openFile;
    private CancellationTokenSource? _operation;
    private bool _released;
    private bool _opening;
    public bool IsReleased => _released;
    public string TabTitle { get; private set; } = "폴더 비교";
    public event EventHandler? StateChanged;
    public IReadOnlyList<FolderCompareEntry> Entries => _rows.Select(row => row.Entry).ToArray();

    public FolderCompareTabView(Func<FolderCompareTabView, FolderCompareEntry, Task> openFile)
    {
        InitializeComponent();
        _openFile = openFile;
        ResultsList.ItemsSource = _rows;
        CollectionViewSource.GetDefaultView(_rows).Filter = item => DifferencesOnly.IsChecked != true || ((Row)item).Status != FolderCompareStatus.Equal;
    }

    public void ApplyAppearance(DiffAppearance appearance)
    {
        ResultsList.FontSize = appearance.FontSize;
        var dark = appearance.Palette?.IsDark ?? appearance.DarkTheme;
        Resources["FolderChangedBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#E0BC60" : "#8A6200"));
        Resources["FolderDeletedBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#FF8C94" : "#B63744"));
        Resources["FolderAddedBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? "#72D39B" : "#257244"));
    }

    public async Task CompareAsync(string? left = null, string? right = null)
    {
        if (_released) return;
        if (left is not null) LeftPathInput.Text = left;
        if (right is not null) RightPathInput.Text = right;
        if (string.IsNullOrWhiteSpace(LeftPathInput.Text) || string.IsNullOrWhiteSpace(RightPathInput.Text))
        {
            SummaryText.Text = "왼쪽과 오른쪽 폴더 경로를 모두 입력하세요.";
            return;
        }
        var operation = StartOperation();
        SummaryText.Text = "하위 폴더를 포함해 비교하는 중…";
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
            var entries = await _service.CompareAsync(leftRoot, rightRoot, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(operation)) return;
            _rows.Clear();
            foreach (var entry in entries) _rows.Add(new(entry));
            _openedPaths.Clear();
            TabTitle = $"폴더: {Path.GetFileName(Path.TrimEndingDirectorySeparator(leftRoot))} ↔ {Path.GetFileName(Path.TrimEndingDirectorySeparator(rightRoot))}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            UpdateSummary();
        }
        catch (OperationCanceledException) { if (IsCurrent(operation)) SummaryText.Text = "비교를 취소했습니다. 이전 결과를 유지합니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { if (IsCurrent(operation)) SummaryText.Text = $"폴더를 비교하지 못했습니다: {ex.Message}"; }
        finally { EndOperation(operation); }
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
            await _openFile(this, entry);
            if (!_released) _openedPaths.Add(entry.RelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or ArgumentException or System.Security.SecurityException)
        { if (!_released) SummaryText.Text = $"파일 비교를 열지 못했습니다: {ex.Message}"; }
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

    private void UpdateSummary() => SummaryText.Text = $"전체 {_rows.Count:N0}개 · 동일 {_rows.Count(r => r.Status == FolderCompareStatus.Equal):N0} · 차이 {_rows.Count(r => r.Status is not (FolderCompareStatus.Equal or FolderCompareStatus.Error)):N0} · 오류 {_rows.Count(r => r.Status == FolderCompareStatus.Error):N0} · 더블클릭: 새 파일 비교 탭";
    private void Filter_Changed(object sender, RoutedEventArgs e) => CollectionViewSource.GetDefaultView(_rows)?.Refresh();
    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void Path_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await CompareAsync(); } }
    private async void Results_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Enter && ResultsList.SelectedItem is Row row) { e.Handled = true; await OpenEntryAsync(row.Entry); } }
    private async void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultsList, source) is DataGridRow { Item: Row row })
        { e.Handled = true; await OpenEntryAsync(row.Entry); }
    }
    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftPathInput);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightPathInput);
    private void Browse(System.Windows.Controls.TextBox input)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "비교할 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FolderName;
    }
}
