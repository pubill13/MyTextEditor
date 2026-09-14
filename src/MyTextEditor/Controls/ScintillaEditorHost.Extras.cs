using System.Windows;
using System.Windows.Threading;
using ScintillaNET;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;

namespace MyTextEditor.Controls;

public sealed partial class ScintillaEditorHost
{
    private readonly Forms.Panel _surface = new() { Dock = Forms.DockStyle.Fill };
    private readonly EditorScrollBar _verticalBar = new(false) { Dock = Forms.DockStyle.Right, Width = 14 };
    private readonly EditorScrollBar _horizontalBar = new(true) { Dock = Forms.DockStyle.Bottom, Height = 14 };
    private readonly Forms.ContextMenuStrip _contextMenu = new();

    private void InitializeExtras()
    {
        _editor.HScrollBar = false;
        _editor.VScrollBar = false;
        _editor.ScrollWidthTracking = true;
        _editor.ScrollWidth = 1;
        _surface.Controls.Add(_editor);
        _surface.Controls.Add(_verticalBar);
        _surface.Controls.Add(_horizontalBar);
        _verticalBar.PositionChanged += value => SetFirstVisibleLine(value);
        _horizontalBar.PositionChanged += value => { if (_editor.XOffset != value) _editor.XOffset = value; };
        _editor.SizeChanged += Editor_SizeChanged;
        _editor.BeforeInsert += Highlight_BeforeInsert;
        _editor.BeforeDelete += Highlight_BeforeDelete;
        _highlightTimer.Tick += HighlightTimer_Tick;
        InitializeHighlightMenu();
        _editor.UsePopup(PopupMode.Never);
        _editor.ContextMenuStrip = _contextMenu;
        Child = _surface;
    }

    private void Editor_SizeChanged(object? sender, EventArgs e) => UpdateScrollBars();

    private void UpdateScrollBars()
    {
        if (_resourcesReleased || !_editor.IsHandleCreated) return;
        _verticalBar.SetRange(Math.Max(0, LineCount - Math.Max(1, LinesOnScreen)), Math.Max(1, LinesOnScreen), FirstVisibleLine);
        _horizontalBar.SetRange(Math.Max(0, _editor.ScrollWidth - _editor.ClientSize.Width + _editor.Margins[0].Width),
            Math.Max(1, _editor.ClientSize.Width), _editor.XOffset);
    }

    public float GetLineY(int zeroBasedLine)
    {
        var line = Math.Clamp(zeroBasedLine, 0, LineCount - 1);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        return (float)(_editor.PointYFromPosition(_editor.Lines[line].Position) / dpi);
    }

    public void ShowGoToLineDialog()
    {
        var window = new Window { Title = "줄 번호로 이동", Width = 330, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"줄 번호 (1 ~ {LineCount:N0})" });
        var input = new System.Windows.Controls.TextBox { Text = (CurrentLine + 1).ToString(), Margin = new Thickness(0, 8, 0, 8) };
        var error = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap };
        var button = new System.Windows.Controls.Button { Content = "이동", IsDefault = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        button.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text, out var line) || line < 1 || line > LineCount)
            { error.Text = $"1부터 {LineCount:N0}까지 입력하세요."; input.Focus(); input.SelectAll(); return; }
            window.DialogResult = true;
            GoToLine(line);
        };
        panel.Children.Add(input); panel.Children.Add(error); panel.Children.Add(button);
        window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        window.ShowDialog();
    }

    private void ReleaseExtras()
    {
        _highlightTimer.Stop();
        _highlightTimer.Tick -= HighlightTimer_Tick;
        _highlightCancellation?.Cancel();
        _highlightCancellation?.Dispose();
        _highlightCancellation = null;
        _editor.SizeChanged -= Editor_SizeChanged;
        _editor.BeforeInsert -= Highlight_BeforeInsert;
        _editor.BeforeDelete -= Highlight_BeforeDelete;
        _contextMenu.Dispose();
        _highlightRules.Clear();
        HighlightColorChanged = null;
        HighlightFailed = null;
    }
}
