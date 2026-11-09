using System.Windows;
using System.Windows.Controls;
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
        var actions = (FrameworkElement)row.Children[1];
        actions.Margin = narrow ? new Thickness(0, 3, 0, 0) : new Thickness(6, 0, 0, 0);
        Grid.SetRow(actions, narrow ? 1 : 0);
        Grid.SetColumn(actions, narrow ? 0 : 1);
        Grid.SetColumnSpan(actions, narrow ? 2 : 1);
    }

    private static TextBlock TipText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
        LineHeight = 21, Margin = new Thickness(10)
    };

    private void SetTransformTip(FrameworkElement button, string text)
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
            "빈 줄 모두 제거" => "A ↵ 빈 줄 ↵ B → A ↵ B",
            "연속 빈 줄을 한 줄로" => "빈 줄 3개 → 빈 줄 1개",
            "줄 공백 정리" => "들여쓴 줄은 줄 끝만 선택하면 들여쓰기가 유지됩니다.",
            _ => null
        };
        SetTransformTip(button, button.Tag + (example is null ? "" : "\n예: " + example));
    }

}
