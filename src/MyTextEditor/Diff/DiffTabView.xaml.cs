using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using MyTextEditor.Models;
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
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfStackPanel = System.Windows.Controls.StackPanel;

namespace MyTextEditor.Diff;

public partial class DiffTabView : System.Windows.Controls.UserControl
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
    private bool _initialized;
    private bool _leftDirty;
    private bool _rightDirty;
    private bool _isActive;
    private bool _gutterScheduled;
    private bool _highlightsPending;
    private string _highlightColor;
    private readonly List<WpfStackPanel> _gutterPool = [];
    private readonly List<System.Windows.Shapes.Polygon> _gutterConnections = [];
    private readonly SortedSet<int> _visibleBlocks = [];

    public DiffTabView(DiffEndpoint left, DiffEndpoint right, DiffWindowOptions options,
        DiffWindowCallbacks callbacks, DiffAppearance appearance)
    {
        InitializeComponent();
        _left = left;
        _right = right;
        _callbacks = callbacks;
        _highlightColor = callbacks.HighlightColor;
        _appearance = appearance;
        IgnoreWhitespaceCheck.IsChecked = options.IgnoreWhitespace;
        IgnoreCaseCheck.IsChecked = options.IgnoreCase;
        IgnoreEmptyLinesCheck.IsChecked = options.IgnoreEmptyLines;
        ScrollSyncCheck.IsChecked = options.ScrollSync;
        _recompareTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _recompareTimer.Tick += RecompareTimer_Tick;
    }

    public event EventHandler? ResourcesReleased;
    public event EventHandler? StateChanged;
    public event EventHandler? OptionsChanged;
    public event EventHandler<DiffTabRequestedEventArgs>? NewComparisonRequested;
    public event EventHandler<DiffFilesDroppedEventArgs>? FilesDropped;
    public event EventHandler? CloseRequested;
    public event EventHandler? CloseWorkspaceRequested;
    public event EventHandler<DiffTabCycleRequestedEventArgs>? CycleTabRequested;
    public event Action<double>? FontSizeRequested;
    public bool HasUnsavedChanges => _leftDirty || _rightDirty;
    public bool IsCompletelyEmpty => !_left.IsReady && !_right.IsReady && !HasUnsavedChanges;
    public bool LeftIsReady => _left.IsReady;
    public bool RightIsReady => _right.IsReady;
    public bool IsReady => _left.IsReady && _right.IsReady;
    public int DifferenceCount => _result?.Blocks.Count ?? 0;
    public string TabTitle
    {
        get
        {
            var name = IsCompletelyEmpty ? "새 비교" : $"{_left.DisplayName} ↔ {_right.DisplayName}";
            var count = IsReady && _result is not null ? $" ({DifferenceCount})" : string.Empty;
            return name + count + (HasUnsavedChanges ? " •" : string.Empty);
        }
    }

    public void RefreshSourceState()
    {
        if (IsLoaded && !_resourcesReleased) CheckStaleSources();
    }

    public void ApplyAppearance(DiffAppearance appearance)
    {
        _appearance = appearance;
        if (!_initialized || !_isActive || _resourcesReleased) return;
        var palette = appearance.Palette ?? ThemePalette.Get(appearance.DarkTheme ? "Dark" : "Light");
        _leftEditor.ApplyAppearance(appearance.FontFamily, appearance.FontSize, palette, appearance.DpiScale);
        _rightEditor.ApplyAppearance(appearance.FontFamily, appearance.FontSize, palette, appearance.DpiScale);
        RebuildMergeGutter();
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active) { CancelGutterUpdate(); return; }
        ApplyAppearance(_appearance);
        if (_initialized && _highlightsPending && _result is not null)
        {
            ApplyHighlights(_result);
            _highlightsPending = false;
        }
        RebuildMergeGutter();
    }

    public void SetHighlightColor(string color)
    {
        _highlightColor = color;
        if (!_initialized || _resourcesReleased) return;
        _leftEditor.HighlightColor = color;
        _rightEditor.HighlightColor = color;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
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
        _leftEditor.FilesDropped += LeftEditor_FilesDropped;
        _rightEditor.FilesDropped += RightEditor_FilesDropped;
        RefreshReadyState();
        if (IsReady) _ = CompareNowAsync();
    }

    private ScintillaEditorHost CreateEditor(DiffEndpoint endpoint)
    {
        var editor = new ScintillaEditorHost();
        editor.LoadUtf8(Encoding.UTF8.GetBytes(endpoint.Text));
        editor.SetNewLine(endpoint.NewLine);
        editor.IsReadOnly = endpoint.IsReadOnly;
        editor.HighlightColor = _highlightColor;
        editor.HighlightColorChanged += color => _callbacks.HighlightColorChanged?.Invoke(color);
        editor.HighlightFailed += message => StatusText.Text = message;
        editor.ApplyAppearance(_appearance.FontFamily, _appearance.FontSize,
            _appearance.Palette ?? ThemePalette.Get(_appearance.DarkTheme ? "Dark" : "Light"), _appearance.DpiScale);
        return editor;
    }

    private void LeftEditor_RevisionChanged(object? sender, EventArgs e)
    {
        if (_updatingEditors) return;
        _leftDirty = _leftEditor.IsModified;
        PromoteTypedEndpoint(true);
        EditorChanged();
    }

    private void RightEditor_RevisionChanged(object? sender, EventArgs e)
    {
        if (_updatingEditors) return;
        _rightDirty = _rightEditor.IsModified;
        PromoteTypedEndpoint(false);
        EditorChanged();
    }

    private void PromoteTypedEndpoint(bool left)
    {
        var endpoint = left ? _left : _right;
        if (endpoint.IsReady) return;
        var scratch = new DiffEndpoint
        {
            Kind = DiffEndpointKind.Scratch,
            DisplayName = left ? "왼쪽 직접 입력" : "오른쪽 직접 입력",
            Text = string.Empty, NewLine = endpoint.NewLine
        };
        if (left) _left = scratch; else _right = scratch;
        RefreshReadyState();
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
        if (!_initialized) return;
        if (_resourcesReleased || !IsReady) { InvalidateDisplayedResult(); RefreshReadyState(); return; }
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
            _highlightsPending = !_isActive;
            if (_isActive) ApplyHighlights(result);
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
        foreach (var panel in _gutterPool) panel.Visibility = Visibility.Collapsed;
        foreach (var connector in _gutterConnections) connector.Visibility = Visibility.Collapsed;
        BlockRangeText.Text = "내용이 변경되었습니다. 비교가 끝나면 반영 범위가 표시됩니다.";
        PositionText.Text = "0 / 0";
        StatisticsText.Text = string.Empty;
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
    }

    private DiffOptions CurrentDiffOptions() => new(
        IgnoreWhitespaceCheck.IsChecked == true,
        IgnoreCaseCheck.IsChecked == true,
        IgnoreEmptyLinesCheck.IsChecked == true);

    private void ApplyHighlights(TextDiffResult result)
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
        RefreshBlockRange();
        RefreshReadyState();
        RebuildMergeGutter();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => NavigateDifference(-1);

    internal static string DescribeRange(int startLine, int count) => count == 0
        ? startLine <= 1 ? "문서 시작 삽입 위치" : $"{startLine - 1}행 뒤 삽입 위치"
        : count == 1 ? $"{startLine}행" : $"{startLine}–{startLine + count - 1}행 ({count}행)";

    internal static string DescribeMerge(DiffBlock block, DiffSide sourceSide)
    {
        var source = sourceSide == DiffSide.Left ? "왼쪽" : "오른쪽";
        var target = sourceSide == DiffSide.Left ? "오른쪽" : "왼쪽";
        if (block.IsTerminalNewLineChange) return $"{source}의 파일 끝 개행 상태를 {target}에 반영";
        var sourceStart = sourceSide == DiffSide.Left ? block.LeftStartLine : block.RightStartLine;
        var sourceCount = sourceSide == DiffSide.Left ? block.LeftLineCount : block.RightLineCount;
        var targetStart = sourceSide == DiffSide.Left ? block.RightStartLine : block.LeftStartLine;
        var targetCount = sourceSide == DiffSide.Left ? block.RightLineCount : block.LeftLineCount;
        if (sourceCount == 0) return $"{target} {DescribeRange(targetStart, targetCount)} 삭제 ({source}에는 없는 내용)";
        if (targetCount == 0) return $"{source} {DescribeRange(sourceStart, sourceCount)} → {target} {DescribeRange(targetStart, 0)}에 추가";
        return $"{source} {DescribeRange(sourceStart, sourceCount)} → {target} {DescribeRange(targetStart, targetCount)}를 대체";
    }

    private void RefreshBlockRange()
    {
        if (_result is null || _currentBlockIndex < 0 || _currentBlockIndex >= _result.Blocks.Count)
        {
            BlockRangeText.Text = "선택된 차이가 없습니다.";
            return;
        }
        var block = _result.Blocks[_currentBlockIndex];
        BlockRangeText.Text = block.IsTerminalNewLineChange
            ? $"차이 {_currentBlockIndex + 1}: 파일 끝 개행 유무 차이"
            : $"차이 {_currentBlockIndex + 1}: 왼쪽 {DescribeRange(block.LeftStartLine, block.LeftLineCount)}  ↔  오른쪽 {DescribeRange(block.RightStartLine, block.RightLineCount)}";
        BlockRangeText.ToolTip = DescribeMerge(block, DiffSide.Right) + "\n" + DescribeMerge(block, DiffSide.Left) + "\n반영 후 Ctrl+Z로 되돌릴 수 있습니다.";
    }
    private void Next_Click(object sender, RoutedEventArgs e) => NavigateDifference(1);

    internal void NavigateDifference(int delta)
    {
        if (!_isActive || _resourcesReleased || _result is null || _result.Blocks.Count == 0) return;
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
        if (!_isActive || !_initialized || _resourcesReleased || _gutterScheduled) return;
        _gutterScheduled = true;
        CompositionTarget.Rendering += RenderMergeGutter;
    }

    private void CancelGutterUpdate()
    {
        CompositionTarget.Rendering -= RenderMergeGutter;
        _gutterScheduled = false;
    }

    private void RenderMergeGutter(object? sender, EventArgs e)
    {
        CancelGutterUpdate();
        if (!_isActive || _resourcesReleased || _result is null || !IsVisible) return;
        _visibleBlocks.Clear();
        CollectVisibleBlocks(_leftEditor, true);
        CollectVisibleBlocks(_rightEditor, false);
        var used = 0;
        foreach (var index in _visibleBlocks)
        {
            var block = _result.Blocks[index];
            var leftVisible = BlockVisible(block, _leftEditor, true);
            var editor = leftVisible ? _leftEditor : _rightEditor;
            var line = (leftVisible ? block.LeftStartLine : block.RightStartLine) - 1;
            var offset = editor.TranslatePoint(new System.Windows.Point(0, 0), MergeGutterCanvas).Y;
            var top = offset + editor.GetLineY(Math.Clamp(line, editor.FirstVisibleLine, Math.Max(0, editor.LineCount - 1)));
            if (top < 0 || top > MergeGutterCanvas.ActualHeight - 28) continue;
            if (used == _gutterPool.Count) _gutterPool.Add(CreateGutterButtons());
            var panel = _gutterPool[used++];
            panel.Visibility = Visibility.Visible;
            var connector = _gutterConnections[used - 1];
            var leftRange = GetVisibleBlockRange(block.LeftStartLine, block.LeftLineCount, _leftEditor);
            var rightRange = GetVisibleBlockRange(block.RightStartLine, block.RightLineCount, _rightEditor);
            var width = MergeGutterCanvas.ActualWidth;
            connector.Points = new PointCollection
            {
                new(0, leftRange.Top), new(width, rightRange.Top),
                new(width, rightRange.Bottom), new(0, leftRange.Bottom)
            };
            connector.Visibility = Visibility.Visible;
            connector.Opacity = index == _currentBlockIndex ? 0.65 : 0.22;
            connector.StrokeThickness = index == _currentBlockIndex ? 2 : 1;
            var left = (WpfButton)panel.Children[0];
            var right = (WpfButton)panel.Children[1];
            var firstLine = Math.Min(editor.FirstVisibleLine, Math.Max(0, editor.LineCount - 2));
            var lineHeight = editor.LineCount > 1 ? Math.Abs(editor.GetLineY(firstLine + 1) - editor.GetLineY(firstLine)) : 25;
            left.MinHeight = right.MinHeight = 0;
            left.Height = right.Height = Math.Clamp(lineHeight, 10, 25);
            left.FontSize = right.FontSize = Math.Clamp(lineHeight - 2, 7, 14);
            left.Tag = right.Tag = index;
            left.ToolTip = DescribeMerge(block, DiffSide.Right) + "\nCtrl+Z로 되돌리기";
            right.ToolTip = DescribeMerge(block, DiffSide.Left) + "\nCtrl+Z로 되돌리기";
            left.IsEnabled = !_leftEditor.IsReadOnly;
            right.IsEnabled = !_rightEditor.IsReadOnly;
            Canvas.SetTop(panel, top);
        }
        for (var index = used; index < _gutterPool.Count; index++)
        {
            _gutterPool[index].Visibility = Visibility.Collapsed;
            _gutterConnections[index].Visibility = Visibility.Collapsed;
        }
    }

    private (double Top, double Bottom) GetVisibleBlockRange(int start, int count, ScintillaEditorHost editor)
    {
        var offset = editor.TranslatePoint(new System.Windows.Point(0, 0), MergeGutterCanvas).Y;
        var line = Math.Clamp(start - 1, 0, Math.Max(0, editor.LineCount - 1));
        var first = Math.Min(editor.FirstVisibleLine, Math.Max(0, editor.LineCount - 2));
        var height = editor.LineCount > 1 ? Math.Abs(editor.GetLineY(first + 1) - editor.GetLineY(first)) : 18;
        var top = offset + editor.GetLineY(line);
        var bottom = top + (count == 0 ? 2 : Math.Max(1, count) * height);
        return (Math.Clamp(top, 0, MergeGutterCanvas.ActualHeight), Math.Clamp(bottom, 0, MergeGutterCanvas.ActualHeight));
    }

    private WpfStackPanel CreateGutterButtons()
    {
        var panel = new WpfStackPanel { Orientation = WpfOrientation.Horizontal };
        var connector = new System.Windows.Shapes.Polygon { IsHitTestVisible = false };
        connector.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        connector.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        System.Windows.Controls.Panel.SetZIndex(connector, -1);
        MergeGutterCanvas.Children.Add(connector);
        _gutterConnections.Add(connector);
        var left = new WpfButton { Content = "← 반영", Width = 49, Height = 25, Padding = new Thickness(0), ToolTip = "오른쪽 블록을 왼쪽으로 병합" };
        var right = new WpfButton { Content = "반영 →", Width = 49, Height = 25, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0), ToolTip = "왼쪽 블록을 오른쪽으로 병합" };
        left.MouseEnter += GutterButton_MouseEnter;
        right.MouseEnter += GutterButton_MouseEnter;
        left.Click += MergeBlockToLeft_Click;
        right.Click += MergeBlockToRight_Click;
        panel.Children.Add(left); panel.Children.Add(right);
        Canvas.SetLeft(panel, 4);
        MergeGutterCanvas.Children.Add(panel);
        return panel;
    }

    private void GutterButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        SelectTaggedBlock(sender);
        RefreshBlockRange();
        RebuildMergeGutter();
        if (sender is WpfButton button) StatusText.Text = button.ToolTip?.ToString()?.Split('\n')[0] ?? string.Empty;
    }

    private static bool BlockVisible(DiffBlock block, ScintillaEditorHost editor, bool left)
    {
        var first = editor.FirstVisibleLine + 1;
        var start = left ? block.LeftStartLine : block.RightStartLine;
        var count = left ? block.LeftLineCount : block.RightLineCount;
        return start <= first + editor.LinesOnScreen && start + Math.Max(1, count) > first;
    }

    private void CollectVisibleBlocks(ScintillaEditorHost editor, bool left)
    {
        var blocks = _result!.Blocks;
        var first = editor.FirstVisibleLine + 1;
        var last = first + editor.LinesOnScreen;
        var low = 0;
        var high = blocks.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var block = blocks[middle];
            var end = left ? block.LeftStartLine + Math.Max(1, block.LeftLineCount) : block.RightStartLine + Math.Max(1, block.RightLineCount);
            if (end <= first) low = middle + 1; else high = middle;
        }
        for (var index = low; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if ((left ? block.LeftStartLine : block.RightStartLine) > last) break;
            _visibleBlocks.Add(index);
        }
    }

    private void MergeBlockToLeft_Click(object sender, RoutedEventArgs e) { SelectTaggedBlock(sender); MergeCurrent(DiffSide.Right); }
    private void MergeBlockToRight_Click(object sender, RoutedEventArgs e) { SelectTaggedBlock(sender); MergeCurrent(DiffSide.Left); }
    private void SelectTaggedBlock(object sender) { if (sender is FrameworkElement { Tag: int index }) _currentBlockIndex = index; }
    private void MergeGutter_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildMergeGutter();

    private void Editor_CaretChanged(object? sender, EventArgs e)
    {
        if (!_isActive || _result is null) return;
        var left = ReferenceEquals(sender, _leftEditor);
        var line = left ? _leftEditor.CurrentLine + 1 : _rightEditor.CurrentLine + 1;
        var blocks = _result.Blocks;
        var low = 0;
        var high = blocks.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if ((left ? blocks[middle].LeftStartLine : blocks[middle].RightStartLine) <= line) low = middle + 1;
            else high = middle;
        }
        if (low == 0) return;
        var candidate = blocks[low - 1];
        var end = left ? candidate.LeftStartLine + Math.Max(1, candidate.LeftLineCount) : candidate.RightStartLine + Math.Max(1, candidate.RightLineCount);
        if (line < end && _currentBlockIndex != candidate.Index)
        {
            _currentBlockIndex = candidate.Index;
            PositionText.Text = $"{candidate.Index + 1} / {blocks.Count}";
            RefreshBlockRange();
            RebuildMergeGutter();
        }
    }

    private void LeftEditor_VerticalScrolled(object? sender, EventArgs e) => SyncScroll(_leftEditor, _rightEditor, true);
    private void RightEditor_VerticalScrolled(object? sender, EventArgs e) => SyncScroll(_rightEditor, _leftEditor, false);

    private void SyncScroll(ScintillaEditorHost source, ScintillaEditorHost target, bool leftToRight)
    {
        if (!_isActive || _resourcesReleased || _syncingScroll) return;
        RebuildMergeGutter();
        if (ScrollSyncCheck.IsChecked != true || _result is null) return;
        var targetLine = Math.Clamp(MapScrollLine(source.FirstVisibleLine + 1, leftToRight) - 1, 0,
            Math.Max(0, target.LineCount - target.LinesOnScreen));
        if (target.FirstVisibleLine == targetLine) return;
        _syncingScroll = true;
        try { target.ScrollToLine(targetLine); }
        finally { _syncingScroll = false; }
    }

    private int MapScrollLine(int sourceLine, bool leftToRight)
    {
        var anchors = _result?.ScrollAnchors;
        if (anchors is null || anchors.Count == 0) return sourceLine;
        int From(DiffLineAnchor item) => leftToRight ? item.LeftLineNumber : item.RightLineNumber;
        int To(DiffLineAnchor item) => leftToRight ? item.RightLineNumber : item.LeftLineNumber;
        var low = 0;
        var high = anchors.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (From(anchors[middle]) <= sourceLine) low = middle + 1; else high = middle;
        }
        var before = anchors[Math.Max(0, low - 1)];
        var after = anchors[Math.Min(anchors.Count - 1, low)];
        if (From(after) == From(before)) return To(before);
        var ratio = (sourceLine - From(before)) / (double)(From(after) - From(before));
        return Math.Max(1, (int)Math.Round(To(before) + ratio * (To(after) - To(before))));
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        NotifyOptionsChanged();
        _ = CompareNowAsync();
    }

    private void ScrollSync_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) NotifyOptionsChanged(); }

    private void ReadOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _leftEditor.IsReadOnly = LeftReadOnlyCheck.IsChecked == true;
        _rightEditor.IsReadOnly = RightReadOnlyCheck.IsChecked == true;
        _left.IsReadOnly = _leftEditor.IsReadOnly;
        _right.IsReadOnly = _rightEditor.IsReadOnly;
        RefreshReadyState();
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
        RefreshReadyState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeEndpoint(DiffEndpoint endpoint) => endpoint.Kind switch
    {
        DiffEndpointKind.Empty => "직접 입력하거나 파일을 놓으세요",
        DiffEndpointKind.Scratch => "직접 입력 · 저장되지 않은 비교 버퍼",
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
        _leftEditor.RevisionChanged -= LeftEditor_RevisionChanged;
        _rightEditor.RevisionChanged -= RightEditor_RevisionChanged;
        _leftEditor.VerticalScrolled -= LeftEditor_VerticalScrolled;
        _rightEditor.VerticalScrolled -= RightEditor_VerticalScrolled;
        _leftEditor.FilesDropped -= LeftEditor_FilesDropped;
        _rightEditor.FilesDropped -= RightEditor_FilesDropped;
        LeftEditorContainer.Content = null;
        RightEditorContainer.Content = null;
        (_leftEditor, _rightEditor) = (_rightEditor, _leftEditor);
        LeftEditorContainer.Content = _leftEditor;
        RightEditorContainer.Content = _rightEditor;
        _leftEditor.RevisionChanged += LeftEditor_RevisionChanged;
        _rightEditor.RevisionChanged += RightEditor_RevisionChanged;
        _leftEditor.VerticalScrolled += LeftEditor_VerticalScrolled;
        _rightEditor.VerticalScrolled += RightEditor_VerticalScrolled;
        _leftEditor.FilesDropped += LeftEditor_FilesDropped;
        _rightEditor.FilesDropped += RightEditor_FilesDropped;
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
        NewComparisonRequested?.Invoke(this, new DiffTabRequestedEventArgs(leftSelection, rightSelection));
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
        if (dirty && WpfMessageBox.Show(Window.GetWindow(this), "이쪽 Diff 버퍼의 변경을 버리고 최신 원본을 불러올까요?", "원본 다시 불러오기", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
        if (WpfMessageBox.Show(Window.GetWindow(this), "원본 탭의 최신 내용을 현재 Diff 내용으로 교체할까요?", "원본 강제 교체", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
        if (destination is not null && HasExternalChange(endpoint))
        {
            var choice = WpfMessageBox.Show(Window.GetWindow(this), "파일이 외부에서 변경되었습니다.\n\n예: 덮어쓰기   아니요: 다른 이름으로 저장", "외부 변경 감지", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return false;
            if (choice == MessageBoxResult.No) destination = null;
        }
        if (destination is null)
        {
            var dialog = new WpfSaveFileDialog { FileName = endpoint.DisplayName, Filter = "텍스트 파일|*.txt|모든 파일|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
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
            WpfMessageBox.Show(Window.GetWindow(this), exception.Message, "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (!_isActive || _resourcesReleased || e.Handled) return;
        switch (e.Shortcut)
        {
            case EditorShortcut.DiffPrevious: NavigateDifference(-1); e.Handled = true; break;
            case EditorShortcut.DiffNext: NavigateDifference(1); e.Handled = true; break;
            case EditorShortcut.MergeLeft: MergeCurrent(DiffSide.Right); e.Handled = true; break;
            case EditorShortcut.MergeRight: MergeCurrent(DiffSide.Left); e.Handled = true; break;
            case EditorShortcut.SaveDocument: _ = SaveEndpointAsync(ReferenceEquals(sender, _leftEditor)); e.Handled = true; break;
            case EditorShortcut.CloseDocument: CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
            case EditorShortcut.CloseWindow: CloseWorkspaceRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
            case EditorShortcut.NextDocument: CycleTabRequested?.Invoke(this, new(1)); e.Handled = true; break;
            case EditorShortcut.PreviousDocument: CycleTabRequested?.Invoke(this, new(-1)); e.Handled = true; break;
            case EditorShortcut.Help: _callbacks.ShowHelp?.Invoke(); e.Handled = true; break;
            case EditorShortcut.ZoomIn: FontSizeRequested?.Invoke(_appearance.FontSize + 1); e.Handled = true; break;
            case EditorShortcut.ZoomOut: FontSizeRequested?.Invoke(_appearance.FontSize - 1); e.Handled = true; break;
            case EditorShortcut.ZoomReset: FontSizeRequested?.Invoke(11); e.Handled = true; break;
            case EditorShortcut.GoToLine: ((ScintillaEditorHost)sender!).ShowGoToLineDialog(); e.Handled = true; break;
        }
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (!_isActive || _resourcesReleased || e.Handled) return;
        var focused = _leftEditor?.IsEditorFocused == true ? _leftEditor : _rightEditor?.IsEditorFocused == true ? _rightEditor : null;
        if (focused is not null && Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus: case Key.Add: FontSizeRequested?.Invoke(_appearance.FontSize + 1); e.Handled = true; return;
                case Key.OemMinus: case Key.Subtract: FontSizeRequested?.Invoke(_appearance.FontSize - 1); e.Handled = true; return;
                case Key.D0: case Key.NumPad0: FontSizeRequested?.Invoke(11); e.Handled = true; return;
                case Key.G: focused.ShowGoToLineDialog(); e.Handled = true; return;
            }
        }
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Up) { NavigateDifference(-1); e.Handled = true; }
        else if (key == Key.Down) { NavigateDifference(1); e.Handled = true; }
        else if (key == Key.Left) { MergeCurrent(DiffSide.Right); e.Handled = true; }
        else if (key == Key.Right) { MergeCurrent(DiffSide.Left); e.Handled = true; }
    }

    public DiffWindowOptions GetOptions() => new(
        IgnoreWhitespaceCheck.IsChecked == true, IgnoreCaseCheck.IsChecked == true,
        IgnoreEmptyLinesCheck.IsChecked == true, ScrollSyncCheck.IsChecked == true);

    public async Task<bool> RequestCloseAsync()
    {
        if (!HasUnsavedChanges) return true;
        var choice = WpfMessageBox.Show(Window.GetWindow(this),
            $"'{TabTitle}'에서 수정한 내용을 저장할까요?\n\n예: 수정된 양쪽 저장   아니요: 버리기",
            "비교 탭 닫기", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes)
        {
            if (_leftDirty && !await SaveEndpointAsync(true)) return false;
            if (_rightDirty && !await SaveEndpointAsync(false)) return false;
        }
        return true;
    }

    public void PauseComparisonForClose()
    {
        _recompareTimer.Stop();
        ++_generation;
    }

    public void ResumeComparisonAfterCloseCanceled()
    {
        if (!_resourcesReleased && IsReady && _result is null) _recompareTimer.Start();
    }

    public void ReleaseResources()
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        CancelGutterUpdate();
        _gutterPool.Clear();
        _gutterConnections.Clear();
        MergeGutterCanvas.Children.Clear();
        _generation++;
        _recompareTimer.Stop();
        _recompareTimer.Tick -= RecompareTimer_Tick;
        if (_leftEditor is not null) _leftEditor.ReleaseResources();
        if (_rightEditor is not null) _rightEditor.ReleaseResources();
        ResourcesReleased?.Invoke(this, EventArgs.Empty);
        ResourcesReleased = null;
    }

    public async Task<bool> SetEndpointAsync(bool left, DiffEndpoint endpoint, bool confirmReplacement = true)
    {
        if (_resourcesReleased) return false;
        var dirty = left ? _leftDirty : _rightDirty;
        if (confirmReplacement && dirty)
        {
            var choice = WpfMessageBox.Show(Window.GetWindow(this),
                "이쪽 비교 버퍼에 저장하지 않은 변경이 있습니다. 저장한 뒤 소스를 바꿀까요?\n\n예: 저장   아니요: 버리기",
                "비교 소스 바꾸기", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return false;
            if (choice == MessageBoxResult.Yes && !await SaveEndpointAsync(left)) return false;
        }

        if (left) _left = endpoint; else _right = endpoint;
        if (_initialized)
        {
            var editor = left ? _leftEditor : _rightEditor;
            _updatingEditors = true;
            try
            {
                editor.IsReadOnly = false;
                editor.LoadUtf8(Encoding.UTF8.GetBytes(endpoint.Text));
                editor.SetNewLine(endpoint.NewLine);
                editor.IsReadOnly = endpoint.IsReadOnly;
                editor.MarkSaved();
            }
            finally { _updatingEditors = false; }
        }
        if (left) _leftDirty = false; else _rightDirty = false;
        _generation++;
        InvalidateDisplayedResult();
        RefreshEndpointHeaders();
        if (IsReady) await CompareNowAsync();
        return true;
    }

    public static async Task<DiffEndpoint> LoadFileEndpointAsync(string path)
    {
        var state = await new DocumentFileService().LoadAsync(path);
        var info = new FileInfo(path);
        return new DiffEndpoint
        {
            Kind = DiffEndpointKind.File, DisplayName = info.Name, Text = state.Text,
            FilePath = info.FullName, Encoding = state.Encoding, HasByteOrderMark = state.HasByteOrderMark,
            NewLine = state.NewLine, SourceFileLength = info.Length, SourceFileLastWriteUtc = info.LastWriteTimeUtc
        };
    }

    private void RefreshReadyState()
    {
        if (LeftDropZone is null || RightDropZone is null) return;
        LeftDropZone.Visibility = _left.IsReady ? Visibility.Collapsed : Visibility.Visible;
        RightDropZone.Visibility = _right.IsReady ? Visibility.Collapsed : Visibility.Visible;
        SwapButton.IsEnabled = IsReady;
        PreviousButton.IsEnabled = NextButton.IsEnabled = IsReady && _result is { Blocks.Count: > 0 };
        CompareSelectionsButton.IsEnabled = IsReady;
        CopyAllLeftButton.IsEnabled = IsReady && _initialized && !_leftEditor.IsReadOnly;
        CopyAllRightButton.IsEnabled = IsReady && _initialized && !_rightEditor.IsReadOnly;
        CopyDifferenceButton.IsEnabled = IsReady && _result is not null && _result.Blocks.Count > 0;
        if (!IsReady) { StatusText.Text = "좌우 비교 소스를 선택하세요."; BusyProgress.Visibility = Visibility.Collapsed; }
    }

    private void NotifyOptionsChanged() => OptionsChanged?.Invoke(this, EventArgs.Empty);

    private static bool TryGetDroppedFiles(System.Windows.DragEventArgs e, out IReadOnlyList<string> paths)
    {
        paths = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] items
            ? items : Array.Empty<string>();
        return paths.Count > 0;
    }

    private void Root_DragOver(object sender, System.Windows.DragEventArgs e) { e.Effects = TryGetDroppedFiles(e, out _) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; }
    private void Root_Drop(object sender, System.Windows.DragEventArgs e) { if (TryGetDroppedFiles(e, out var paths)) FilesDropped?.Invoke(this, new(paths)); e.Handled = true; }
    private void Left_DragOver(object sender, System.Windows.DragEventArgs e) => Root_DragOver(sender, e);
    private void Right_DragOver(object sender, System.Windows.DragEventArgs e) => Root_DragOver(sender, e);
    private async void Left_Drop(object sender, System.Windows.DragEventArgs e) { if (TryGetDroppedFiles(e, out var paths)) await LoadDroppedSideAsync(true, paths); e.Handled = true; }
    private async void Right_Drop(object sender, System.Windows.DragEventArgs e) { if (TryGetDroppedFiles(e, out var paths)) await LoadDroppedSideAsync(false, paths); e.Handled = true; }

    private async Task LoadDroppedSideAsync(bool left, IReadOnlyList<string> paths)
    {
        if (paths.Count != 1) { FilesDropped?.Invoke(this, new(paths)); return; }
        if (Directory.Exists(paths[0])) { StatusText.Text = "폴더는 비교 소스로 열 수 없습니다."; return; }
        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = $"{(left ? "왼쪽" : "오른쪽")} 파일을 불러오는 중…";
        try { await SetEndpointAsync(left, await LoadFileEndpointAsync(paths[0])); }
        catch (Exception exception) { StatusText.Text = $"파일을 열지 못했습니다: {exception.Message}"; }
        finally { BusyProgress.Visibility = Visibility.Collapsed; }
    }

    private async Task ChooseFileAsync(bool left)
    {
        var dialog = new WpfOpenFileDialog { Filter = "텍스트 파일|*.txt;*.log;*.csv;*.json;*.xml|모든 파일|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = $"{(left ? "왼쪽" : "오른쪽")} 파일을 불러오는 중…";
        try { await SetEndpointAsync(left, await LoadFileEndpointAsync(dialog.FileName)); }
        catch (Exception exception) { StatusText.Text = $"파일을 열지 못했습니다: {exception.Message}"; }
        finally { BusyProgress.Visibility = Visibility.Collapsed; }
    }

    private void LeftEditor_FilesDropped(IReadOnlyList<string> paths) => _ = LoadDroppedSideAsync(true, paths);
    private void RightEditor_FilesDropped(IReadOnlyList<string> paths) => _ = LoadDroppedSideAsync(false, paths);

    private void ChooseDocument(bool left, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        foreach (var snapshot in _callbacks.GetOpenDocuments?.Invoke() ?? [])
        {
            var item = new MenuItem { Header = snapshot.DisplayName, Tag = snapshot };
            item.Click += async (_, _) => await SetEndpointAsync(left, SnapshotToEndpoint((DiffSourceSnapshot)item.Tag));
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "열린 문서가 없습니다", IsEnabled = false });
        menu.IsOpen = true;
    }

    private static DiffEndpoint SnapshotToEndpoint(DiffSourceSnapshot value) => new()
    {
        Kind = DiffEndpointKind.OpenDocument, DisplayName = value.DisplayName, Text = value.Text,
        FilePath = value.FilePath, Encoding = value.Encoding, HasByteOrderMark = value.HasByteOrderMark,
        NewLine = value.NewLine, SourceDocumentId = value.SourceDocumentId, SourceRevision = value.Revision
    };

    private Task UseClipboardAsync(bool left)
    {
        if (!WpfClipboard.ContainsText()) { StatusText.Text = "클립보드에 텍스트가 없습니다."; return Task.CompletedTask; }
        return SetEndpointAsync(left, new DiffEndpoint { Kind = DiffEndpointKind.Clipboard, DisplayName = "클립보드", Text = WpfClipboard.GetText(), IsReadOnly = true });
    }

    private async void ChooseLeftFile_Click(object sender, RoutedEventArgs e) => await ChooseFileAsync(true);
    private async void ChooseRightFile_Click(object sender, RoutedEventArgs e) => await ChooseFileAsync(false);
    private void ChooseLeftDocument_Click(object sender, RoutedEventArgs e) => ChooseDocument(true, (FrameworkElement)sender);
    private void ChooseRightDocument_Click(object sender, RoutedEventArgs e) => ChooseDocument(false, (FrameworkElement)sender);
    private async void UseLeftClipboard_Click(object sender, RoutedEventArgs e) => await UseClipboardAsync(true);
    private async void UseRightClipboard_Click(object sender, RoutedEventArgs e) => await UseClipboardAsync(false);

    private void LeftSourceMenu_Click(object sender, RoutedEventArgs e) => ShowSourceMenu(true, (FrameworkElement)sender);
    private void RightSourceMenu_Click(object sender, RoutedEventArgs e) => ShowSourceMenu(false, (FrameworkElement)sender);

    private void ShowSourceMenu(bool left, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var file = new MenuItem { Header = "파일 선택…" };
        file.Click += async (_, _) => await ChooseFileAsync(left);
        var document = new MenuItem { Header = "열린 문서" };
        document.Click += (_, _) => ChooseDocument(left, document);
        var clipboard = new MenuItem { Header = "클립보드" };
        clipboard.Click += async (_, _) => await UseClipboardAsync(left);
        var clear = new MenuItem { Header = "비우기" };
        clear.Click += async (_, _) => await SetEndpointAsync(left, DiffEndpoint.Empty(left ? "왼쪽" : "오른쪽"));
        menu.Items.Add(file); menu.Items.Add(document); menu.Items.Add(clipboard);
        menu.Items.Add(new Separator()); menu.Items.Add(clear);
        menu.IsOpen = true;
    }
}




