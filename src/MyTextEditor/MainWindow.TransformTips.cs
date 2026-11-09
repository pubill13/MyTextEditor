using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using Button = System.Windows.Controls.Button;
using ToolTip = System.Windows.Controls.ToolTip;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MyTextEditor;

public partial class MainWindow
{
    private void QuickTransformRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var row = (Grid)sender;
        var narrow = e.NewSize.Width < 280;
        Grid.SetColumnSpan(row.Children[0], narrow ? 2 : 1);
        var actions = row.Children[1];
        Grid.SetRow(actions, narrow ? 1 : 0);
        Grid.SetColumn(actions, narrow ? 0 : 1);
        Grid.SetColumnSpan(actions, narrow ? 2 : 1);
    }
    private Popup? _transformTip;

    private static TextBlock TipText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
        LineHeight = 21, Margin = new Thickness(10)
    };

    private void SetTransformTip(Button button, string text)
    {
        if (button.ToolTip is ToolTip previous) previous.IsOpen = false;
        button.ToolTip = new ToolTip { Content = TipText(text), Resources = Resources };
        ToolTipService.SetInitialShowDelay(button, 350);
        ToolTipService.SetShowDuration(button, 30000);
    }

    private void TransformTitleHelp_Loaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var example = button.Content?.ToString() switch
        {
            "중복 줄 제거" => "A ↵ a ↵ B → A ↵ B",
            "빈 줄 제거" => "A ↵ 빈 줄 ↵ B → A ↵ B",
            "연속 빈 줄 줄이기" => "빈 줄 3개 → 빈 줄 1개",
            "줄 앞뒤 공백 제거" => "  내용  → 내용",
            _ => null
        };
        SetTransformTip(button, button.Tag + (example is null ? "" : "\n예: " + example));
    }

    private void CloseTransformTip()
    {
        if (_transformTip is not null) _transformTip.IsOpen = false;
        _transformTip = null;
    }

    private void TransformTip_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (_transformTip?.PlacementTarget == button) { CloseTransformTip(); return; }
        CloseTransformTip();
        if (button.ToolTip is not ToolTip tip || tip.Content is not TextBlock text) return;
        tip.IsOpen = false;
        var border = new Border { Child = TipText(text.Text), Resources = Resources,
            BorderThickness = new Thickness(1), Focusable = true };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        border.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        var popup = new Popup { Child = border, PlacementTarget = button, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true };
        border.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            popup.IsOpen = false; button.Focus(); args.Handled = true;
        };
        popup.Opened += (_, _) => border.Focus();
        popup.Closed += (_, _) => { if (ReferenceEquals(_transformTip, popup)) _transformTip = null; };
        _transformTip = popup;
        popup.IsOpen = true;
    }
}
