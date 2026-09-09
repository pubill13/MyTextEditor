using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using MyTextEditor.Models;
using MyTextEditor.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using DpiChangedEventArgs = System.Windows.DpiChangedEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Mouse = System.Windows.Input.Mouse;
using FontFamily = System.Windows.Media.FontFamily;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using ListBox = System.Windows.Controls.ListBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;

namespace MyTextEditor;

public partial class MainWindow : Window
{
    private readonly DocumentFileService _fileService = new();
    private readonly TextSearchEngine _searchEngine = new();
    private readonly TextTransformService _transformService = new();
    private readonly UserSettings _settings;
    private readonly Dictionary<(DocumentViewModel Document, long Revision), SearchSnapshot> _searchSnapshots = [];
    private readonly HashSet<DocumentViewModel> _savingDocuments = [];
    private readonly HashSet<DocumentViewModel> _closingDocuments = [];
    private string? _pendingTransformedText;
    private DocumentViewModel? _resultDocument;
    private long _resultSourceRevision;
    private bool _resultInvalidated;
    private bool _allowClose;
    private bool _closingInProgress;
    private bool _closingAllDocuments;
    private readonly List<TransformPreviewRow> _allPreviewRows = [];
    private string _settingsSnapshot;
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _settingsReady;
    private static readonly ToolDescriptor[] TextTools =
    [
        new(TextToolIds.RemoveBefore, "머리 자르기", 0), new(TextToolIds.RemoveAfter, "꼬리 자르기", 1),
        new(TextToolIds.RemoveBetween, "사이 지우기", 2), new(TextToolIds.KeepBetween, "사이만 남기기", 3),
        new(TextToolIds.RemoveCharactersLeft, "왼쪽 글자 삭제", 4), new(TextToolIds.RemoveCharactersRight, "오른쪽 글자 삭제", 5),
        new(TextToolIds.AddPrefix, "접두사 추가", 6), new(TextToolIds.AddSuffix, "접미사 추가", 7),
        new(TextToolIds.SplitByDelimiter, "줄 나누기", 8), new(TextToolIds.JoinLines, "줄 합치기", 9),
        new(TextToolIds.AddLineNumbers, "줄 번호 추가", 10), new(TextToolIds.RemoveLineNumbers, "줄 번호 제거", 11),
        new(TextToolIds.RemoveLinesContaining, "포함 줄 삭제"), new(TextToolIds.Replace, "일괄 치환"),
        new(TextToolIds.RemoveDuplicateLines, "중복 줄 제거", QuickOperation: "Duplicate"),
        new(TextToolIds.RemoveBlankLines, "빈 줄 제거", QuickOperation: "Blank"),
        new(TextToolIds.CollapseBlankLines, "빈 줄 합치기", QuickOperation: "Collapse"),
        new(TextToolIds.TrimWhitespace, "앞뒤 공백 제거", QuickOperation: "Whitespace"),
        new(TextToolIds.CleanupLog, "로그 정리", QuickOperation: "LogCleanup")
    ];

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    public ObservableCollection<SearchResultSession> SearchSessions { get; } = [];
    public ObservableCollection<TransformPreviewRow> PreviewRows { get; } = [];

    private DocumentViewModel? CurrentDocument => DocumentTabs.SelectedItem as DocumentViewModel;
    private ScintillaEditorHost? CurrentEditor => CurrentDocument?.Editor;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        var loadResult = SettingsService.LoadWithResult();
        _settings = loadResult.Settings;
        _settingsSnapshot = loadResult.NeedsSave ? string.Empty : JsonSerializer.Serialize(_settings);
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        RestoreWindowPlacement();
        ToolColumn.Width = new GridLength(Math.Max(280, _settings.ToolPanelWidth));
        ResultRow.Height = new GridLength(Math.Max(120, _settings.ResultPanelHeight));
        Application.Current.Resources["EditorFontFamily"] = new FontFamily(_settings.EditorFontFamily);
        Application.Current.Resources["EditorFontSize"] = _settings.EditorFontSize;
        ApplyTheme(_settings.Theme);
        LoadInstalledFonts();
        SelectComboItem(FontFamilyCombo, _settings.EditorFontFamily);
        SelectComboItem(OverflowFontFamilyCombo, _settings.EditorFontFamily);
        SelectComboItem(FontSizeCombo, _settings.EditorFontSize.ToString("0"));
        SelectComboItem(OverflowFontSizeCombo, _settings.EditorFontSize.ToString("0"));
        RestorePanelVisibility();
        BuildRecentFilesMenu();

        UpdateConditionSummary();
        RestoreWorkState();
        RenderFavoriteTools();
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); SaveSettings(false); };
        AttachSettingsTracking();
        _settingsReady = true;
        LocationChanged += (_, _) => MarkSettingsDirty();
        StateChanged += (_, _) => MarkSettingsDirty();
        NewDocument();
        if (loadResult.Error is not null)
            Dispatcher.BeginInvoke(() => StatusMessage.Text = $"설정을 불러오지 못해 기본값을 사용했습니다: {loadResult.Error.Message}");
        else if (loadResult.NeedsSave)
            Dispatcher.BeginInvoke(() =>
            {
                var saved = SaveSettings(false);
                if (saved && loadResult.RemovedLegacySearchCount > 0)
                    StatusMessage.Text = $"새 검색 방식으로 바꿀 수 없는 현재 조건 또는 검색 기록 {loadResult.RemovedLegacySearchCount:N0}개를 정리했습니다.";
            });
    }

    private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

    private void NewDocument(string text = "", string? title = null)
    {
        var editor = new ScintillaEditorHost();
        var document = new DocumentViewModel { Editor = editor };
        if (!string.IsNullOrWhiteSpace(title)) document.FilePath = title;
        ConfigureEditor(document);
        editor.SetNewLine(document.NewLine);
        editor.LoadUtf8(ReadOnlyMemory<byte>.Empty);
        if (text.Length > 0) editor.ReplaceAll(text);
        Documents.Add(document);
        document.IsModified = text.Length > 0;
        DocumentTabs.SelectedItem = document;
        EmptyDocumentState.Visibility = Visibility.Collapsed;
        StatusMessage.Text = "새 문서를 만들었습니다.";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "텍스트 파일 열기",
            Filter = "텍스트 파일|*.txt;*.log;*.csv;*.md;*.json;*.xml;*.yaml;*.yml|모든 파일|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await OpenFilesAsync(dialog.FileNames);
    }

    private async Task OpenFileAsync(string filePath) => await OpenFilesAsync([filePath]);

    private async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        var uniquePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignoredFolders = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { ignoredFolders++; continue; }
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath)) uniquePaths.Add(fullPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }
        if (uniquePaths.Count == 0)
        {
            StatusMessage.Text = ignoredFolders > 0 ? "폴더는 열지 않았습니다. 파일을 놓아 주세요." : "열 수 있는 파일이 없습니다.";
            return;
        }

        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        long openedBytes = 0;
        Mouse.OverrideCursor = Cursors.Wait;
        StatusMessage.Text = $"{uniquePaths.Count:N0}개 파일을 여는 중…";
        try
        {
            foreach (var filePath in uniquePaths)
            {
                try { await OpenFileCoreAsync(filePath); openedBytes += new FileInfo(filePath).Length; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    errors.Add($"{Path.GetFileName(filePath)}: {exception.Message}");
                }
            }
        }
        finally
        {
            stopwatch.Stop();
            Mouse.OverrideCursor = null;
        }
        StatusMessage.Text = errors.Count == 0
            ? $"{uniquePaths.Count:N0}개 · {FormatFileSize(openedBytes)} · {stopwatch.Elapsed.TotalSeconds:F2}초" + (ignoredFolders > 0 ? $" · 폴더 {ignoredFolders:N0}개 제외" : string.Empty)
            : $"{uniquePaths.Count - errors.Count:N0}개 · {FormatFileSize(openedBytes)} · {stopwatch.Elapsed.TotalSeconds:F2}초 · 실패 {errors.Count:N0}개";
        if (errors.Count > 0)
            MessageBox.Show(this, $"일부 파일을 열 수 없습니다.\n\n{string.Join("\n", errors.Take(12))}", "파일 열기", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FormatFileSize(long bytes) => bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024d:F1}MB" : bytes >= 1024 ? $"{bytes / 1024d:F1}KB" : $"{bytes}B";

    private async Task OpenFileCoreAsync(string filePath)
    {
        var alreadyOpen = Documents.FirstOrDefault(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen is not null) { DocumentTabs.SelectedItem = alreadyOpen; return; }
        var buffer = await _fileService.LoadBufferAsync(filePath);
        var editor = new ScintillaEditorHost();
        var document = new DocumentViewModel
        {
            Editor = editor,
            FilePath = buffer.FilePath,
            Encoding = buffer.OriginalEncoding,
            HasByteOrderMark = buffer.HasByteOrderMark,
            NewLine = buffer.NewLine,
            IsModified = false
        };
        ConfigureEditor(document);
        editor.SetNewLine(document.NewLine);
        editor.LoadUtf8(buffer.Utf8Buffer);
        Documents.Add(document);
        DocumentTabs.SelectedItem = document;
        AddRecentFile(filePath);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveDocumentAsync(CurrentDocument, false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveDocumentAsync(CurrentDocument, true);

    private async Task<bool> SaveDocumentAsync(DocumentViewModel? document, bool saveAs)
    {
        if (document is null) return true;
        if (!_savingDocuments.Add(document))
        {
            StatusMessage.Text = $"{document.DisplayName} 파일을 이미 저장하고 있습니다.";
            return false;
        }

        try
        {
            var path = document.FilePath;
            if (saveAs || string.IsNullOrWhiteSpace(path))
            {
                var dialog = new SaveFileDialog
                {
                    Title = "다른 이름으로 저장",
                    FileName = document.FilePath is null ? "새 문서.txt" : document.DisplayName,
                    Filter = "텍스트 파일|*.txt|로그 파일|*.log|마크다운|*.md|모든 파일|*.*"
                };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }

            var savedRevision = document.ContentRevision;
            var state = ToCoreDocument(document);
            try
            {
                await _fileService.SaveAsync(state, path);
            }
            catch (DocumentEncodingException exception)
            {
                var answer = MessageBox.Show(this, $"{exception.Message}\n\nUTF-8로 저장할까요?", "문자 인코딩", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return false;
                state.Encoding = new UTF8Encoding(false, true);
                state.HasByteOrderMark = false;
                try
                {
                    await _fileService.SaveAsync(state, path);
                }
                catch (Exception retryException) when (retryException is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(this, $"UTF-8로도 파일을 저장할 수 없습니다.\n\n{retryException.Message}", "저장", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"파일을 저장할 수 없습니다.\n\n{exception.Message}", "저장", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            document.FilePath = state.FilePath;
            document.Encoding = state.Encoding;
            document.HasByteOrderMark = state.HasByteOrderMark;
            var hasNewerChanges = document.ContentRevision != savedRevision;
            if (hasNewerChanges)
                document.IsModified = true;
            else
                document.Editor.MarkSaved();
            document.NotifyIdentityChanged();
            AddRecentFile(path!);
            UpdateStatus();
            StatusMessage.Text = hasNewerChanges
                ? $"{document.DisplayName} 파일을 저장했습니다. 저장 중 입력한 변경 내용은 아직 저장되지 않았습니다."
                : $"{document.DisplayName} 파일을 저장했습니다.";
            return !hasNewerChanges;
        }
        finally
        {
            _savingDocuments.Remove(document);
        }
    }

    private static DocumentState ToCoreDocument(DocumentViewModel document) => new()
    {
        FilePath = document.FilePath,
        Text = document.Text,
        IsModified = document.IsModified,
        Encoding = document.Encoding,
        HasByteOrderMark = document.HasByteOrderMark,
        NewLine = document.NewLine
    };

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DocumentViewModel document) await CloseDocumentAsync(document);
        e.Handled = true;
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is not null) await CloseDocumentAsync(CurrentDocument);
    }

    private async void CloseAllTabs_Click(object sender, RoutedEventArgs e) => await CloseAllDocumentsAsync();

    private async Task<bool> CloseDocumentAsync(DocumentViewModel document)
    {
        if (_closingAllDocuments || !_closingDocuments.Add(document)) return false;
        try
        {
            if (document.IsModified)
            {
                var answer = MessageBox.Show(this, $"'{document.DisplayName}'의 변경 내용을 저장할까요?", "문서 닫기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return false;
                if (answer == MessageBoxResult.Yes && !await SaveDocumentAsync(document, false)) return false;
            }
            if (!Documents.Contains(document)) return false;
            RemoveDocument(document);
            return true;
        }
        finally
        {
            _closingDocuments.Remove(document);
        }
    }

    private void RemoveDocument(DocumentViewModel document)
    {
        Documents.Remove(document);
        document.Editor.ReleaseResources();
        RefreshSearchSessionState();
        EmptyDocumentState.Visibility = Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (CurrentEditor?.CanUndo == true) CurrentEditor.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (CurrentEditor?.CanRedo == true) CurrentEditor.Redo(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => CurrentEditor?.SelectAll();
    private void Find_Click(object sender, RoutedEventArgs e) => ShowSearchInput();
    private void Replace_Click(object sender, RoutedEventArgs e) => ShowReplaceInput();
    private void FindNext_Click(object sender, RoutedEventArgs e) => MoveToSearchResult(1);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => MoveToSearchResult(-1);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_closingAllDocuments || _closingDocuments.Count > 0 || _savingDocuments.Count > 0)
        {
            e.Cancel = true;
            StatusMessage.Text = "진행 중인 문서 저장 또는 닫기가 끝난 뒤 다시 시도하세요.";
            return;
        }
        if (!Documents.Any(document => document.IsModified))
        {
            _closingInProgress = true;
            CompleteShutdown();
            return;
        }
        e.Cancel = true;
        if (_closingInProgress) return;
        _closingInProgress = true;
        if (!await ConfirmCloseAllAsync())
        {
            _closingInProgress = false;
            return;
        }

        CompleteShutdown();
        _allowClose = true;
        Close();
    }

    private void CompleteShutdown()
    {
        _settingsSaveTimer.Stop();
        Hide();
        if (!SaveSettings(false))
        {
            Show();
            MessageBox.Show(this, StatusMessage.Text, "설정 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
            Hide();
        }
        foreach (var document in Documents)
            document.Editor.ReleaseResources();
        SearchSessions.Clear();
        _searchSnapshots.Clear();
    }

    private async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var document in Documents.Where(item => item.IsModified).ToArray())
        {
            DocumentTabs.SelectedItem = document;
            var answer = MessageBox.Show(this, $"'{document.DisplayName}'의 변경 내용을 저장할까요?", "프로그램 종료", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !await SaveDocumentAsync(document, false)) return false;
        }
        return true;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (!TryMapShortcut(e.Key, Keyboard.Modifiers, out var shortcut)) return;
        e.Handled = true;
        ExecuteShortcut(shortcut);
    }

    private static bool TryMapShortcut(Key key, ModifierKeys modifiers, out EditorShortcut shortcut)
    {
        shortcut = default;
        if (modifiers == ModifierKeys.Control)
        {
            shortcut = key switch
            {
                Key.N => EditorShortcut.NewDocument,
                Key.O => EditorShortcut.OpenDocument,
                Key.S => EditorShortcut.SaveDocument,
                Key.W or Key.F4 => EditorShortcut.CloseDocument,
                Key.F => EditorShortcut.Find,
                Key.H => EditorShortcut.Replace,
                Key.Tab or Key.PageDown => EditorShortcut.NextDocument,
                Key.PageUp => EditorShortcut.PreviousDocument,
                _ => default
            };
            return key is Key.N or Key.O or Key.S or Key.W or Key.F4 or Key.F or Key.H or Key.Tab or Key.PageDown or Key.PageUp;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            shortcut = key switch
            {
                Key.S => EditorShortcut.SaveDocumentAs,
                Key.W => EditorShortcut.CloseAllDocuments,
                Key.Tab => EditorShortcut.PreviousDocument,
                _ => default
            };
            return key is Key.S or Key.W or Key.Tab;
        }
        if (modifiers == ModifierKeys.None && key == Key.F3) { shortcut = EditorShortcut.FindNext; return true; }
        if (modifiers == ModifierKeys.Shift && key == Key.F3) { shortcut = EditorShortcut.FindPrevious; return true; }
        return false;
    }

    private void Editor_ShortcutRequested(object? sender, EditorShortcutEventArgs e) =>
        Dispatcher.BeginInvoke(() => ExecuteShortcut(e.Shortcut));

    private void ExecuteShortcut(EditorShortcut shortcut)
    {
        switch (shortcut)
        {
            case EditorShortcut.NewDocument: NewDocument(); break;
            case EditorShortcut.OpenDocument: Open_Click(this, new RoutedEventArgs()); break;
            case EditorShortcut.SaveDocument: Save_Click(this, new RoutedEventArgs()); break;
            case EditorShortcut.SaveDocumentAs: SaveAs_Click(this, new RoutedEventArgs()); break;
            case EditorShortcut.CloseDocument: CloseTab_Click(this, new RoutedEventArgs()); break;
            case EditorShortcut.CloseAllDocuments: _ = CloseAllDocumentsAsync(); break;
            case EditorShortcut.Find: ShowSearchInput(); break;
            case EditorShortcut.Replace: ShowReplaceInput(); break;
            case EditorShortcut.NextDocument: SelectRelativeDocument(1); break;
            case EditorShortcut.PreviousDocument: SelectRelativeDocument(-1); break;
            case EditorShortcut.FindNext: MoveToSearchResult(1); break;
            case EditorShortcut.FindPrevious: MoveToSearchResult(-1); break;
        }
    }

    private async Task CloseAllDocumentsAsync()
    {
        if (_closingAllDocuments || _closingDocuments.Count > 0) return;
        _closingAllDocuments = true;
        try
        {
            if (!await ConfirmCloseAllAsync()) return;
            foreach (var document in Documents.ToArray()) RemoveDocument(document);
        }
        finally
        {
            _closingAllDocuments = false;
        }
    }

    private void SelectRelativeDocument(int direction)
    {
        if (Documents.Count < 2) return;
        var index = DocumentTabs.SelectedIndex;
        DocumentTabs.SelectedIndex = (index + direction + Documents.Count) % Documents.Count;
        CurrentEditor?.FocusEditor();
    }

    private void ShowSearchInput()
    {
        ToolsMenuItem.IsChecked = true;
        ToggleTools_Click(ToolsMenuItem, new RoutedEventArgs());
        ToolTabs.SelectedIndex = 0;
        SimpleAllBox.Focus();
    }

    private void ShowReplaceInput()
    {
        ToolsMenuItem.IsChecked = true;
        ToggleTools_Click(ToolsMenuItem, new RoutedEventArgs());
        ToolTabs.SelectedIndex = 1;
        ReplaceFromBox.Focus();
    }

    private void Editor_CaretChanged(object? sender, EventArgs e) => UpdateStatus();
    private void Editor_RevisionChanged(object? sender, EventArgs e)
    {
        if (sender is ScintillaEditorHost editor && FindDocument(editor) is { } document && document == _resultDocument && document.ContentRevision != _resultSourceRevision)
            InvalidateResults("원문이 변경되었습니다. 검색 또는 미리보기를 다시 실행하세요.");
        RefreshSearchSessionState();
        UpdateStatus();
    }

    private void Editor_DirtyChanged(object? sender, EventArgs e)
    {
        if (sender is ScintillaEditorHost editor && FindDocument(editor) is { } document)
            document.IsModified = editor.IsModified;
    }

    private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DocumentTabs) return;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var document = CurrentDocument;
        var editor = CurrentEditor;
        if (document is null)
        {
            CaretStatus.Text = "줄 -, 열 -"; LineCountStatus.Text = "0줄"; EncodingStatus.Text = "-"; NewLineStatus.Text = "-"; return;
        }
        CaretStatus.Text = $"줄 {(editor?.CurrentLine ?? 0) + 1}, 열 {(editor?.CurrentColumn ?? 0) + 1}";
        LineCountStatus.Text = $"{editor?.LineCount ?? 1:N0}줄";
        EncodingStatus.Text = document.Encoding.WebName.ToUpperInvariant();
        NewLineStatus.Text = document.NewLine == "\r\n" ? "CRLF" : document.NewLine == "\n" ? "LF" : "CR";
    }

    private void ConfigureEditor(DocumentViewModel document)
    {
        var editor = document.Editor;
        editor.CaretChanged += Editor_CaretChanged;
        editor.RevisionChanged += Editor_RevisionChanged;
        editor.DirtyChanged += Editor_DirtyChanged;
        editor.ShortcutRequested += Editor_ShortcutRequested;
        editor.FilesDropped += paths => _ = Dispatcher.InvokeAsync(async () => await OpenFilesAsync(paths));
        ApplyEditorAppearance(editor);
    }

    private DocumentViewModel? FindDocument(ScintillaEditorHost editor) => Documents.FirstOrDefault(document => document.Editor == editor);

    private void SimpleCondition_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderSimpleTags();
        UpdateConditionSummary();
    }
    private void SearchInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && SearchButton.IsEnabled) { Search_Click(sender, new RoutedEventArgs()); e.Handled = true; } }

    private void UpdateConditionSummary()
    {
        var valid = SimpleDescriptions().Length > 0;
        ConditionSummaryText.Text = !valid
            ? "검색 조건을 입력하세요."
            : $"{string.Join(", ", SimpleDescriptions())}인 줄을 찾습니다.";
        SearchButton.IsEnabled = valid;
        MarkSettingsDirty();
    }

    private string[] SimpleDescriptions()
    {
        var all = ParseTerms(SimpleAllBox.Text).Select(value => $"'{value}' 모두 포함");
        var any = ParseTerms(SimpleAnyBox.Text).Select(value => $"'{value}' 중 하나 포함");
        var excluded = ParseTerms(SimpleExcludeBox.Text).Select(value => $"'{value}' 제외");
        return all.Concat(any).Concat(excluded).ToArray();
    }

    private static string[] ParseTerms(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(value => value.Length > 0).ToArray();

    private void RenderSimpleTags()
    {
        if (SimpleAllTags is null || SimpleAnyTags is null || SimpleExcludeTags is null) return;
        RenderTagList(SimpleAllTags, SimpleAllBox);
        RenderTagList(SimpleAnyTags, SimpleAnyBox);
        RenderTagList(SimpleExcludeTags, SimpleExcludeBox);
    }

    private void RenderTagList(Panel host, TextBox source)
    {
        host.Children.Clear();
        foreach (var term in ParseTerms(source.Text))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = term, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "×", ToolTip = $"'{term}' 조건 삭제", MinWidth = 24, MinHeight = 24, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(5, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                var terms = ParseTerms(source.Text).ToList();
                var index = terms.FindIndex(value => string.Equals(value, term, StringComparison.Ordinal));
                if (index >= 0) terms.RemoveAt(index);
                source.Text = string.Join(", ", terms);
                source.CaretIndex = source.Text.Length;
                source.Focus();
            };
            content.Children.Add(remove);
            var chip = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 2, 3, 2), Margin = new Thickness(0, 0, 5, 4) };
            chip.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            chip.BorderThickness = new Thickness(1);
            host.Children.Add(chip);
        }
    }

    private ConditionNode BuildSimpleCondition()
    {
        var children = new List<ConditionNode>();
        children.AddRange(ParseTerms(SimpleAllBox.Text).Select(value => new TextCondition(TextConditionKind.Contains, value)));
        var any = ParseTerms(SimpleAnyBox.Text).Select(value => (ConditionNode)new TextCondition(TextConditionKind.Contains, value)).ToArray();
        if (any.Length > 0) children.Add(new ConditionGroup(ConditionOperator.Any, any));
        children.AddRange(ParseTerms(SimpleExcludeBox.Text).Select(value => new TextCondition(TextConditionKind.DoesNotContain, value)));
        return new ConditionGroup(ConditionOperator.All, children);
    }

    private SearchInputState CaptureSearchState(SearchOptions? options = null)
    {
        options ??= new SearchOptions(MatchCaseCheck.IsChecked == true, WholeWordCheck.IsChecked == true, SelectedNumber(ContextLinesCombo));
        return SearchInputState.CreateSimple(
            ParseTerms(SimpleAllBox.Text), ParseTerms(SimpleAnyBox.Text), ParseTerms(SimpleExcludeBox.Text),
            new SavedSearchOptions { MatchCase = options.MatchCase, WholeWord = options.WholeWord, ContextLines = options.ContextLines });
    }

    private void RestoreSearchState(SearchInputState state)
    {
        SimpleAllBox.Text = string.Join(", ", state.SimpleAllTerms);
        SimpleAnyBox.Text = string.Join(", ", state.SimpleAnyTerms);
        SimpleExcludeBox.Text = string.Join(", ", state.SimpleExcludeTerms);
        MatchCaseCheck.IsChecked = state.Options.MatchCase;
        WholeWordCheck.IsChecked = state.Options.WholeWord;
        SelectComboItem(ContextLinesCombo, state.Options.ContextLines.ToString());
        RenderSimpleTags(); UpdateConditionSummary();
    }

    private void RecordRecentSearch(string summary, SearchOptions options)
    {
        SavedSearchHistory.AddOrMoveToFront(_settings.RecentSearches, new SavedSearch
        {
            Search = CaptureSearchState(options), Summary = summary, ExecutedAt = DateTimeOffset.Now
        });
        MarkSettingsDirty();
    }

    private void RecentSearches_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_settings.RecentSearches.Count == 0) menu.Items.Add(new MenuItem { Header = "최근 검색 없음", IsEnabled = false });
        foreach (var saved in _settings.RecentSearches.ToArray())
        {
            var header = saved.Summary.Length > 60 ? saved.Summary[..60] + "…" : saved.Summary;
            var parent = new MenuItem { Header = header, ToolTip = $"{saved.Summary}\n{saved.ExecutedAt.ToLocalTime():yyyy-MM-dd HH:mm}" };
            var load = new MenuItem { Header = "조건 불러오기" };
            load.Click += (_, _) => { RestoreSearchState(saved.Search); _settings.SearchState = CaptureSearchState(); MarkSettingsDirty(); };
            var remove = new MenuItem { Header = "기록 삭제" };
            remove.Click += (_, _) => { _settings.RecentSearches.Remove(saved); MarkSettingsDirty(); };
            parent.Items.Add(load); parent.Items.Add(remove); menu.Items.Add(parent);
        }
        if (_settings.RecentSearches.Count > 0)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "모든 최근 검색 삭제" };
            clear.Click += (_, _) => { _settings.RecentSearches.Clear(); MarkSettingsDirty(); };
            menu.Items.Add(clear);
        }
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null) return;
        try
        {
            var condition = BuildSimpleCondition();
            var options = new SearchOptions(MatchCaseCheck.IsChecked == true, WholeWordCheck.IsChecked == true, SelectedNumber(ContextLinesCombo));
            var document = CurrentDocument;
            var revision = document.ContentRevision;
            var key = (document, revision);
            if (!_searchSnapshots.TryGetValue(key, out var snapshot))
            {
                snapshot = new SearchSnapshot(document, revision, document.Text, document.NewLine);
                _searchSnapshots.Add(key, snapshot);
            }
            var results = _searchEngine.SearchRanges(snapshot.Text, condition, options);
            var matchedLines = results.Select(result => result.LineNumber).ToHashSet();
            var displayLines = new Dictionary<int, TextRange>();
            foreach (var result in results)
            {
                foreach (var context in result.Context)
                    displayLines.TryAdd(context.LineNumber, context.Range);
            }
            var summary = ConditionSummaryText.Text;
            RecordRecentSearch(summary, options);
            var shortSummary = summary.Length > 28 ? summary[..28] + "…" : summary;
            var session = new SearchResultSession
            {
                Title = $"{shortSummary} · {results.Count:N0}",
                ToolTip = $"{document.DisplayName}\n{summary}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                ConditionSummary = summary,
                Snapshot = snapshot,
                MatchedLineNumbers = results.Select(result => result.LineNumber).ToArray()
            };
            foreach (var line in displayLines.OrderBy(item => item.Key))
                session.Rows.Add(new SearchResultRow(line.Key, snapshot.Text.Substring(line.Value.Start, line.Value.Length), !matchedLines.Contains(line.Key)));
            snapshot.ReferenceCount++;
            SearchSessions.Add(session);
            SearchResultTabs.SelectedItem = session;
            if (TransformPreviewView.Visibility != Visibility.Visible)
                ShowSearchResults();
            RefreshSearchSessionState();
            StatusMessage.Text = results.Count == 0 ? "일치하는 줄이 없습니다." : $"{results.Count:N0}개 줄을 찾았습니다.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "조건 검색", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void MoveToSearchResult(int direction)
    {
        if (SearchResultTabs.SelectedItem is not SearchResultSession session || session.Rows.Count == 0 || _activeResultsList is null) return;
        var matches = session.Rows.Where(row => !row.IsContext).ToArray();
        if (matches.Length == 0) return;
        var current = _activeResultsList.SelectedItem as SearchResultRow;
        var index = Array.IndexOf(matches, current);
        index = index < 0 ? (direction > 0 ? 0 : matches.Length - 1) : (index + direction + matches.Length) % matches.Length;
        var row = matches[index];
        _activeResultsList.SelectedItem = row;
        _activeResultsList.ScrollIntoView(row);
        if (IsSessionCurrent(session))
        {
            DocumentTabs.SelectedItem = session.Snapshot.Source;
            session.Snapshot.Source.Editor.GoToLine(row.LineNumber);
        }
    }

    private void GoToLine(int lineNumber)
    {
        var editor = CurrentEditor;
        if (editor is null || lineNumber < 1 || lineNumber > editor.LineCount) return;
        editor.GoToLine(lineNumber);
    }

    private ListBox? _activeResultsList;
    private SearchResultSession? ActiveSearchSession => SearchResultTabs.SelectedItem as SearchResultSession;
    private bool IsSessionCurrent(SearchResultSession session) => Documents.Contains(session.Snapshot.Source) && session.Snapshot.Source.ContentRevision == session.Snapshot.Revision;
    private void SearchResultsList_Loaded(object sender, RoutedEventArgs e) { _activeResultsList = (ListBox)sender; }
    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => NavigateSelectedResult((ListBox)sender);
    private void SearchResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NavigateSelectedResult((ListBox)sender); e.Handled = true; }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CopyRows(((ListBox)sender).SelectedItems.Cast<SearchResultRow>()); e.Handled = true; }
    }
    private void NavigateSelectedResult(ListBox list)
    {
        if (ActiveSearchSession is not { } session || list.SelectedItem is not SearchResultRow row || !IsSessionCurrent(session)) return;
        DocumentTabs.SelectedItem = session.Snapshot.Source;
        session.Snapshot.Source.Editor.GoToLine(row.LineNumber);
    }
    private void SearchResultTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == SearchResultTabs) RefreshSearchSessionState(); }
    private void IncludeLineNumbers_Changed(object sender, RoutedEventArgs e) { if (ActiveSearchSession is { } session) session.IncludeLineNumbers = IncludeLineNumbersCheck.IsChecked == true; }
    private void RefreshSearchSessionState()
    {
        if (ActiveSearchSession is not { } session) { SearchSessionStatus.Text = "검색 결과 없음"; return; }
        IncludeLineNumbersCheck.IsChecked = session.IncludeLineNumbers;
        var current = IsSessionCurrent(session);
        SearchSessionStatus.Text = current ? $"{session.Snapshot.Source.DisplayName} · 원문 연결됨" : $"{session.Snapshot.Source.DisplayName} · 원문 변경됨 (복사만 가능)";
        ExtractSelectedButton.IsEnabled = ExtractAllButton.IsEnabled = DeleteSelectedButton.IsEnabled = DeleteAllButton.IsEnabled = current;
    }
    private IEnumerable<SearchResultRow> SelectedMatchRows() => _activeResultsList?.SelectedItems.Cast<SearchResultRow>().Where(row => !row.IsContext) ?? [];
    private void CopySelectedResults_Click(object sender, RoutedEventArgs e) => CopyRows(_activeResultsList?.SelectedItems.Cast<SearchResultRow>() ?? []);
    private void CopyAllResults_Click(object sender, RoutedEventArgs e) => CopyRows(ActiveSearchSession?.Rows.Where(row => !row.IsContext) ?? []);
    private void CopyRows(IEnumerable<SearchResultRow> source)
    {
        if (ActiveSearchSession is not { } session) return;
        var rows = source.ToArray(); if (rows.Length == 0) return;
        Clipboard.SetText(string.Join(session.Snapshot.NewLine, rows.Select(row => session.IncludeLineNumbers ? $"{row.LineNumber}: {row.Text}" : row.Text)));
        StatusMessage.Text = $"{rows.Length:N0}개 결과를 복사했습니다.";
    }
    private void ExtractSelectedResults_Click(object sender, RoutedEventArgs e) => ExtractSessionRows(SelectedMatchRows().Select(row => row.LineNumber));
    private void ExtractAllResults_Click(object sender, RoutedEventArgs e) => ExtractSessionRows(ActiveSearchSession?.MatchedLineNumbers ?? []);
    private void ExtractSessionRows(IEnumerable<int> lines)
    {
        if (ActiveSearchSession is not { } session || !IsSessionCurrent(session)) return;
        var numbers = lines.Distinct().Order().ToArray(); if (numbers.Length == 0) return;
        var result = _transformService.ExtractLines(session.Snapshot.Source.Text, numbers, 0, session.Snapshot.Source.NewLine);
        NewDocument(result.Text); StatusMessage.Text = $"{numbers.Length:N0}개 줄을 새 탭으로 추출했습니다.";
    }
    private void DeleteSelectedResults_Click(object sender, RoutedEventArgs e) => DeleteSessionRows(SelectedMatchRows().Select(row => row.LineNumber));
    private void DeleteAllResults_Click(object sender, RoutedEventArgs e) => DeleteSessionRows(ActiveSearchSession?.MatchedLineNumbers ?? []);
    private void DeleteSessionRows(IEnumerable<int> lines)
    {
        if (ActiveSearchSession is not { } session || !IsSessionCurrent(session)) return;
        var numbers = lines.Distinct().Order().ToArray(); if (numbers.Length == 0) return;
        DocumentTabs.SelectedItem = session.Snapshot.Source;
        ShowTransformPreview(_transformService.DeleteLines(session.Snapshot.Source.Text, numbers, session.Snapshot.Source.NewLine), "일치 줄 삭제");
    }
    private void SearchResultClose_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is SearchResultSession session) CloseSearchSession(session); e.Handled = true; }
    private void CloseOtherResults_Click(object sender, RoutedEventArgs e) { if (ActiveSearchSession is not { } keep) return; foreach (var session in SearchSessions.Where(x => x != keep).ToArray()) CloseSearchSession(session); }
    private void CloseAllResults_Click(object sender, RoutedEventArgs e) { foreach (var session in SearchSessions.ToArray()) CloseSearchSession(session); HideResultPanelIfEmpty(); }
    private void CloseSearchSession(SearchResultSession session)
    {
        SearchSessions.Remove(session); session.Snapshot.ReferenceCount--;
        if (session.Snapshot.ReferenceCount == 0) _searchSnapshots.Remove((session.Snapshot.Source, session.Snapshot.Revision));
        RefreshSearchSessionState(); HideResultPanelIfEmpty();
    }

    private void TrimOperationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndMarkerPanel is null) return;
        var index = TrimOperationCombo.SelectedIndex;
        var markerOperation = index is >= 0 and <= 3;
        var between = index is 2 or 3;
        MarkerInputsPanel.Visibility = markerOperation ? Visibility.Visible : Visibility.Collapsed;
        EndMarkerPanel.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        TrimOptionsPanel.Visibility = markerOperation ? Visibility.Visible : Visibility.Collapsed;
        KeepEndCheck.Visibility = between ? Visibility.Visible : Visibility.Collapsed;
        CharacterCountPanel.Visibility = index is 4 or 5 ? Visibility.Visible : Visibility.Collapsed;
        ValueInputPanel.Visibility = index is 6 or 7 or 8 or 9 ? Visibility.Visible : Visibility.Collapsed;
        LineNumberPanel.Visibility = index is 10 or 11 ? Visibility.Visible : Visibility.Collapsed;
        StartNumberPanel.Visibility = index == 10 ? Visibility.Visible : Visibility.Collapsed;
        IncludeBlankLinesCheck.Visibility = index is 6 or 7 or 10 ? Visibility.Visible : Visibility.Collapsed;
        ValueInputLabel.Text = index switch { 6 => "앞에 추가할 텍스트", 7 => "뒤에 추가할 텍스트", 8 => "나눌 구분자", 9 => "줄을 이을 구분자", _ => "텍스트" };
    }

    private void PreviewTrim_Click(object sender, RoutedEventArgs e) => RunSelectedTransform(preview: true);
    private void ApplyTrimDirect_Click(object sender, RoutedEventArgs e) => RunSelectedTransform(preview: false);

    private void RunSelectedTransform(bool preview)
    {
        if (CurrentDocument is null) return;
        var index = TrimOperationCombo.SelectedIndex;
        TextTransformResult result;
        if (index is >= 0 and <= 3)
        {
            if (string.IsNullOrEmpty(StartMarkerBox.Text)) { ShowInputMessage("시작 기준 텍스트를 입력하세요.", StartMarkerBox); return; }
            if (index is 2 or 3 && string.IsNullOrEmpty(EndMarkerBox.Text)) { ShowInputMessage("종료 기준 텍스트를 입력하세요.", EndMarkerBox); return; }
            var operation = index switch { 1 => TrimOperation.RemoveAfter, 2 => TrimOperation.RemoveBetween, 3 => TrimOperation.KeepBetween, _ => TrimOperation.RemoveBefore };
            var options = new TrimOptions(operation, StartMarkerBox.Text, EndMarkerBox.Text, KeepStartCheck.IsChecked == true, KeepEndCheck.IsChecked == true, TrimMatchCaseCheck.IsChecked == true);
            result = _transformService.Trim(CurrentDocument.Text, options, CurrentDocument.NewLine);
        }
        else if (index is 4 or 5)
        {
            if (!int.TryParse(CharacterCountBox.Text, out var count) || count < 0) { ShowInputMessage("삭제할 글자 수에 0 이상의 정수를 입력하세요.", CharacterCountBox); return; }
            result = _transformService.RemoveCharacters(CurrentDocument.Text, count, index == 4 ? CharacterRemovalSide.Left : CharacterRemovalSide.Right, CurrentDocument.NewLine);
        }
        else if (index is >= 6 and <= 9)
        {
            if (index is 8 or 9 && string.IsNullOrEmpty(ValueInputBox.Text)) { ShowInputMessage("구분자를 입력하세요.", ValueInputBox); return; }
            result = index switch
            {
                6 => _transformService.AddPrefix(CurrentDocument.Text, ValueInputBox.Text, IncludeBlankLinesCheck.IsChecked == true, CurrentDocument.NewLine),
                7 => _transformService.AddSuffix(CurrentDocument.Text, ValueInputBox.Text, IncludeBlankLinesCheck.IsChecked == true, CurrentDocument.NewLine),
                8 => _transformService.SplitByDelimiter(CurrentDocument.Text, ValueInputBox.Text, CurrentDocument.NewLine),
                _ => _transformService.JoinLines(CurrentDocument.Text, ValueInputBox.Text)
            };
        }
        else
        {
            if (index == 10 && (!int.TryParse(StartNumberBox.Text, out var startNumber) || startNumber < 0)) { ShowInputMessage("시작 번호에 0 이상의 정수를 입력하세요.", StartNumberBox); return; }
            if (string.IsNullOrEmpty(LineNumberSeparatorBox.Text)) { ShowInputMessage("번호 구분자를 입력하세요.", LineNumberSeparatorBox); return; }
            result = index == 10
                ? _transformService.AddLineNumbers(CurrentDocument.Text, int.Parse(StartNumberBox.Text), LineNumberSeparatorBox.Text, IncludeBlankLinesCheck.IsChecked == true, CurrentDocument.NewLine)
                : _transformService.RemoveLineNumbers(CurrentDocument.Text, LineNumberSeparatorBox.Text, CurrentDocument.NewLine);
        }
        var title = (TrimOperationCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "텍스트 정리";
        if (preview) ShowTransformPreview(result, $"{title} 미리보기");
        else ApplyTransformDirect(result, title);
    }

    private void ShowInputMessage(string message, FrameworkElement? input = null)
    {
        StatusMessage.Text = message;
        input?.Focus();
    }

    private void QuickTransform_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null || sender is not FrameworkElement { Tag: string operation }) return;
        RunQuickTransform(operation, ((Button)sender).ToolTip?.ToString() ?? "빠른 정리", preview: true);
    }

    private void ApplyQuickTransform_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null || sender is not FrameworkElement { Tag: string operation }) return;
        RunQuickTransform(operation, ((Button)sender).ToolTip?.ToString() ?? "빠른 정리", preview: false);
    }

    private void RunQuickTransform(string operation, string title, bool preview = true)
    {
        if (CurrentDocument is null) return;
        if (operation == "LogCleanup")
        {
            var cleanup = _transformService.CleanupLog(CurrentDocument.Text, CurrentDocument.NewLine);
            var summary = cleanup.CleanupSummary;
            var details = $"ANSI {summary.AnsiSequencesRemoved:N0} · 제어문자 {summary.ControlCharactersRemoved:N0} · 뒤 공백 {summary.TrailingWhitespaceCharactersRemoved:N0} · 빈 줄 {summary.CollapsedBlankLines:N0}";
            if (preview) ShowTransformPreview(cleanup.TransformResult, $"{title} · {details}");
            else ApplyTransformDirect(cleanup.TransformResult, $"{title} ({details})");
            return;
        }
        var result = operation switch
        {
            "Duplicate" => _transformService.RemoveDuplicateLines(CurrentDocument.Text, false, CurrentDocument.NewLine),
            "Blank" => _transformService.RemoveBlankLines(CurrentDocument.Text, CurrentDocument.NewLine),
            "Collapse" => _transformService.CollapseBlankLines(CurrentDocument.Text, CurrentDocument.NewLine),
            "Whitespace" => _transformService.TrimWhitespace(CurrentDocument.Text, CurrentDocument.NewLine),
            _ => throw new ArgumentException("지원하지 않는 빠른 정리 기능입니다.", nameof(operation))
        };
        if (preview) ShowTransformPreview(result, title);
        else ApplyTransformDirect(result, title);
    }

    private void RenderFavoriteTools()
    {
        if (FavoriteToolsPanel is null) return;
        FavoriteToolsPanel.Children.Clear();
        foreach (var id in _settings.FavoriteToolIds)
        {
            var tool = TextTools.FirstOrDefault(item => item.Id == id);
            if (tool is null) continue;
            var button = new Button { Content = tool.DisplayName, ToolTip = $"즐겨찾기: {tool.DisplayName}", Height = 28, Padding = new Thickness(8, 2, 8, 2), Tag = tool };
            button.Click += FavoriteTool_Click;
            FavoriteToolsPanel.Children.Add(button);
        }
    }

    private void FavoriteToolsEdit_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var tool in TextTools)
        {
            var item = new MenuItem { Header = tool.DisplayName, IsCheckable = true, IsChecked = _settings.FavoriteToolIds.Contains(tool.Id), Tag = tool };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) { if (!_settings.FavoriteToolIds.Contains(tool.Id)) _settings.FavoriteToolIds.Add(tool.Id); }
                else _settings.FavoriteToolIds.Remove(tool.Id);
                RenderFavoriteTools(); MarkSettingsDirty();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = FavoriteToolsEditButton; menu.IsOpen = true;
    }

    private void FavoriteTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolDescriptor tool) return;
        ToolsMenuItem.IsChecked = true; ToggleTools_Click(ToolsMenuItem, new RoutedEventArgs()); ToolTabs.SelectedIndex = 1;
        if (tool.QuickOperation is not null) { RunQuickTransform(tool.QuickOperation, tool.DisplayName); return; }
        if (tool.OperationIndex is int index)
        {
            TrimOperationCombo.SelectedIndex = index;
            (index switch { <= 3 => StartMarkerBox, <= 5 => CharacterCountBox, <= 9 => ValueInputBox, 10 => StartNumberBox, _ => LineNumberSeparatorBox }).Focus();
            return;
        }
        if (tool.Id == TextToolIds.RemoveLinesContaining) DeleteContainingBox.Focus();
        else if (tool.Id == TextToolIds.Replace) ReplaceFromBox.Focus();
    }

    private void PreviewDeleteContaining_Click(object sender, RoutedEventArgs e)
        => RunDeleteContaining(preview: true);

    private void ApplyDeleteContainingDirect_Click(object sender, RoutedEventArgs e)
        => RunDeleteContaining(preview: false);

    private void RunDeleteContaining(bool preview)
    {
        if (CurrentDocument is null) return;
        if (string.IsNullOrEmpty(DeleteContainingBox.Text)) { ShowInputMessage("삭제할 줄에 포함된 문장을 입력하세요.", DeleteContainingBox); return; }
        var result = _transformService.RemoveLinesContaining(CurrentDocument.Text, DeleteContainingBox.Text,
            DeleteContainingMatchCaseCheck.IsChecked == true, CurrentDocument.NewLine);
        if (preview) ShowTransformPreview(result, "특정 문장 포함 줄 삭제");
        else ApplyTransformDirect(result, "특정 문장 포함 줄 삭제");
    }

    private void PreviewReplace_Click(object sender, RoutedEventArgs e)
        => RunReplace(preview: true);

    private void ApplyReplaceDirect_Click(object sender, RoutedEventArgs e)
        => RunReplace(preview: false);

    private void RunReplace(bool preview)
    {
        if (CurrentDocument is null) return;
        if (string.IsNullOrEmpty(ReplaceFromBox.Text)) { ShowInputMessage("찾을 텍스트를 입력하세요.", ReplaceFromBox); return; }
        var result = _transformService.Replace(CurrentDocument.Text, ReplaceFromBox.Text, ReplaceToBox.Text, false, CurrentDocument.NewLine);
        if (preview) ShowTransformPreview(result, "치환 미리보기");
        else ApplyTransformDirect(result, "일괄 치환");
    }

    private void ApplyTransformDirect(TextTransformResult result, string title)
    {
        if (CurrentDocument is null) return;
        if (result.Summary.ChangedLines == 0)
        {
            StatusMessage.Text = $"{title}: 변경할 내용이 없습니다.";
            return;
        }

        ClearTransformPreview();
        CurrentEditor?.ReplaceAll(result.Text);
        StatusMessage.Text = $"{title}: {result.Summary.ChangedLines:N0}개 항목을 적용했습니다. Ctrl+Z로 되돌릴 수 있습니다.";
        UpdateStatus();
    }

    private void ShowTransformPreview(TextTransformResult result, string title)
    {
        _allPreviewRows.Clear();
        PreviewRows.Clear();
        foreach (var item in result.Preview)
            _allPreviewRows.Add(new TransformPreviewRow(item.LineNumber, item.OriginalText, item.ResultText, item.Status switch { TextChangeStatus.Changed => "변경", TextChangeStatus.Skipped => "건너뜀", _ => "유지" }));
        ApplyPreviewFilter();
        _pendingTransformedText = result.Text;
        CaptureResultSource();
        ResultHeader.Text = $"{title}  ·  변경 {result.Summary.ChangedLines:N0} / 건너뜀 {result.Summary.SkippedLines:N0} / 빈 줄 {result.Summary.EmptyResultLines:N0}";
        SearchResultsView.Visibility = Visibility.Collapsed;
        TransformPreviewView.Visibility = Visibility.Visible;
        ApplyTransformButton.Visibility = Visibility.Visible;
        ApplyTransformButton.IsEnabled = result.Summary.ChangedLines > 0;
        ResultPanel.Visibility = Visibility.Visible;
        ResultRow.MinHeight = 120;
        ResultSplitterRow.Height = new GridLength(5);
        ResultsMenuItem.IsChecked = true;
        ResultRow.Height = new GridLength(Math.Max(180, _settings.ResultPanelHeight));
        StatusMessage.Text = "변경 전과 변경 후를 확인한 다음 적용하세요.";
    }

    private void ApplyTransform_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateResultSource() || CurrentDocument is null || _pendingTransformedText is null) return;
        var editor = CurrentEditor;
        _resultDocument = null;
        if (editor is not null)
            editor.ReplaceAll(_pendingTransformedText);
        _pendingTransformedText = null;
        ApplyTransformButton.Visibility = Visibility.Visible;
        ApplyTransformButton.IsEnabled = false;
        StatusMessage.Text = "텍스트 변경을 적용했습니다. Ctrl+Z로 되돌릴 수 있습니다.";
        UpdateStatus();
    }

    private void ShowSearchResults()
    {
        SearchResultsView.Visibility = Visibility.Visible;
        TransformPreviewView.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultRow.MinHeight = 120;
        ResultSplitterRow.Height = new GridLength(5);
        ResultsMenuItem.IsChecked = true;
        ResultRow.Height = new GridLength(Math.Max(120, _settings.ResultPanelHeight));
    }

    private void PreviewFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewRows is null) return;
        ApplyPreviewFilter();
    }

    private void ApplyPreviewFilter()
    {
        var filter = (PreviewFilterCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "전체";
        PreviewRows.Clear();
        foreach (var row in _allPreviewRows.Where(row => filter == "전체" || row.Status == filter)) PreviewRows.Add(row);
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        ClearTransformPreview();
        StatusMessage.Text = "변경 미리보기를 닫았습니다.";
    }

    private void ClearTransformPreview()
    {
        PreviewRows.Clear();
        _allPreviewRows.Clear();
        _pendingTransformedText = null;
        _resultDocument = null;
        _resultSourceRevision = 0;
        _resultInvalidated = false;
        TransformPreviewView.Visibility = Visibility.Collapsed;
        if (SearchSessions.Count > 0) ShowSearchResults(); else HideResultPanelIfEmpty();
    }

    private void HideResultPanelIfEmpty()
    {
        if (SearchSessions.Count > 0 || TransformPreviewView.Visibility == Visibility.Visible) return;
        ResultPanel.Visibility = Visibility.Collapsed; ResultRow.MinHeight = 0; ResultRow.Height = new GridLength(0);
        ResultSplitterRow.Height = new GridLength(0); ResultsMenuItem.IsChecked = false;
    }

    private void CaptureResultSource()
    {
        _resultDocument = CurrentDocument;
        _resultSourceRevision = CurrentDocument?.ContentRevision ?? 0;
        _resultInvalidated = false;
    }

    private bool TryValidateResultSource()
    {
        var valid = !_resultInvalidated && CurrentDocument is not null && CurrentDocument == _resultDocument && CurrentDocument.ContentRevision == _resultSourceRevision;
        if (valid) return true;
        InvalidateResults("문서가 변경되어 이 결과를 적용할 수 없습니다. 검색 또는 미리보기를 다시 실행하세요.");
        MessageBox.Show(this, "결과를 만든 뒤 문서나 원문이 변경되었습니다.\n현재 문서에서 검색 또는 미리보기를 다시 실행해 주세요.", "결과가 만료됨", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void InvalidateResults(string message)
    {
        if (_resultDocument is null) return;
        _resultInvalidated = true;
        _pendingTransformedText = null;
        ApplyTransformButton.Visibility = Visibility.Visible;
        ApplyTransformButton.IsEnabled = false;
        ResultHeader.Text = "결과가 만료되었습니다  ·  다시 실행 필요";
        StatusMessage.Text = message;
    }

    private void ToggleTools_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as MenuItem)?.IsChecked == true;
        ToolColumn.MinWidth = show ? 280 : 0;
        ToolColumn.Width = show ? new GridLength(Math.Max(280, _settings.ToolPanelWidth)) : new GridLength(0);
        ToolSplitterColumn.Width = show ? new GridLength(5) : new GridLength(0);
        ToolPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MarkSettingsDirty();
    }

    private void ToggleResults_Click(object sender, RoutedEventArgs e)
    {
        var show = (sender as MenuItem)?.IsChecked == true;
        ResultRow.MinHeight = show ? 120 : 0;
        ResultRow.Height = show ? new GridLength(Math.Max(120, _settings.ResultPanelHeight)) : new GridLength(0);
        ResultSplitterRow.Height = show ? new GridLength(5) : new GridLength(0);
        ResultPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MarkSettingsDirty();
    }

    private void PanelSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => MarkSettingsDirty();

    private void LightTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light");
    private void DarkTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark");
    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme(_settings.Theme == "Dark" ? "Light" : "Dark");

    private void ApplyTheme(string theme)
    {
        _settings.Theme = theme == "Dark" ? "Dark" : "Light";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{_settings.Theme}.xaml", UriKind.Relative) };
        foreach (var document in Documents) ApplyEditorAppearance(document.Editor);
        MarkSettingsDirty();
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || FontSizeCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        ApplyEditorFontSize(size, OverflowFontSizeCombo);
    }

    private void OverflowFontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || OverflowFontSizeCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        ApplyEditorFontSize(size, FontSizeCombo);
    }

    private void ApplyEditorFontSize(double size, ComboBox counterpart)
    {
        _settings.EditorFontSize = size;
        Application.Current.Resources["EditorFontSize"] = size;
        SelectComboItem(counterpart, size.ToString("0"));
        foreach (var document in Documents) ApplyEditorAppearance(document.Editor);
        MarkSettingsDirty();
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyEditorFont((FontFamilyCombo.SelectedItem as FontChoice)?.FamilyName ?? FontFamilyCombo.SelectedItem?.ToString());
    }

    private void FontFamilyCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyEditorFont(FontFamilyCombo.Text);

    private void OverflowFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyEditorFont((OverflowFontFamilyCombo.SelectedItem as FontChoice)?.FamilyName ?? OverflowFontFamilyCombo.SelectedItem?.ToString());
    }

    private void OverflowFontFamilyCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyEditorFont(OverflowFontFamilyCombo.Text);

    private void ApplyEditorFont(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return;
        var installed = FontFamilyCombo.Items.Cast<FontChoice>().FirstOrDefault(item =>
            string.Equals(item.FamilyName, familyName.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            string.Equals(item.DisplayName, familyName.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            familyName.Trim() == "맑은 고딕" && item.FamilyName == "Malgun Gothic");
        if (installed is null) return;
        _settings.EditorFontFamily = installed.FamilyName;
        FontFamilyCombo.SelectedItem = installed;
        OverflowFontFamilyCombo.SelectedItem = installed;
        Application.Current.Resources["EditorFontFamily"] = new FontFamily(installed.FamilyName);
        foreach (var document in Documents) ApplyEditorAppearance(document.Editor);
        MarkSettingsDirty();
    }

    private void ApplyEditorAppearance(ScintillaEditorHost editor)
    {
        var dpiScale = IsLoaded ? (float)VisualTreeHelper.GetDpi(this).DpiScaleX : 1f;
        editor.ApplyAppearance(_settings.EditorFontFamily, (float)_settings.EditorFontSize, _settings.Theme == "Dark", dpiScale);
    }

    private void Window_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        foreach (var document in Documents) document.Editor.ApplyAppearance(_settings.EditorFontFamily, (float)_settings.EditorFontSize, _settings.Theme == "Dark", (float)e.NewDpi.DpiScaleX);
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) await OpenFilesAsync(paths);
    }

    private void LoadInstalledFonts()
    {
        (string Display, string FamilyName, string[] Aliases)[] preferredAliases =
        {
            ("맑은 고딕 (Malgun Gothic)", "Malgun Gothic", ["Malgun Gothic", "맑은 고딕"]), ("D2Coding", "D2Coding", ["D2Coding"]),
            ("나눔고딕 (NanumGothic)", "NanumGothic", ["NanumGothic", "나눔고딕"]), ("Noto Sans KR", "Noto Sans KR", ["Noto Sans KR"]),
            ("Pretendard", "Pretendard", ["Pretendard"]), ("Cascadia Mono", "Cascadia Mono", ["Cascadia Mono"]), ("Consolas", "Consolas", ["Consolas"]),
            ("JetBrains Mono", "JetBrains Mono", ["JetBrains Mono"]), ("굴림 (Gulim)", "Gulim", ["Gulim", "굴림"]),
            ("돋움 (Dotum)", "Dotum", ["Dotum", "돋움"]), ("바탕 (Batang)", "Batang", ["Batang", "바탕"]),
            ("궁서 (Gungsuh)", "Gungsuh", ["Gungsuh", "궁서"]), ("Segoe UI", "Segoe UI", ["Segoe UI"])
        };
        var installed = Fonts.SystemFontFamilies.Select(family => family.Source).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var preferred = preferredAliases
            .Where(item => item.Aliases.Any(alias => installed.Contains(alias, StringComparer.CurrentCultureIgnoreCase)))
            .Select(item => new FontChoice(item.Display, item.FamilyName))
            .ToArray();
        var preferredInstalledNames = preferredAliases.SelectMany(item => item.Aliases).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var orderedFonts = preferred
            .Concat(installed.Where(name => !preferredInstalledNames.Contains(name))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).Select(name => new FontChoice(name, name)))
            .ToArray();
        FontFamilyCombo.ItemsSource = orderedFonts;
        OverflowFontFamilyCombo.ItemsSource = orderedFonts;
        var selected = orderedFonts.FirstOrDefault(item => string.Equals(item.FamilyName, _settings.EditorFontFamily, StringComparison.CurrentCultureIgnoreCase)
            || _settings.EditorFontFamily == "맑은 고딕" && item.FamilyName == "Malgun Gothic")
            ?? orderedFonts.FirstOrDefault(item => item.FamilyName == "Malgun Gothic")
            ?? orderedFonts.FirstOrDefault(item => item.FamilyName == "Consolas")
            ?? orderedFonts.FirstOrDefault()
            ?? new FontChoice("Consolas", "Consolas");
        _settings.EditorFontFamily = selected.FamilyName;
        Application.Current.Resources["EditorFontFamily"] = new FontFamily(selected.FamilyName);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useOverflow = e.NewSize.Width < 1180;
        ToolbarFontPanel.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
        ToolbarOverflowPanel.Visibility = useOverflow ? Visibility.Visible : Visibility.Collapsed;
        if (!useOverflow) ToolbarOverflowPopup.IsOpen = false;
        MarkSettingsDirty();
    }

    private void ToolbarOverflowButton_Click(object sender, RoutedEventArgs e) => ToolbarOverflowPopup.IsOpen = !ToolbarOverflowPopup.IsOpen;

    private void AddRecentFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        _settings.RecentFiles.RemoveAll(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        _settings.RecentFiles.Insert(0, fullPath);
        if (_settings.RecentFiles.Count > 10) _settings.RecentFiles.RemoveRange(10, _settings.RecentFiles.Count - 10);
        BuildRecentFilesMenu();
        MarkSettingsDirty();
    }

    private void BuildRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles.Where(File.Exists))
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, Tag = path };
            item.Click += async (_, _) => await OpenFileAsync(path);
            RecentFilesMenu.Items.Add(item);
        }
        if (RecentFilesMenu.Items.Count == 0) RecentFilesMenu.Items.Add(new MenuItem { Header = "최근 파일 없음", IsEnabled = false });
    }

    private void RestoreWorkState()
    {
        RestoreSearchState(_settings.SearchState);
        var state = _settings.TransformState;
        var selected = TextTools.FirstOrDefault(item => item.Id == state.SelectedToolId && item.OperationIndex is not null);
        TrimOperationCombo.SelectedIndex = selected?.OperationIndex ?? 0;
        StartMarkerBox.Text = state.StartMarker; EndMarkerBox.Text = state.EndMarker;
        KeepStartCheck.IsChecked = state.KeepStartMarker; KeepEndCheck.IsChecked = state.KeepEndMarker;
        TrimMatchCaseCheck.IsChecked = state.MarkerMatchCase;
        CharacterCountBox.Text = state.CharacterCount.ToString(); ValueInputBox.Text = state.Value;
        IncludeBlankLinesCheck.IsChecked = state.IncludeBlankLines; StartNumberBox.Text = state.StartNumber.ToString();
        LineNumberSeparatorBox.Text = state.LineNumberSeparator; ReplaceFromBox.Text = state.ReplaceFrom; ReplaceToBox.Text = state.ReplaceTo;
        DeleteContainingBox.Text = state.RemoveLinesContainingText; DeleteContainingMatchCaseCheck.IsChecked = state.RemoveLinesContainingMatchCase;
    }

    private void CaptureWorkState()
    {
        _settings.SearchState = CaptureSearchState();
        var operationTool = TextTools.First(item => item.OperationIndex == TrimOperationCombo.SelectedIndex);
        _settings.TransformState = new TransformInputState
        {
            SelectedToolId = operationTool.Id, StartMarker = StartMarkerBox.Text, EndMarker = EndMarkerBox.Text,
            KeepStartMarker = KeepStartCheck.IsChecked == true, KeepEndMarker = KeepEndCheck.IsChecked == true,
            MarkerMatchCase = TrimMatchCaseCheck.IsChecked == true,
            CharacterCount = int.TryParse(CharacterCountBox.Text, out var count) ? count : 1,
            Value = ValueInputBox.Text, IncludeBlankLines = IncludeBlankLinesCheck.IsChecked == true,
            StartNumber = int.TryParse(StartNumberBox.Text, out var start) ? start : 1,
            LineNumberSeparator = LineNumberSeparatorBox.Text, ReplaceFrom = ReplaceFromBox.Text, ReplaceTo = ReplaceToBox.Text,
            RemoveLinesContainingText = DeleteContainingBox.Text,
            RemoveLinesContainingMatchCase = DeleteContainingMatchCaseCheck.IsChecked == true
        };
    }

    private void AttachSettingsTracking()
    {
        foreach (var textBox in new[] { StartMarkerBox, EndMarkerBox, CharacterCountBox, ValueInputBox, StartNumberBox,
                     LineNumberSeparatorBox, ReplaceFromBox, ReplaceToBox, DeleteContainingBox })
            textBox.TextChanged += (_, _) => MarkSettingsDirty();
        foreach (var checkBox in new[] { MatchCaseCheck, WholeWordCheck, KeepStartCheck, KeepEndCheck, TrimMatchCaseCheck,
                     IncludeBlankLinesCheck, DeleteContainingMatchCaseCheck })
        {
            checkBox.Checked += (_, _) => MarkSettingsDirty();
            checkBox.Unchecked += (_, _) => MarkSettingsDirty();
        }
        ContextLinesCombo.SelectionChanged += (_, _) => MarkSettingsDirty();
        TrimOperationCombo.SelectionChanged += (_, _) => MarkSettingsDirty();
    }

    private void MarkSettingsDirty()
    {
        if (!_settingsReady || _closingInProgress) return;
        CaptureWorkState();
        _settingsSaveTimer.Stop(); _settingsSaveTimer.Start();
    }

    private bool SaveSettings(bool showError = true)
    {
        if (_settingsReady) CaptureWorkState();
        _settings.WindowWidth = RestoreBounds.Width;
        _settings.WindowHeight = RestoreBounds.Height;
        _settings.WindowLeft = RestoreBounds.Left;
        _settings.WindowTop = RestoreBounds.Top;
        _settings.WindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        _settings.ToolPanelVisible = ToolPanel.Visibility == Visibility.Visible;
        _settings.ResultPanelVisible = ResultPanel.Visibility == Visibility.Visible;
        if (ToolColumn.Width.Value > 0) _settings.ToolPanelWidth = ToolColumn.ActualWidth;
        if (ResultRow.Height.Value > 0) _settings.ResultPanelHeight = ResultRow.ActualHeight;
        var serialized = JsonSerializer.Serialize(_settings);
        if (serialized == _settingsSnapshot) return true;
        var result = SettingsService.TrySave(_settings);
        if (result.Succeeded) { _settingsSnapshot = serialized; return true; }
        StatusMessage.Text = $"설정을 저장하지 못했습니다: {result.Error?.Message}";
        if (showError) MessageBox.Show(this, StatusMessage.Text, "설정 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RestoreWindowPlacement()
    {
        var workArea = SystemParameters.WorkArea;
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
        if (_settings.WindowState == "Maximized") WindowState = WindowState.Maximized;
    }

    private void RestorePanelVisibility()
    {
        ToolsMenuItem.IsChecked = _settings.ToolPanelVisible;
        ToolColumn.MinWidth = _settings.ToolPanelVisible ? 280 : 0;
        ToolColumn.Width = _settings.ToolPanelVisible ? new GridLength(Math.Max(280, _settings.ToolPanelWidth)) : new GridLength(0);
        ToolSplitterColumn.Width = _settings.ToolPanelVisible ? new GridLength(5) : new GridLength(0);
        ToolPanel.Visibility = _settings.ToolPanelVisible ? Visibility.Visible : Visibility.Collapsed;

        ResultsMenuItem.IsChecked = _settings.ResultPanelVisible;
        ResultRow.MinHeight = _settings.ResultPanelVisible ? 120 : 0;
        ResultRow.Height = _settings.ResultPanelVisible ? new GridLength(Math.Max(120, _settings.ResultPanelHeight)) : new GridLength(0);
        ResultSplitterRow.Height = _settings.ResultPanelVisible ? new GridLength(5) : new GridLength(0);
        ResultPanel.Visibility = _settings.ResultPanelVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "조건 검색\n  모두 포함·하나라도 포함·제외 검색어를 입력하세요.\n\n텍스트 정리\n  미리보기로 확인하거나 바로 적용할 수 있습니다. 로그 정리는 표시 제어 코드와 불필요한 공백을 정리합니다.\n\n단축키\n  Ctrl+N 새 문서 · Ctrl+O 열기 · Ctrl+S 저장 · Ctrl+W 탭 닫기\n  Ctrl+Tab 탭 이동 · Ctrl+F 찾기 · Ctrl+H 바꾸기 · F3 다음 결과",
        "나만의 텍스트 편집기", MessageBoxButton.OK, MessageBoxImage.Information);

    private static int SelectedNumber(ComboBox comboBox) => int.TryParse((comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var result) ? result : 0;

    private static int CountLines(string text)
    {
        var count = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                count++;
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static void SelectComboItem(ComboBox comboBox, string value)
    {
        foreach (var candidate in comboBox.Items)
        {
            var display = candidate is ComboBoxItem item ? item.Content?.ToString() : candidate?.ToString();
            var family = (candidate as FontChoice)?.FamilyName;
            if (string.Equals(display, value, StringComparison.OrdinalIgnoreCase) || string.Equals(family, value, StringComparison.OrdinalIgnoreCase)
                || value == "맑은 고딕" && family == "Malgun Gothic") { comboBox.SelectedItem = candidate; return; }
        }
    }

}
