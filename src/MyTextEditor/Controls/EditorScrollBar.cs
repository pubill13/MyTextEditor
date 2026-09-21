using MyTextEditor.Models;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MyTextEditor.Controls;

// A native sibling avoids the WPF/WindowsFormsHost airspace boundary.
internal sealed class EditorScrollBar : Forms.Control
{
    private readonly bool _horizontal;
    private int _maximum, _viewport = 1, _position, _dragOffset;
    private bool _dragging, _hover;
    private Drawing.Color _track, _thumb, _hoverColor;
    public event Action<int>? PositionChanged;

    public EditorScrollBar(bool horizontal)
    {
        _horizontal = horizontal;
        SetStyle(Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.UserPaint | Forms.ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = horizontal ? "가로 스크롤" : "세로 스크롤";
        ApplyPalette(ThemePalette.Get("Light"));
    }
    public void ApplyPalette(ThemePalette palette)
    { _track = palette.ScrollTrack; _thumb = palette.ScrollThumb; _hoverColor = palette.ScrollHover; Invalidate(); }
    public void SetRange(int maximum, int viewport, int position)
    {
        maximum = Math.Max(0, maximum); viewport = Math.Max(1, viewport); position = Math.Clamp(position, 0, maximum);
        if (_maximum == maximum && _viewport == viewport && _position == position) return;
        _maximum = maximum; _viewport = viewport; _position = position; Invalidate();
    }
    private int TrackLength => _horizontal ? ClientSize.Width : ClientSize.Height;
    private int ThumbLength => Math.Min(TrackLength, Math.Max(24, (int)((long)TrackLength * _viewport / ((long)_maximum + _viewport))));
    private int ThumbStart => _maximum == 0 ? 0 : (int)((long)(TrackLength - ThumbLength) * _position / _maximum);
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.Clear(_track);
        if (_maximum == 0) return;
        using var brush = new Drawing.SolidBrush(_hover || _dragging || Focused ? _hoverColor : _thumb);
        var bounds = _horizontal ? new Drawing.Rectangle(ThumbStart, 3, ThumbLength, Math.Max(1, Height - 6)) : new Drawing.Rectangle(3, ThumbStart, Math.Max(1, Width - 6), ThumbLength);
        e.Graphics.FillRectangle(brush, bounds);
    }
    private void MoveTo(int position)
    { var next = Math.Clamp(position, 0, _maximum); if (next == _position) return; _position = next; Invalidate(); PositionChanged?.Invoke(next); }
    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != Forms.MouseButtons.Left || _maximum == 0) return;
        Focus(); var p = _horizontal ? e.X : e.Y;
        if (p >= ThumbStart && p < ThumbStart + ThumbLength) { _dragging = true; _dragOffset = p - ThumbStart; Capture = true; }
        else MoveTo(_position + (p < ThumbStart ? -_viewport : _viewport));
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var space = TrackLength - ThumbLength;
        if (space > 0) MoveTo((int)Math.Clamp((long)((_horizontal ? e.X : e.Y) - _dragOffset) * _maximum / space, 0, _maximum));
    }
    protected override void OnMouseUp(Forms.MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _dragging = false; }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseWheel(Forms.MouseEventArgs e) { base.OnMouseWheel(e); MoveTo(_position - e.Delta / 120 * (_horizontal ? 40 : 3)); }
    protected override bool IsInputKey(Forms.Keys keyData) => keyData is Forms.Keys.Up or Forms.Keys.Down or Forms.Keys.Left or Forms.Keys.Right or Forms.Keys.Home or Forms.Keys.End or Forms.Keys.PageUp or Forms.Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(Forms.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var next = e.KeyCode switch { Forms.Keys.Home => 0, Forms.Keys.End => _maximum, Forms.Keys.PageUp => _position - _viewport, Forms.Keys.PageDown => _position + _viewport,
            Forms.Keys.Up or Forms.Keys.Left => _position - 1, Forms.Keys.Down or Forms.Keys.Right => _position + 1, _ => _position };
        MoveTo(next); e.Handled = true;
    }
}

internal sealed class EditorMenuColors(ThemePalette palette) : Forms.ProfessionalColorTable
{
    public override Drawing.Color ToolStripDropDownBackground => palette.MarginBackground;
    public override Drawing.Color ImageMarginGradientBegin => palette.MarginBackground;
    public override Drawing.Color ImageMarginGradientMiddle => palette.MarginBackground;
    public override Drawing.Color ImageMarginGradientEnd => palette.MarginBackground;
    public override Drawing.Color MenuItemSelected => palette.SelectionBackground;
    public override Drawing.Color MenuItemBorder => palette.Accent;
    public override Drawing.Color MenuBorder => palette.ScrollThumb;
    public override Drawing.Color MenuItemPressedGradientBegin => palette.SelectionBackground;
    public override Drawing.Color MenuItemPressedGradientMiddle => palette.SelectionBackground;
    public override Drawing.Color MenuItemPressedGradientEnd => palette.SelectionBackground;
}

// WinForms' default renderer uses system (often black) text even with a dark color table.
internal sealed class EditorMenuRenderer(ThemePalette palette) : Forms.ToolStripProfessionalRenderer(new EditorMenuColors(palette))
{
    private Drawing.Color Foreground(Forms.ToolStripItem? item) => item is null || !item.Enabled ? palette.MarginForeground :
        item.Selected || item.Pressed ? palette.SelectionForeground : palette.EditorForeground;

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, Foreground(e.Item), e.TextFormat);
        if (e.Item.Tag is Drawing.Color color)
        {
            using var brush = new Drawing.SolidBrush(color);
            e.Graphics.FillRectangle(brush, 6, Math.Max(2, (e.Item.Height - 10) / 2), 10, 10);
        }
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Foreground(e.Item);
        base.OnRenderArrow(e);
    }

}
