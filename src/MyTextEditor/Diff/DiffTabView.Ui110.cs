using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyTextEditor.Core.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace MyTextEditor.Diff;

public partial class DiffTabView
{
    private void CopyBlockMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _currentBlockIndex < 0 || _currentBlockIndex >= _result.Blocks.Count) return;
        var block = _result.Blocks[_currentBlockIndex];
        var side = (sender as System.Windows.Controls.MenuItem)?.Tag as string;
        var text = side == "Left" ? BuildBlockText(block, true) : side == "Right" ? BuildBlockText(block, false)
            : $"--- LEFT: {_left.DisplayName} ---\n{BuildBlockText(block, true)}\n--- RIGHT: {_right.DisplayName} ---\n{BuildBlockText(block, false)}";
        System.Windows.Clipboard.SetText(text);
        e.Handled = true;
    }

    private void RefreshDifferenceMap()
    {
        if (DifferenceMapView is null || _rightEditor is null) return;
        DifferenceMapView.SetResult(_result, _rightEditor.LineCount);
        DifferenceMapView.Navigate = index =>
        {
            if (!_isActive || _result is null || index >= _result.Blocks.Count) return;
            _currentBlockIndex = index - 1;
            NavigateDifference(1);
        };
        RefreshDifferenceMapViewport();
    }

    private void RefreshDifferenceMapViewport()
    {
        if (DifferenceMapView is null || _rightEditor is null || !_isActive) return;
        DifferenceMapView.SetViewport(_rightEditor.FirstVisibleLine, _rightEditor.LinesOnScreen);
    }
}

// A cached pixel-row map keeps scrolling independent of the number of differences.
public sealed class DifferenceMap : FrameworkElement
{
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(nameof(Surface), typeof(Brush), typeof(DifferenceMap), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(DifferenceMap), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Surface { get => (Brush)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    private TextDiffResult? _result;
    private int _lineCount = 1, _first, _visible = 1;
    private int[] _pixels = [];
    private static readonly Brush Added = FrozenBrush(0x40, 0xA0, 0x5B);
    private static readonly Brush Deleted = FrozenBrush(0xD2, 0x3F, 0x44);
    private static readonly Brush Modified = FrozenBrush(0xCD, 0x97, 0x1F);
    public Action<int>? Navigate { get; set; }

    public DifferenceMap()
    {
        SetResourceReference(SurfaceProperty, "SurfaceAltBrush");
        SetResourceReference(AccentProperty, "AccentBrush");
        Cursor = System.Windows.Input.Cursors.Hand;
        SizeChanged += (_, _) => Rebuild();
        MouseLeftButtonDown += (_, e) =>
        {
            if (_result is null || _result.Blocks.Count == 0 || ActualHeight <= 0) return;
            var line = e.GetPosition(this).Y / ActualHeight * _lineCount + 1;
            var blocks = _result.Blocks;
            int low = 0, high = blocks.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (blocks[middle].RightStartLine < line) low = middle + 1; else high = middle;
            }
            var index = Math.Min(low, blocks.Count - 1);
            if (index > 0 && Math.Abs(blocks[index - 1].RightStartLine - line) < Math.Abs(blocks[index].RightStartLine - line)) index--;
            Navigate?.Invoke(index);
            e.Handled = true;
        };
    }

    public void SetResult(TextDiffResult? result, int lineCount)
    {
        if (ReferenceEquals(result, _result) && _lineCount == lineCount) return;
        _result = result;
        _lineCount = Math.Max(1, lineCount);
        Rebuild();
    }

    public void SetViewport(int first, int visible)
    {
        if (_first == first && _visible == visible) return;
        _first = first; _visible = visible;
        InvalidateVisual();
    }

    private void Rebuild()
    {
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        _pixels = new int[height];
        if (_result is not null)
            foreach (var block in _result.Blocks)
            {
                var start = Math.Clamp((int)((long)(block.RightStartLine - 1) * height / _lineCount), 0, height - 1);
                var end = Math.Clamp((int)((long)(block.RightStartLine - 1 + Math.Max(1, block.RightLineCount)) * height / _lineCount), start, height - 1);
                var kind = (int)block.Kind + 1;
                for (int y = start; y <= end; y++) _pixels[y] = Math.Max(_pixels[y], kind);
            }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Surface, null, new Rect(RenderSize));
        for (int y = 0; y < _pixels.Length;)
        {
            var kind = _pixels[y]; int end = y + 1;
            while (end < _pixels.Length && _pixels[end] == kind) end++;
            if (kind != 0) context.DrawRectangle(kind == 1 ? Added : kind == 2 ? Deleted : Modified, null, new Rect(4, y, Math.Max(1, ActualWidth - 8), end - y));
            y = end;
        }
        var top = Math.Clamp((double)_first / _lineCount * ActualHeight, 0, ActualHeight);
        var height = Math.Min(ActualHeight - top, Math.Max(4, (double)_visible / _lineCount * ActualHeight));
        context.DrawRectangle(null, new Pen(Accent, 1), new Rect(1, top, Math.Max(0, ActualWidth - 2), height));
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
    }
}

