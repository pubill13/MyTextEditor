using System.Windows.Threading;
using System.Threading.Channels;
using ScintillaNET;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;

namespace MyTextEditor.Controls;

public sealed partial class ScintillaEditorHost
{
    private const int UserIndicator = 23;
    private sealed record HighlightRule(EditorTextRange Range, string? Phrase, DrawingColor Color);
    private readonly List<HighlightRule> _highlightRules = [];
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _highlightCancellation;
    private string _highlightColor = "#F2CC60";
    public event Action<string>? HighlightColorChanged;
    public event Action<string>? HighlightFailed;
    public string HighlightColor
    {
        get => _highlightColor;
        set
        {
            if (value?.Length != 7 || value[0] != '#' || !int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _)) return;
            _highlightColor = value.ToUpperInvariant();
        }
    }

    private void InitializeHighlightMenu()
    {
        _contextMenu.Items.Add("실행 취소", null, (_, _) => Undo());
        _contextMenu.Items.Add("다시 실행", null, (_, _) => Redo());
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());
        _contextMenu.Items.Add("잘라내기", null, (_, _) => _editor.Cut());
        _contextMenu.Items.Add("복사", null, (_, _) => _editor.Copy());
        _contextMenu.Items.Add("붙여넣기", null, (_, _) => _editor.Paste());
        _contextMenu.Items.Add("전체 선택", null, (_, _) => SelectAll());
        var highlight = new Forms.ToolStripMenuItem("하이라이트");
        var selected = new Forms.ToolStripMenuItem("선택 영역만 표시");
        var all = new Forms.ToolStripMenuItem("같은 문구 전체 표시");
        AddColorItems(selected, false); AddColorItems(all, true);
        highlight.DropDownItems.Add(selected); highlight.DropDownItems.Add(all);
        highlight.DropDownItems.Add("해당 표시 제거", null, (_, _) => RemoveCurrentHighlight());
        highlight.DropDownItems.Add("현재 편집 영역의 모든 표시 제거", null, (_, _) => ClearUserHighlights());
        _contextMenu.Items.Add(new Forms.ToolStripSeparator()); _contextMenu.Items.Add(highlight);
        _contextMenu.Opening += (_, _) =>
        {
            _contextMenu.Items[0].Enabled = CanUndo && !IsReadOnly;
            _contextMenu.Items[1].Enabled = CanRedo && !IsReadOnly;
            _contextMenu.Items[3].Enabled = HasSelection && !IsReadOnly;
            _contextMenu.Items[4].Enabled = HasSelection;
            _contextMenu.Items[5].Enabled = !IsReadOnly;
            selected.Enabled = all.Enabled = HasSelection;
        };
        var indicator = _editor.Indicators[UserIndicator];
        indicator.Style = IndicatorStyle.FullBox;
        indicator.Flags = IndicatorFlags.ValueFore;
        indicator.Alpha = 100; indicator.OutlineAlpha = 180; indicator.Under = true;
    }

    private void AddColorItems(Forms.ToolStripMenuItem menu, bool all)
    {
        menu.DropDownItems.Add("최근 색상", null, (_, _) => AddSelectionHighlight(all));
        foreach (var (name, hex) in new[] { ("노랑", "#F2CC60"), ("주황", "#F2994A"), ("초록", "#6FCF97"), ("파랑", "#56B4E9"), ("보라", "#BB86FC"), ("분홍", "#F38BA8") })
        {
            var item = new Forms.ToolStripMenuItem(name) { ForeColor = System.Drawing.ColorTranslator.FromHtml(hex) };
            item.Click += (_, _) => { ChooseHighlightColor(hex); AddSelectionHighlight(all); };
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add("사용자 지정 색상…", null, (_, _) =>
        {
            using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(HighlightColor) };
            if (dialog.ShowDialog(_surface.FindForm()) != Forms.DialogResult.OK) return;
            ChooseHighlightColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"); AddSelectionHighlight(all);
        });
    }

    private void ChooseHighlightColor(string color) { HighlightColor = color; HighlightColorChanged?.Invoke(HighlightColor); }

    public void AddSelectionHighlight(bool allOccurrences)
    {
        if (!HasSelection || _resourcesReleased) return;
        _highlightRules.Add(new HighlightRule(SelectionRange, allOccurrences ? SelectedText : null, System.Drawing.ColorTranslator.FromHtml(HighlightColor)));
        ScheduleHighlightRefresh(immediate: true);
    }

    public void ClearUserHighlights()
    {
        _highlightTimer.Stop(); _highlightCancellation?.Cancel();
        _highlightRules.Clear();
        if (!_editor.IsDisposed) { _editor.IndicatorCurrent = UserIndicator; _editor.IndicatorClearRange(0, _editor.TextLength); }
    }

    public void RemoveCurrentHighlight()
    {
        var selected = SelectionRange;
        // Removing a repeated phrase removes its rule, including every occurrence.
        var text = _highlightRules.Any(x => x.Phrase is not null) ? GetText() : string.Empty;
        for (var i = _highlightRules.Count - 1; i >= 0; i--)
        {
            var rule = _highlightRules[i];
            var hit = rule.Phrase is null ? Touches(rule.Range, selected) : PhraseTouches(text, rule.Phrase, selected);
            if (!hit) continue;
            _highlightRules.RemoveAt(i); break;
        }
        ScheduleHighlightRefresh(immediate: true);
    }

    private static bool Touches(EditorTextRange mark, EditorTextRange selection) => selection.Length == 0
        ? selection.Start >= mark.Start && selection.Start < mark.End
        : mark.Start < selection.End && selection.Start < mark.End;
    private static bool PhraseTouches(string text, string phrase, EditorTextRange range)
    {
        var start = Math.Max(0, range.Start - phrase.Length + 1);
        var end = Math.Min(text.Length, Math.Max(range.End, range.Start + 1) + phrase.Length - 1);
        for (var p = start; p <= end - phrase.Length; p++)
            if (text.AsSpan(p, phrase.Length).SequenceEqual(phrase) && Touches(new(p, phrase.Length), range)) return true;
        return false;
    }

    private void Highlight_BeforeInsert(object? sender, BeforeModificationEventArgs e)
    {
        if (_loading || _highlightRules.Count == 0) return;
        var length = e.Text?.Length ?? 0;
        for (var i = _highlightRules.Count - 1; i >= 0; i--)
        {
            var rule = _highlightRules[i]; if (rule.Phrase is not null) continue;
            if (e.Position > rule.Range.Start && e.Position < rule.Range.End) _highlightRules.RemoveAt(i);
            else if (e.Position <= rule.Range.Start) _highlightRules[i] = rule with { Range = rule.Range with { Start = rule.Range.Start + length } };
        }
    }

    private void Highlight_BeforeDelete(object? sender, BeforeModificationEventArgs e)
    {
        if (_loading || _highlightRules.Count == 0) return;
        var length = e.Text?.Length ?? 0; var end = e.Position + length;
        for (var i = _highlightRules.Count - 1; i >= 0; i--)
        {
            var rule = _highlightRules[i]; if (rule.Phrase is not null) continue;
            if (e.Position < rule.Range.End && end > rule.Range.Start) _highlightRules.RemoveAt(i);
            else if (end <= rule.Range.Start) _highlightRules[i] = rule with { Range = rule.Range with { Start = rule.Range.Start - length } };
        }
    }

    private void ScheduleHighlightRefresh(bool immediate = false)
    {
        if (_resourcesReleased) return;
        _highlightCancellation?.Cancel(); _highlightTimer.Stop();
        _editor.IndicatorCurrent = UserIndicator; _editor.IndicatorClearRange(0, _editor.TextLength);
        if (immediate) _ = RefreshHighlightsAsync();
        else if (_highlightRules.Count > 0) _highlightTimer.Start();
        else { _editor.IndicatorCurrent = UserIndicator; _editor.IndicatorClearRange(0, _editor.TextLength); }
    }
    private void HighlightTimer_Tick(object? sender, EventArgs e) { _highlightTimer.Stop(); _ = RefreshHighlightsAsync(); }

    private async Task RefreshHighlightsAsync()
    {
        _highlightCancellation?.Cancel(); _highlightCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource(); _highlightCancellation = cancellation;
        var token = cancellation.Token; var revision = ContentRevision; var rules = _highlightRules.ToArray();
        try
        {
            var text = rules.Any(x => x.Phrase is not null) ? GetText() : string.Empty;
            // Bounded batches prevent a common phrase from creating millions of retained range objects.
            var channel = Channel.CreateBounded<(EditorTextRange Range, DrawingColor Color)[]>(4);
            var producer = Task.Run(async () =>
            {
                try
                {
                    var batch = new List<(EditorTextRange Range, DrawingColor Color)>(512);
                    async Task Emit(EditorTextRange range, DrawingColor color)
                    {
                        batch.Add((range, color));
                        if (batch.Count < 512) return;
                        await channel.Writer.WriteAsync(batch.ToArray(), token); batch.Clear();
                    }
                    foreach (var rule in rules)
                    {
                        token.ThrowIfCancellationRequested();
                        if (rule.Phrase is not { } phrase) { await Emit(rule.Range, rule.Color); continue; }
                        var position = 0; var pendingStart = -1; var pendingEnd = -1;
                        while (position <= text.Length - phrase.Length)
                        {
                            token.ThrowIfCancellationRequested();
                            position = text.IndexOf(phrase, position, StringComparison.Ordinal);
                            if (position < 0) break;
                            if (pendingStart >= 0 && position > pendingEnd)
                            { await Emit(new(pendingStart, pendingEnd - pendingStart), rule.Color); pendingStart = -1; }
                            if (pendingStart < 0) pendingStart = position;
                            pendingEnd = position + phrase.Length;
                            position++;
                        }
                        if (pendingStart >= 0) await Emit(new(pendingStart, pendingEnd - pendingStart), rule.Color);
                    }
                    if (batch.Count > 0) await channel.Writer.WriteAsync(batch.ToArray(), token);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex) { channel.Writer.TryComplete(ex); }
            }, token);
            _editor.IndicatorCurrent = UserIndicator; _editor.IndicatorClearRange(0, _editor.TextLength);
            await foreach (var batch in channel.Reader.ReadAllAsync(token))
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                token.ThrowIfCancellationRequested();
                if (_resourcesReleased || revision != ContentRevision) { cancellation.Cancel(); return; }
                foreach (var (range, color) in batch)
                {
                    _editor.IndicatorCurrent = UserIndicator;
                    _editor.IndicatorValue = 0x1000000 | color.R | color.G << 8 | color.B << 16;
                    _editor.IndicatorFillRange(range.Start, range.Length);
                }
            }
            await producer;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            cancellation.Cancel();
            if (!_resourcesReleased) HighlightFailed?.Invoke($"하이라이트를 표시하지 못했습니다: {ex.Message}");
        }
        finally { if (ReferenceEquals(_highlightCancellation, cancellation)) _highlightCancellation = null; }
    }
}
