using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfStackPanel = System.Windows.Controls.StackPanel;

namespace MyTextEditor.Diff;

public partial class DiffWindow : Window
{
    private readonly TextDiffEngine _diffEngine = new();
    private readonly TextMergeService _mergeService = new();
    private readonly DocumentFileService _fileService = new();
    private readonly DiffWindowCallbacks _callbacks;
    private DiffAppearance _appearance;
    private readonly DispatcherTimer _recompareTimer;
    private ScintillaEditorHost _leftEditor = null!;
    private ScintillaEditorHost _rightEditor = null!;
    private DiffEndpoint _left;
    private DiffEndpoint _right;
    private TextDiffResult? _result;
    private int _currentBlockIndex = -1;
    private int _generation;
    private bool _updatingEditors;
    private bool _syncingScroll;
    private bool _resourcesReleased;
    private bool _closingApproved;
    private bool _closePromptRunning;
    private bool _leftDirty;
    private bool _rightDirty;

    public DiffWindow(DiffEndpoint left, DiffEndpoint right, DiffWindowOptions options,
        DiffWindowCallbacks callbacks, DiffAppearance appearance)
    {
        InitializeComponent();
        _left = left;
        _right = right;
        _callbacks = callbacks;
        _appearance = appearance;
        Width = Math.Max(MinWidth, options.Width);
        Height = Math.Max(MinHeight, options.Height);
        if (options.Left is { } x && options.Top is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x;
            Top = y;
        }
        IgnoreWhitespaceCheck.IsChecked = options.IgnoreWhitespace;
        IgnoreCaseCheck.IsChecked = options.IgnoreCase;
        IgnoreEmptyLinesCheck.IsChecked = options.IgnoreEmptyLines;
        ScrollSyncCheck.IsChecked = options.ScrollSync;
        _recompareTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _recompareTimer.Tick += RecompareTimer_Tick;
    }

    public event EventHandler? ResourcesReleased;
    public bool HasUnsavedChanges => _leftDirty || _rightDirty;

    public void CancelPreparedClose()
    {
        if (!_resourcesReleased) _closingApproved = false;
    }

    public void RefreshSourceState()
    {
        if (IsLoaded && !_resourcesReleased) CheckStaleSources();
    }

    public void ApplyAppearance(DiffAppearance appearance)
    {
        _appearance = appearance;
        if (!IsLoaded || _resourcesReleased) return;
        _leftEditor.ApplyAppearance(appearance.FontFamily, appearance.FontSize, appearance.DarkTheme, appearance.DpiScale);
        _rightEditor.ApplyAppearance(appearance.FontFamily, appearance.FontSize, appearance.DarkTheme, appearance.DpiScale);
        if (_result is not null) ApplyHighlights(_leftEditor.GetText(), _rightEditor.GetText(), _result);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _leftEditor = CreateEditor(_left);
        _rightEditor = CreateEditor(_right);
        LeftEditorContainer.Content = _leftEditor;
        RightEditorContainer.Content = _rightEditor;
        _leftEditor.RevisionChanged += LeftEditor_RevisionChanged;
        _rightEditor.RevisionChanged += RightEditor_RevisionChanged;
        _leftEditor.CaretChanged += Editor_CaretChanged;
        _rightEditor.CaretChanged += Editor_CaretChanged;
        _leftEditor.VerticalScrolled += LeftEditor_VerticalScrolled;
        _rightEditor.VerticalScrolled += RightEditor_VerticalScrolled;
        _leftEditor.ShortcutRequested += Editor_ShortcutRequested;
        _rightEditor.ShortcutRequested += Editor_ShortcutRequested;
        RefreshEndpointHeaders();
        _ = CompareNowAsync();
    }

    private ScintillaEditorHost CreateEditor(DiffEndpoint endpoint)
    {
        var editor = new ScintillaEditorHost();
        editor.LoadUtf8(Encoding.UTF8.GetBytes(endpoint.Text));
        editor.SetNewLine(endpoint.NewLine);
        editor.IsReadOnly = endpoint.IsReadOnly;
        editor.ApplyAppearance(_appearance.FontFamily, _appearance.FontSize, _appearance.DarkTheme, _appearance.DpiScale);
        return editor;
    }

    private void LeftEditor_RevisionChanged(object? sender, EventArgs e)
    {
        if (_updatingEditors) return;
        _leftDirty = _leftEditor.IsModified;
        EditorChanged();
    }

    private void RightEditor_RevisionChanged(object? sender, EventArgs e)
    {
        if (_updatingEditors) return;
        _rightDirty = _rightEditor.IsModified;
        EditorChanged();
    }

    private void EditorChanged()
    {
        RefreshEndpointHeaders();
        CheckStaleSources();
        _generation++;
        InvalidateDisplayedResult();
        _recompareTimer.Stop();
        _recompareTimer.Start();
        StatusText.Text = "입력이 끝나면 차이를 다시 계산합니다…";
    }

    private async void RecompareTimer_Tick(object? sender, EventArgs e)
    {
        _recompareTimer.Stop();
        await CompareNowAsync();
    }

    private async Task CompareNowAsync()
    {
        if (_resourcesReleased) return;
        var generation = ++_generation;
        var left = _leftEditor.GetText();
        var right = _rightEditor.GetText();
        var options = CurrentDiffOptions();
        InvalidateDisplayedResult();
        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = "차이를 계산하는 중…";
        try
        {
            var result = await Task.Run(() => _diffEngine.Compare(left, right, options));
            if (generation != _generation || _resourcesReleased) return;
            _result = result;
            _currentBlockIndex = result.Blocks.Count == 0 ? -1 : Math.Clamp(_currentBlockIndex, 0, result.Blocks.Count - 1);
            ApplyHighlights(left, right, result);
            RefreshResultState();
            CheckStaleSources();
        }
        catch (Exception exception)
        {
            if (generation == _generation) StatusText.Text = $"비교 실패: {exception.Message}";
        }
        finally
        {
            if (generation == _generation) BusyProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void InvalidateDisplayedResult()
    {
        _result = null;
        _currentBlockIndex = -1;
        if (_leftEditor is not null) _leftEditor.ClearDiffHighlights();
        if (_rightEditor is not null) _rightEditor.ClearDiffHighlights();
        MergeGutterCanvas.Children.Clear();
        PositionText.Text = "0 / 0";
        StatisticsText.Text = string.Empty;
    }

    private DiffOptions CurrentDiffOptions() => new(
        IgnoreWhitespaceCheck.IsChecked == true,
        IgnoreCaseCheck.IsChecked == true,
        IgnoreEmptyLinesCheck.IsChecked == true);

    private void ApplyHighlights(string left, string right, TextDiffResult result)
    {
        var leftLines = new List<DiffLineHighlight>();
        var rightLines = new List<DiffLineHighlight>();
        var leftInline = new List<DiffInlineHighlight>();
        var rightInline = new List<DiffInlineHighlight>();
        foreach (var block in result.Blocks.Where(item => !item.IsTerminalNewLineChange))
        {
            var leftKind = block.Kind == DiffBlockKind.Deleted ? DiffHighlightKind.Deleted : DiffHighlightKind.Modified;
            var rightKind = block.Kind == DiffBlockKind.Added ? DiffHighlightKind.Added : DiffHighlightKind.Modified;
            if (block.LeftLineCount > 0) leftLines.Add(new(block.LeftStartLine - 1, block.LeftLineCount, leftKind));
            if (block.RightLineCount > 0) rightLines.Add(new(block.RightStartLine - 1, block.RightLineCount, rightKind));
            foreach (var pair in block.Lines)
            {
                if (pair.LeftLineNumber is { } leftNumber)
                    foreach (var span in pair.LeftChanges)
                        leftInline.Add(new(_leftEditor.GetLineTextRange(leftNumber - 1, span.Start, span.Length).Start,
                            _leftEditor.GetLineTextRange(leftNumber - 1, span.Start, span.Length).Length, leftKind));
                if (pair.RightLineNumber is { } rightNumber)
                    foreach (var span in pair.RightChanges)
                        rightInline.Add(new(_rightEditor.GetLineTextRange(rightNumber - 1, span.Start, span.Length).Start,
                            _rightEditor.GetLineTextRange(rightNumber - 1, span.Start, span.Length).Length, rightKind));
            }
        }
        _leftEditor.SetDiffHighlights(leftLines, leftInline);
        _rightEditor.SetDiffHighlights(rightLines, rightInline);
    }

    private void RefreshResultState()
    {
        var count = _result?.Blocks.Count ?? 0;
        PositionText.Text = count == 0 ? "0 / 0" : $"{_currentBlockIndex + 1} / {count}";
        StatisticsText.Text = _result is null ? string.Empty : $"추가 {_result.AddedLines}  삭제 {_result.DeletedLines}  수정 {_result.ModifiedLines}";
        StatusText.Text = count == 0 ? "두 내용이 같습니다." : $"차이 {count}개";
        RebuildMergeGutter();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateDifference(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateDifference(1);

    private void NavigateDifference(int delta)
    {
        if (_result is null || _result.Blocks.Count == 0) return;
        _currentBlockIndex = _currentBlockIndex < 0 ? 0 : (_currentBlockIndex + delta + _result.Blocks.Count) % _result.Blocks.Count;
        var block = _result.Blocks[_currentBlockIndex];
        _leftEditor.GoToLine(Math.Max(1, block.LeftStartLine));
        _rightEditor.GoToLine(Math.Max(1, block.RightStartLine));
        RefreshResultState();
    }

    private void MergeCurrent(DiffSide sourceSide)
    {
        if (_result is null || _currentBlockIndex < 0) return;
        var block = _result.Blocks[_currentBlockIndex];
        var target = sourceSide == DiffSide.Left ? _rightEditor : _leftEditor;
        if (target.IsReadOnly) { StatusText.Text = "대상이 읽기 전용이어서 병합할 수 없습니다."; return; }
        var merged = _mergeService.ApplyBlock(_leftEditor.GetText(), _rightEditor.GetText(), block, sourceSide, _left.NewLine, _right.NewLine);
        ReplaceMergedSide(merged);
    }

    private void CopyAll(DiffSide sourceSide)
    {
        var target = sourceSide == DiffSide.Left ? _rightEditor : _leftEditor;
        if (target.IsReadOnly) { StatusText.Text = "대상이 읽기 전용이어서 복사할 수 없습니다."; return; }
        ReplaceMergedSide(_mergeService.ApplyAll(_leftEditor.GetText(), _rightEditor.GetText(), sourceSide, _left.NewLine, _right.NewLine));
    }

    private void ReplaceMergedSide(TextMergeResult merged)
    {
        _updatingEditors = true;
        try
        {
            if (merged.ChangedSide == DiffSide.Left) { _leftEditor.ReplaceAll(merged.LeftText); _leftDirty = true; }
            else { _rightEditor.ReplaceAll(merged.RightText); _rightDirty = true; }
        }
        finally { _updatingEditors = false; }
        RefreshEndpointHeaders();
        _ = CompareNowAsync();
    }

    private void CopyAllToLeft_Click(object sender, RoutedEventArgs e) => CopyAll(DiffSide.Right);
    private void CopyAllToRight_Click(object sender, RoutedEventArgs e) => CopyAll(DiffSide.Left);

    private void RebuildMergeGutter()
    {
        MergeGutterCanvas.Children.Clear();
        if (_result is null || _result.Blocks.Count == 0 || !IsLoaded) return;
        var first = Math.Min(_leftEditor.FirstVisibleLine, _rightEditor.FirstVisibleLine);
        var visible = Math.Max(_leftEditor.LinesOnScreen, _rightEditor.LinesOnScreen);
        var height = Math.Max(1, MergeGutterCanvas.ActualHeight - 100);
        foreach (var block in _result.Blocks.Where(item => !item.IsTerminalNewLineChange))
        {
            var anchor = Math.Min(block.LeftStartLine, block.RightStartLine) - 1;
            if (anchor < first - 1 || anchor > first + visible + 1) continue;
            var top = Math.Clamp((anchor - first) / (double)Math.Max(1, visible) * height, 2, Math.Max(2, height - 28));
            var panel = new WpfStackPanel { Orientation = WpfOrientation.Horizontal, Tag = block.Index };
            var left = new WpfButton { Content = "←", Width = 31, Height = 25, Padding = new Thickness(0), IsEnabled = !_leftEditor.IsReadOnly, Tag = block.Index };
            var right = new WpfButton { Content = "→", Width = 31, Height = 25, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0), IsEnabled = !_rightEditor.IsReadOnly, Tag = block.Index };
            left.Click += MergeBlockToLeft_Click;
            right.Click += MergeBlockToRight_Click;
            panel.Children.Add(left); panel.Children.Add(right);
            Canvas.SetTop(panel, top); Canvas.SetLeft(panel, 1);
            MergeGutterCanvas.Children.Add(panel);
        }
    }

    private void MergeBlockToLeft_Click(object sender, RoutedEventArgs e) { SelectTaggedBlock(sender); MergeCurrent(DiffSide.Right); }
    private void MergeBlockToRight_Click(object sender, RoutedEventArgs e) { SelectTaggedBlock(sender); MergeCurrent(DiffSide.Left); }
    private void SelectTaggedBlock(object sender) { if (sender is FrameworkElement { Tag: int index }) _currentBlockIndex = index; }
    private void MergeGutter_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildMergeGutter();

    private void Editor_CaretChanged(object? sender, EventArgs e)
    {
        if (_result is null) return;
        var line = ReferenceEquals(sender, _leftEditor) ? _leftEditor.CurrentLine + 1 : _rightEditor.CurrentLine + 1;
        var candidate = _result.Blocks.FirstOrDefault(block => ReferenceEquals(sender, _leftEditor)
            ? line >= block.LeftStartLine && line < block.LeftStartLine + Math.Max(1, block.LeftLineCount)
            : line >= block.RightStartLine && line < block.RightStartLine + Math.Max(1, block.RightLineCount));
        if (candidate is not null) { _currentBlockIndex = candidate.Index; PositionText.Text = $"{candidate.Index + 1} / {_result.Blocks.Count}"; }
    }

    private void LeftEditor_VerticalScrolled(object? sender, EventArgs e) => SyncScroll(_leftEditor, _rightEditor, true);
    private void RightEditor_VerticalScrolled(object? sender, EventArgs e) => SyncScroll(_rightEditor, _leftEditor, false);

    private void SyncScroll(ScintillaEditorHost source, ScintillaEditorHost target, bool leftToRight)
    {
        RebuildMergeGutter();
        if (ScrollSyncCheck.IsChecked != true || _syncingScroll || _result is null) return;
        _syncingScroll = true;
        try { target.ScrollToLine(MapScrollLine(source.FirstVisibleLine + 1, leftToRight) - 1); }
        finally { _syncingScroll = false; }
    }

    private int MapScrollLine(int sourceLine, bool leftToRight)
    {
        var anchors = _result?.ScrollAnchors;
        if (anchors is null || anchors.Count == 0) return sourceLine;
        int From(DiffLineAnchor item) => leftToRight ? item.LeftLineNumber : item.RightLineNumber;
        int To(DiffLineAnchor item) => leftToRight ? item.RightLineNumber : item.LeftLineNumber;
        var before = anchors.LastOrDefault(item => From(item) <= sourceLine) ?? anchors[0];
        var after = anchors.FirstOrDefault(item => From(item) >= sourceLine) ?? anchors[^1];
        if (From(after) == From(before)) return To(before);
        var ratio = (sourceLine - From(before)) / (double)(From(after) - From(before));
        return Math.Max(1, (int)Math.Round(To(before) + ratio * (To(after) - To(before))));
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveOptions();
        _ = CompareNowAsync();
    }

    private void ScrollSync_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) SaveOptions(); }

    private void ReadOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _leftEditor.IsReadOnly = LeftReadOnlyCheck.IsChecked == true;
        _rightEditor.IsReadOnly = RightReadOnlyCheck.IsChecked == true;
        _left.IsReadOnly = _leftEditor.IsReadOnly;
        _right.IsReadOnly = _rightEditor.IsReadOnly;
        RebuildMergeGutter();
    }

    private void RefreshEndpointHeaders()
    {
        LeftTitleText.Text = _left.DisplayName + (_leftDirty ? " •" : string.Empty);
        RightTitleText.Text = _right.DisplayName + (_rightDirty ? " •" : string.Empty);
        LeftMetaText.Text = DescribeEndpoint(_left);
        RightMetaText.Text = DescribeEndpoint(_right);
        LeftReadOnlyCheck.IsChecked = _left.IsReadOnly;
        RightReadOnlyCheck.IsChecked = _right.IsReadOnly;
        ApplyLeftSourceButton.Visibility = _left.SourceDocumentId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        ApplyRightSourceButton.Visibility = _right.SourceDocumentId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        Title = $"{_left.DisplayName} ↔ {_right.DisplayName} — Diff / Merge";
    }

    private static string DescribeEndpoint(DiffEndpoint endpoint) => endpoint.Kind switch
    {
        DiffEndpointKind.OpenDocument => endpoint.FilePath ?? "열린 문서",
        DiffEndpointKind.File => endpoint.FilePath ?? "파일",
        DiffEndpointKind.Clipboard => "클립보드 스냅샷 · 읽기 전용",
        _ => "선택 영역 스냅샷 · 읽기 전용"
    };

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        _left.Text = _leftEditor.GetText(); _right.Text = _rightEditor.GetText();
        (_left, _right) = (_right, _left);
        (_leftDirty, _rightDirty) = (_rightDirty, _leftDirty);
        _updatingEditors = true;
        try
        {
            _leftEditor.IsReadOnly = false; _rightEditor.IsReadOnly = false;
            _leftEditor.LoadUtf8(Encoding.UTF8.GetBytes(_left.Text));
            _rightEditor.LoadUtf8(Encoding.UTF8.GetBytes(_right.Text));
            _leftEditor.SetNewLine(_left.NewLine); _rightEditor.SetNewLine(_right.NewLine);
            _leftEditor.IsReadOnly = _left.IsReadOnly; _rightEditor.IsReadOnly = _right.IsReadOnly;
        }
        finally { _updatingEditors = false; }
        RefreshEndpointHeaders();
        _ = CompareNowAsync();
    }

    private void CopyDifference_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _currentBlockIndex < 0) return;
        var block = _result.Blocks[_currentBlockIndex];
        var menu = new ContextMenu();
        menu.Items.Add(CopyMenuItem("왼쪽 복사", BuildBlockText(block, true)));
        menu.Items.Add(CopyMenuItem("오른쪽 복사", BuildBlockText(block, false)));
        menu.Items.Add(CopyMenuItem("양쪽 복사", $"--- LEFT: {_left.DisplayName} ---\n{BuildBlockText(block, true)}\n--- RIGHT: {_right.DisplayName} ---\n{BuildBlockText(block, false)}"));
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private static MenuItem CopyMenuItem(string title, string text)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => WpfClipboard.SetText(text);
        return item;
    }

    private static string BuildBlockText(DiffBlock block, bool left) => string.Join('\n', block.Lines
        .Select(line => left ? line.LeftText : line.RightText).Where(text => text is not null));

    private void CompareSelections_Click(object sender, RoutedEventArgs e)
    {
        if (!_leftEditor.HasSelection || !_rightEditor.HasSelection)
        {
            StatusText.Text = "좌우 편집기에서 비교할 영역을 각각 선택하세요.";
            return;
        }
        var leftSelection = new DiffEndpoint { Kind = DiffEndpointKind.Selection, DisplayName = $"{_left.DisplayName} 선택", Text = _leftEditor.SelectedText, IsReadOnly = true, NewLine = _left.NewLine };
        var rightSelection = new DiffEndpoint { Kind = DiffEndpointKind.Selection, DisplayName = $"{_right.DisplayName} 선택", Text = _rightEditor.SelectedText, IsReadOnly = true, NewLine = _right.NewLine };
        if (_callbacks.OpenChildWindow is { } openChild) { openChild(leftSelection, rightSelection); return; }
        var child = new DiffWindow(leftSelection, rightSelection,
            CurrentWindowOptions() with { Left = null, Top = null }, _callbacks, _appearance) { Owner = this };
        child.Show();
    }

    private void CheckStaleSources()
    {
        var leftStale = _left.SourceDocumentId is { } leftId && _callbacks.GetSourceRevision(leftId) != _left.SourceRevision;
        var rightStale = _right.SourceDocumentId is { } rightId && _callbacks.GetSourceRevision(rightId) != _right.SourceRevision;
        StaleBanner.Visibility = leftStale || rightStale ? Visibility.Visible : Visibility.Collapsed;
        StaleText.Text = leftStale && rightStale ? "좌우 원본 문서가 변경되었습니다." : leftStale ? "왼쪽 원본 문서가 변경되었습니다." : "오른쪽 원본 문서가 변경되었습니다.";
    }

    private void ApplyLeftSource_Click(object sender, RoutedEventArgs e) => _ = ApplyToSourceAsync(true, false);
    private void ApplyRightSource_Click(object sender, RoutedEventArgs e) => _ = ApplyToSourceAsync(false, false);

    private async Task<bool> ApplyToSourceAsync(bool left, bool force)
    {
        var endpoint = left ? _left : _right;
        var editor = left ? _leftEditor : _rightEditor;
        if (endpoint.SourceDocumentId is not { } id) return false;
        var applied = await _callbacks.ApplyToSourceAsync(id, editor.GetText(), endpoint.SourceRevision, force);
        if (applied)
        {
            endpoint.SourceRevision = _callbacks.GetSourceRevision(id);
            if (left) _leftDirty = false; else _rightDirty = false;
            editor.MarkSaved();
            RefreshEndpointHeaders(); CheckStaleSources();
            StatusText.Text = "원본 탭에 반영했습니다. 디스크에는 아직 저장하지 않았습니다.";
        }
        else { CheckStaleSources(); StatusText.Text = "원본이 변경되어 반영하지 않았습니다."; }
        return applied;
    }

    private async void ReloadStale_Click(object sender, RoutedEventArgs e)
    {
        if (_left.SourceDocumentId is { } leftId && _callbacks.GetSourceRevision(leftId) != _left.SourceRevision)
            if (!ReloadSource(true, leftId)) return;
        if (_right.SourceDocumentId is { } rightId && _callbacks.GetSourceRevision(rightId) != _right.SourceRevision)
            if (!ReloadSource(false, rightId)) return;
        await CompareNowAsync();
    }

    private bool ReloadSource(bool left, Guid documentId)
    {
        var dirty = left ? _leftDirty : _rightDirty;
        if (dirty && WpfMessageBox.Show(this, "이쪽 Diff 버퍼의 변경을 버리고 최신 원본을 불러올까요?", "원본 다시 불러오기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        var snapshot = _callbacks.GetSourceSnapshot(documentId);
        if (snapshot is null) { StatusText.Text = "원본 문서가 이미 닫혀 다시 불러올 수 없습니다."; return false; }
        var endpoint = left ? _left : _right;
        var editor = left ? _leftEditor : _rightEditor;
        endpoint.DisplayName = snapshot.DisplayName; endpoint.Text = snapshot.Text; endpoint.FilePath = snapshot.FilePath;
        endpoint.Encoding = snapshot.Encoding; endpoint.HasByteOrderMark = snapshot.HasByteOrderMark;
        endpoint.NewLine = snapshot.NewLine; endpoint.SourceRevision = snapshot.Revision;
        _updatingEditors = true;
        try
        {
            var readOnly = editor.IsReadOnly; editor.IsReadOnly = false;
            editor.LoadUtf8(Encoding.UTF8.GetBytes(snapshot.Text)); editor.SetNewLine(snapshot.NewLine);
            editor.IsReadOnly = readOnly;
        }
        finally { _updatingEditors = false; }
        if (left) _leftDirty = false; else _rightDirty = false;
        RefreshEndpointHeaders(); CheckStaleSources();
        return true;
    }

    private async void ForceApplyStale_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show(this, "원본 탭의 최신 내용을 현재 Diff 내용으로 교체할까요?", "원본 강제 교체", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_left.SourceDocumentId is { } leftId && _callbacks.GetSourceRevision(leftId) != _left.SourceRevision) await ApplyToSourceAsync(true, true);
        if (_right.SourceDocumentId is { } rightId && _callbacks.GetSourceRevision(rightId) != _right.SourceRevision) await ApplyToSourceAsync(false, true);
    }

    private void SaveLeft_Click(object sender, RoutedEventArgs e) => _ = SaveEndpointAsync(true);
    private void SaveRight_Click(object sender, RoutedEventArgs e) => _ = SaveEndpointAsync(false);
    private void ExtractLeft_Click(object sender, RoutedEventArgs e) => _ = _callbacks.CreateDocumentAsync($"{_left.DisplayName} 추출", _leftEditor.GetText());
    private void ExtractRight_Click(object sender, RoutedEventArgs e) => _ = _callbacks.CreateDocumentAsync($"{_right.DisplayName} 추출", _rightEditor.GetText());

    private async Task<bool> SaveEndpointAsync(bool left)
    {
        var endpoint = left ? _left : _right;
        var editor = left ? _leftEditor : _rightEditor;
        if (endpoint.SourceDocumentId is { } id)
        {
            if (!await ApplyToSourceAsync(left, false)) return false;
            return await _callbacks.SaveSourceAsync(id);
        }
        var destination = endpoint.FilePath;
        if (endpoint.Kind == DiffEndpointKind.File && destination is not null && HasExternalChange(endpoint))
        {
            var choice = WpfMessageBox.Show(this, "파일이 외부에서 변경되었습니다.\n\n예: 덮어쓰기   아니요: 다른 이름으로 저장", "외부 변경 감지", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return false;
            if (choice == MessageBoxResult.No) destination = null;
        }
        if (destination is null)
        {
            var dialog = new WpfSaveFileDialog { FileName = endpoint.DisplayName, Filter = "텍스트 파일|*.txt|모든 파일|*.*" };
            if (dialog.ShowDialog(this) != true) return false;
            destination = dialog.FileName;
        }
        var state = new DocumentState
        {
            FilePath = destination, Text = editor.GetText(), Encoding = endpoint.Encoding,
            HasByteOrderMark = endpoint.HasByteOrderMark, NewLine = endpoint.NewLine, IsModified = true
        };
        try
        {
            await _fileService.SaveAsync(state, destination);
            var info = new FileInfo(destination);
            endpoint.FilePath = info.FullName; endpoint.DisplayName = info.Name;
            endpoint.SourceFileLength = info.Length; endpoint.SourceFileLastWriteUtc = info.LastWriteTimeUtc;
            endpoint.Text = editor.GetText(); editor.MarkSaved();
            if (left) _leftDirty = false; else _rightDirty = false;
            RefreshEndpointHeaders(); StatusText.Text = $"저장됨: {destination}";
            return true;
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static bool HasExternalChange(DiffEndpoint endpoint)
    {
        if (endpoint.FilePath is null || endpoint.SourceFileLength is null || endpoint.SourceFileLastWriteUtc is null) return false;
        var info = new FileInfo(endpoint.FilePath);
        return !info.Exists || info.Length != endpoint.SourceFileLength || info.LastWriteTimeUtc != endpoint.SourceFileLastWriteUtc;
    }

    private void Editor_ShortcutRequested(object? sender, EditorShortcutEventArgs e)
    {
        switch (e.Shortcut)
        {
            case EditorShortcut.DiffPrevious: NavigateDifference(-1); e.Handled = true; break;
            case EditorShortcut.DiffNext: NavigateDifference(1); e.Handled = true; break;
            case EditorShortcut.MergeLeft: MergeCurrent(DiffSide.Right); e.Handled = true; break;
            case EditorShortcut.MergeRight: MergeCurrent(DiffSide.Left); e.Handled = true; break;
            case EditorShortcut.SaveDocument: _ = SaveEndpointAsync(ReferenceEquals(sender, _leftEditor)); e.Handled = true; break;
        }
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
        if (e.Key == Key.Up) { NavigateDifference(-1); e.Handled = true; }
        else if (e.Key == Key.Down) { NavigateDifference(1); e.Handled = true; }
        else if (e.Key == Key.Left) { MergeCurrent(DiffSide.Right); e.Handled = true; }
        else if (e.Key == Key.Right) { MergeCurrent(DiffSide.Left); e.Handled = true; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApproved) { ReleaseResources(); return; }
        if (_closePromptRunning) { e.Cancel = true; return; }
        if (!HasUnsavedChanges) { ReleaseResources(); return; }
        e.Cancel = true;
        _closePromptRunning = true;
        try
        {
            if (await RequestCloseAsync()) Close();
        }
        finally { _closePromptRunning = false; }
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_closingApproved) return true;
        if (!HasUnsavedChanges) { _closingApproved = true; return true; }
        var choice = WpfMessageBox.Show(this, "Diff 창에서 수정한 내용을 저장할까요?\n\n예: 수정된 양쪽 저장   아니요: 버리기", "Diff / Merge 닫기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes)
        {
            if (_leftDirty && !await SaveEndpointAsync(true)) return false;
            if (_rightDirty && !await SaveEndpointAsync(false)) return false;
        }
        _closingApproved = true;
        return true;
    }

    private void ReleaseResources()
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        _generation++;
        _recompareTimer.Stop();
        _recompareTimer.Tick -= RecompareTimer_Tick;
        if (_leftEditor is not null) _leftEditor.ReleaseResources();
        if (_rightEditor is not null) _rightEditor.ReleaseResources();
        ResourcesReleased?.Invoke(this, EventArgs.Empty);
        ResourcesReleased = null;
    }

    private void Window_PlacementChanged(object? sender, EventArgs e) { if (IsLoaded) SaveOptions(); }

    private DiffWindowOptions CurrentWindowOptions() => new(
        IgnoreWhitespaceCheck.IsChecked == true, IgnoreCaseCheck.IsChecked == true,
        IgnoreEmptyLinesCheck.IsChecked == true, ScrollSyncCheck.IsChecked == true,
        ActualWidth, ActualHeight, WindowState == WindowState.Normal ? Left : RestoreBounds.Left,
        WindowState == WindowState.Normal ? Top : RestoreBounds.Top);

    private void SaveOptions() => _callbacks.SettingsChanged(CurrentWindowOptions());
}
