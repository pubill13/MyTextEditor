using System.IO;
using System.Windows;
using System.Windows.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Models;
using ListBox = System.Windows.Controls.ListBox;
using Forms = System.Windows.Forms;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace MyTextEditor;

public partial class MainWindow
{
    private readonly FolderTextSearchService _folderSearch = new();
    private CancellationTokenSource? _folderSearchCancellation;

    private async void DocumentTabMenuClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentViewModel document)
            await CloseDocumentAsync(document);
    }

    private async void DocumentTabMenuCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentViewModel keep)
            await CloseDocumentSetAsync(Documents.Where(document => document != keep).ToArray());
    }

    private async void DocumentTabMenuCloseAll_Click(object sender, RoutedEventArgs e) =>
        await CloseDocumentSetAsync(Documents.ToArray());

    private void DocumentTabMenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DocumentViewModel { FilePath: { } path })
            Clipboard.SetText(path);
    }

    private async Task CloseDocumentSetAsync(IReadOnlyList<DocumentViewModel> targets)
    {
        if (_closingAllDocuments || _closingDocuments.Count > 0) return;
        _closingAllDocuments = true;
        try
        {
            foreach (var document in targets.Where(document => document.IsModified))
            {
                DocumentTabs.SelectedItem = document;
                var answer = MessageBox.Show(this, $"'{document.DisplayName}'의 변경 내용을 저장할까요?",
                    "탭 닫기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes &&
                    !await SaveDocumentAsync(document, false)) return;
            }
            foreach (var document in targets.Where(Documents.Contains))
                RemoveDocument(document);
        }
        finally { _closingAllDocuments = false; }
    }

    private void SearchTabMenuClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchResultSession session)
            CloseSearchSession(session);
    }

    private void SearchTabMenuCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchResultSession keep)
            foreach (var session in SearchSessions.Where(item => item != keep).ToArray())
                CloseSearchSession(session);
    }

    private void SearchTabMenuCloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var session in SearchSessions.ToArray()) CloseSearchSession(session);
    }

    private void SearchTabMenuCopyCondition_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchResultSession session)
            Clipboard.SetText(session.ConditionSummary);
    }

    private void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ConditionInputsPanel is not null) UpdateConditionSummary();
    }

    private void LiteralFindBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        FindOccurrence(false);
        e.Handled = true;
    }

    private void FindOccurrence_Click(object sender, RoutedEventArgs e) => FindOccurrence(false);

    private void FindOccurrence(bool previous)
    {
        if (CurrentEditor is not { } editor || string.IsNullOrEmpty(LiteralFindBox.Text)) return;
        UseConditionSearchCheck.IsChecked = false;
        var found = editor.FindOccurrence(LiteralFindBox.Text, MatchCaseCheck.IsChecked == true,
            WholeWordCheck.IsChecked == true, previous, out var wrapped);
        if (!found) { StatusMessage.Text = $"'{LiteralFindBox.Text}'을(를) 찾지 못했습니다."; return; }
        StatusMessage.Text = wrapped ? "문서 끝에서 다시 검색했습니다." : $"{editor.CurrentLine + 1:N0}번째 줄에서 찾았습니다.";
    }

    private void SearchOpenDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (Documents.Count == 0) return;
        try
        {
            var condition = ActiveSearchCondition();
            var options = CurrentSearchOptions();
            var groups = new List<(SearchResultSource, IReadOnlyList<SearchResult>)>();
            foreach (var document in Documents)
            {
                var snapshot = GetSearchSnapshot(document);
                var results = _searchEngine.Search(snapshot.Text, condition, options);
                groups.Add((new SearchResultSource { DisplayName = document.DisplayName, FilePath = document.FilePath, Snapshot = snapshot }, results));
            }
            var count = groups.Sum(group => group.Item2.Count);
            AddMultiSourceSession($"열린 파일 · {count:N0}", $"열린 문서 {groups.Count:N0}개에서 {count:N0}개 일치 · 결과를 누르면 해당 문서로 이동", groups);
            RecordRecentSearch(ActiveSearchSummary(), options);
            StatusMessage.Text = $"열린 파일에서 {count:N0}개를 찾았습니다.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusMessage.Text = exception.Message;
        }
    }

    private void FolderSearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (FolderSearchOptionsPanel is null) return;
        FolderSearchOptionsPanel.Visibility = FolderSearchCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateConditionSummary();
    }

    private void SearchFolderPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (ConditionInputsPanel is not null) UpdateConditionSummary();
    }

    private void ChooseSearchFolder_Click(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "검색할 폴더 선택",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(SearchFolderPathBox.Text) ? SearchFolderPathBox.Text
                : CurrentDocument?.FilePath is { } path ? Path.GetDirectoryName(path) ?? string.Empty : string.Empty
        };
        if (picker.ShowDialog() == Forms.DialogResult.OK) SearchFolderPathBox.Text = picker.SelectedPath;
    }

    private async void SearchFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folderSearchCancellation is not null) return;
        var folder = SearchFolderPathBox.Text;
        if (!Directory.Exists(folder))
        {
            StatusMessage.Text = "검색할 폴더를 선택하세요. 폴더가 이동하거나 삭제되었는지도 확인하세요.";
            return;
        }
        try
        {
            var condition = ActiveSearchCondition();
            var options = CurrentSearchOptions();
            using var cancellation = new CancellationTokenSource();
            _folderSearchCancellation = cancellation;
            CancelFolderSearchButton.Visibility = Visibility.Visible;
            UpdateConditionSummary();
            StatusMessage.Text = $"{folder} 폴더 검색 중…";
            var progress = new Progress<FolderSearchProgress>(update =>
                StatusMessage.Text = $"폴더 검색 중 · {update.FilesScanned:N0}개 파일 · {update.MatchingLines:N0}개 일치");
            var result = await _folderSearch.SearchAsync(folder, condition, options,
                new FolderSearchOptions(IncludeSubfoldersCheck.IsChecked == true, FolderPatternBox.Text), progress, cancellation.Token);
            var groups = result.Files.Select(file =>
                (Source: new SearchResultSource { DisplayName = Path.GetFileName(file.FilePath), FilePath = file.FilePath,
                    FileLength = file.Length, LastWriteTimeUtc = file.LastWriteUtc }, Matches: file.Matches)).ToArray();
            var count = groups.Sum(group => group.Matches.Count);
            AddMultiSourceSession($"폴더 · {count:N0}",
                $"{folder} · 검사 {result.FilesScanned:N0}개 · 일치 {count:N0}개 · 실패 {result.Failures.Count:N0}개", groups);
            if (result.Failures.Count > 0)
                StatusMessage.Text = $"검색 완료 · {count:N0}개 일치 · 읽기 실패/변경 {result.Failures.Count:N0}개: {string.Join("; ", result.Failures.Take(3).Select(f => Path.GetFileName(f.FilePath) + " " + f.Message))}";
            else StatusMessage.Text = $"검색 완료 · {result.FilesScanned:N0}개 파일 · {count:N0}개 일치";
            RecordRecentSearch(ActiveSearchSummary(), options);
        }
        catch (OperationCanceledException) { StatusMessage.Text = "폴더 검색을 중단했습니다."; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = $"폴더 검색 실패: {exception.Message}";
        }
        finally
        {
            _folderSearchCancellation = null;
            CancelFolderSearchButton.Visibility = Visibility.Collapsed;
            UpdateConditionSummary();
        }
    }

    private void CancelFolderSearch_Click(object sender, RoutedEventArgs e) => _folderSearchCancellation?.Cancel();

    private ConditionNode ActiveSearchCondition() => UseConditionSearchCheck.IsChecked == true
        ? BuildSimpleCondition() : new TextCondition(TextConditionKind.Contains, LiteralFindBox.Text);

    private SearchOptions CurrentSearchOptions() => new(MatchCaseCheck.IsChecked == true,
        WholeWordCheck.IsChecked == true, SelectedNumber(ContextLinesCombo));

    private string ActiveSearchSummary() => UseConditionSearchCheck.IsChecked == true ? ConditionSummaryText.Text : LiteralFindBox.Text;

    private SearchSnapshot GetSearchSnapshot(DocumentViewModel document)
    {
        var key = (document, document.ContentRevision);
        if (!_searchSnapshots.TryGetValue(key, out var snapshot))
            _searchSnapshots[key] = snapshot = new SearchSnapshot(document, document.ContentRevision, document.Text, document.NewLine);
        return snapshot;
    }

    private async Task NavigateExternalResultAsync(ListBox list, SearchResultRow row)
    {
        if (row.Source is not { } source) return;
        if (source.Snapshot is { } snapshot)
        {
            if (!IsSnapshotCurrent(snapshot))
            {
                StatusMessage.Text = "원본 문서가 바뀌거나 닫혀 이동할 수 없습니다.";
                return;
            }
            DocumentTabs.SelectedItem = snapshot.Source;
            snapshot.Source.Editor.GoToLine(row.LineNumber, focusEditor: false);
            list.Focus();
            return;
        }
        if (source.FilePath is not { } filePath) return;
        try
        {
            var file = new FileInfo(filePath);
            if (!file.Exists || file.Length != source.FileLength || file.LastWriteTimeUtc != source.LastWriteTimeUtc)
            {
                StatusMessage.Text = $"{source.DisplayName}: 검색 후 파일이 변경되었습니다. 다시 검색해 주세요.";
                return;
            }
            var opened = Documents.FirstOrDefault(document => string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (opened is { IsModified: true })
            {
                StatusMessage.Text = $"{source.DisplayName}: 열린 문서가 수정되어 검색 당시 위치로 이동할 수 없습니다.";
                return;
            }
            await OpenFileAsync(filePath);
            opened = Documents.FirstOrDefault(document => string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (opened is null) return;
            DocumentTabs.SelectedItem = opened;
            opened.Editor.GoToLine(row.LineNumber, focusEditor: false);
            list.Focus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = $"{source.DisplayName}: {exception.Message}";
        }
    }

    private void AddMultiSourceSession(string title, string description, IEnumerable<(SearchResultSource Source, IReadOnlyList<SearchResult> Matches)> groups)
    {
        var session = new SearchResultSession { Title = title, ToolTip = description, ConditionSummary = title, Description = description };
        foreach (var (source, matches) in groups)
        {
            foreach (var match in matches)
                session.Rows.Add(new SearchResultRow(match.LineNumber, match.Text) { Source = source });
            if (source.Snapshot is { } snapshot) snapshot.ReferenceCount++;
        }
        SearchSessions.Add(session);
        SearchResultTabs.SelectedItem = session;
        ShowSearchResults();
        RefreshSearchSessionState();
    }
}
