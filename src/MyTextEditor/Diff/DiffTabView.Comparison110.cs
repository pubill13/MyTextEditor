using System.Windows;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Diff;

public partial class DiffTabView
{
    private bool _needsComparison = true, _largeModeApplied;
    private (long Revision, string Text)? _leftSnapshot, _rightSnapshot;
    private TextDiffEngine.DiffSnapshot? _leftIndex, _rightIndex;
    private int? _mergeNextLine;
    private DiffSide _mergeNextSide;
    private CancellationTokenSource? _inlineCancellation;
    private readonly Dictionary<(int Block, int Pair), DiffLinePair> _inlineCache = [];
    private bool _inlineRunning;
    private (TextDiffResult? Result, int First, int Count, int InlineVersion) _leftPainted, _rightPainted;
    private int _inlineVersion;

    private void ClearSnapshotCache()
    {
        _leftSnapshot = _rightSnapshot = null;
        _leftIndex = _rightIndex = null;
    }

    private async Task<string> CaptureSideAsync(bool left, CancellationToken token)
    {
        var editor = left ? _leftEditor : _rightEditor;
        var snapshot = left ? _leftSnapshot : _rightSnapshot;
        if (snapshot is { } cached && cached.Revision == editor.ContentRevision) return cached.Text;
        var revision = editor.ContentRevision;
        var text = await editor.CaptureTextAsync(token);
        if (left) { _leftSnapshot = (revision, text); _leftIndex = null; }
        else { _rightSnapshot = (revision, text); _rightIndex = null; }
        return text;
    }

    private void CheckLargeComparison()
    {
        if (_largeModeApplied || Math.Max(_leftEditor.Utf8ByteLength, _rightEditor.Utf8ByteLength) < 30_000_000) return;
        _largeModeApplied = true;
        AutoCompareCheck.IsChecked = false;
        AutoCompareCheck.ToolTip = "30MB 이상 문서: 자동 비교를 껐습니다. 필요하면 다시 켤 수 있습니다.";
    }

    private void AutoCompare_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOptionSummary();
        if (!_initialized) return;
        _recompareTimer.Stop();
        if (AutoCompareCheck.IsChecked == true && _needsComparison && _isActive) _ = CompareNowAsync();
    }
    internal void RequestComparison() => _ = CompareNowAsync();
    private void Recompare_Click(object sender, RoutedEventArgs e) => RequestComparison();
    private void CancelCompare_Click(object sender, RoutedEventArgs e)
    {
        _comparisonCancellation?.Cancel();
        _inlineCancellation?.Cancel();
        _recompareTimer.Stop();
        ++_generation;
        _needsComparison = false;
        CancelCompareButton.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = "비교를 중단했습니다. F5로 다시 비교합니다.";
    }

    private void CompleteMergeNavigation()
    {
        if (_mergeNextLine is not { } line || _result is null) return;
        _mergeNextLine = null;
        var index = -1;
        for (var i = 0; i < _result.Blocks.Count; i++)
            if ((_mergeNextSide == DiffSide.Left ? _result.Blocks[i].LeftStartLine : _result.Blocks[i].RightStartLine) >= line) { index = i; break; }
        if (index < 0) { StatusText.Text = "이후 차이 처리가 완료되었습니다."; return; }
        _currentBlockIndex = index - 1;
        NavigateDifference(1);
    }

    private void PaintComparisonViewport()
    {
        if (_result is null || !_isActive) return;
        var pending = new HashSet<(int Block, int Pair)>();
        PaintSide(_leftEditor, true, pending);
        PaintSide(_rightEditor, false, pending);
        if (pending.Count > 0 && !_inlineRunning) _ = CalculateVisibleInlineAsync(pending.ToArray());
    }

    private void PaintSide(ScintillaEditorHost editor, bool left, HashSet<(int Block, int Pair)> pending)
    {
        var first = editor.FirstVisibleLine + 1;
        var last = first + editor.LinesOnScreen + 2;
        var state = (_result, first, editor.LinesOnScreen, _inlineVersion);
        if (state == (left ? _leftPainted : _rightPainted)) return;
        if (left) _leftPainted = state; else _rightPainted = state;
        var blocks = _result!.Blocks;
        var low = 0; var high = blocks.Count;
        while (low < high)
        {
            var mid = (low + high) / 2; var block = blocks[mid];
            if ((left ? block.LeftStartLine + block.LeftLineCount : block.RightStartLine + block.RightLineCount) <= first) low = mid + 1;
            else high = mid;
        }
        var lines = new List<DiffLineHighlight>();
        var spans = new List<DiffInlineHighlight>();
        for (var b = low; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var start = left ? block.LeftStartLine : block.RightStartLine;
            if (start > last) break;
            var count = left ? block.LeftLineCount : block.RightLineCount;
            if (count == 0) continue;
            var kind = block.Kind == DiffBlockKind.Added ? DiffHighlightKind.Added : block.Kind == DiffBlockKind.Deleted ? DiffHighlightKind.Deleted : DiffHighlightKind.Modified;
            lines.Add(new(Math.Max(first, start) - 1, Math.Max(0, Math.Min(last + 1, start + count) - Math.Max(first, start)), kind));
            var side = left ? DiffSide.Left : DiffSide.Right;
            int pairLow = 0, pairHigh = block.Lines.Count;
            while (pairLow < pairHigh)
            {
                var middle = pairLow + (pairHigh - pairLow) / 2;
                var number = TextDiffEngine.GetLineNumber(block, middle, side);
                if (number is not null && number < first) pairLow = middle + 1;
                else pairHigh = middle;
            }
            for (var i = pairLow; i < block.Lines.Count; i++)
            {
                var visibleNumber = TextDiffEngine.GetLineNumber(block, i, side);
                if (visibleNumber is null || visibleNumber > last) break;
                var key = (b, i);
                if (!_inlineCache.TryGetValue(key, out var pair))
                {
                    if (TextDiffEngine.GetLineLength(block, i, DiffSide.Left) <= 16384 &&
                        TextDiffEngine.GetLineLength(block, i, DiffSide.Right) <= 16384) pending.Add(key);
                    continue;
                }
                var number = left ? pair.LeftLineNumber : pair.RightLineNumber;
                if (number is null || number < first || number > last) continue;
                foreach (var span in left ? pair.LeftChanges : pair.RightChanges)
                {
                    var range = editor.GetLineTextRange(number.Value - 1, span.Start, span.Length);
                    spans.Add(new(range.Start, range.Length, kind));
                }
            }
        }
        editor.SetDiffHighlights(lines, spans);
    }

    private async Task CalculateVisibleInlineAsync((int Block, int Pair)[] keys)
    {
        _inlineRunning = true;
        using var cancel = new CancellationTokenSource();
        _inlineCancellation = cancel;
        var result = _result!;
        var generation = _generation;
        var options = CurrentDiffOptions();
        var acquired = false;
        try
        {
            await _compareGate.WaitAsync(cancel.Token);
            acquired = true;
            var rows = await Task.Run(() => keys.Select(key =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                var pair = result.Blocks[key.Block].Lines[key.Pair];
                // Very long rows retain whole-line shading; avoid expensive native UTF-16 mapping on scroll.
                if (Math.Max(pair.LeftText?.Length ?? 0, pair.RightText?.Length ?? 0) <= 16384)
                    pair = _diffEngine.ComputeInlineChanges(pair, options, cancel.Token);
                return (key, pair);
            }).ToArray(), cancel.Token);
            if (generation != _generation || _resourcesReleased || !_isActive) return;
            if (_inlineCache.Count > 2048) _inlineCache.Clear();
            foreach (var row in rows) _inlineCache[row.key] = row.pair;
            _inlineVersion++;
            RebuildMergeGutter();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_resourcesReleased) StatusText.Text = $"줄 내부 비교 실패: {exception.Message}"; }
        finally
        {
            if (acquired) _compareGate.Release();
            _inlineRunning = false;
            if (ReferenceEquals(_inlineCancellation, cancel)) _inlineCancellation = null;
            if (!_resourcesReleased && _isActive && _result is not null)
            {
                _leftPainted = _rightPainted = default;
                RebuildMergeGutter();
            }
        }
    }
}
