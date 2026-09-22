using System.Windows;
using System.Windows.Controls;
using MyTextEditor.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace MyTextEditor;

public sealed class LogCleanupOptionsDialog : Window
{
    public LogCleanupPreferences Preferences { get; private set; }

    public LogCleanupOptionsDialog(LogCleanupPreferences preferences)
    {
        Preferences = preferences;
        Title = "로그 정리 옵션";
        Width = 450;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "필요한 정리만 선택하세요",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var controls = AddOption(panel, "ANSI 코드·제어문자 제거", preferences.RemoveAnsiAndControlCharacters,
            "색상·터미널 제어 코드와 NUL 등을 제거합니다. 탭과 줄바꿈은 유지합니다.");
        var trailing = AddOption(panel, "줄 끝 공백·탭 제거", preferences.TrimTrailingWhitespace,
            "줄 앞 들여쓰기와 본문 중간 공백은 유지합니다.");
        var blanks = AddOption(panel, "연속 빈 줄을 한 줄로 정리", preferences.CollapseBlankLines,
            "공백만 있는 줄도 빈 줄로 취급합니다.");
        panel.Children.Add(new TextBlock
        {
            Text = "로그 형식마다 필요한 공백이 다를 수 있습니다. 먼저 미리보기로 결과를 확인하세요. 모두 끄면 원문을 변경하지 않습니다.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 16)
        });
        var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "옵션 저장", IsDefault = true, MinWidth = 92 };
        save.Click += (_, _) =>
        {
            Preferences = new LogCleanupPreferences
            {
                RemoveAnsiAndControlCharacters = controls.IsChecked == true,
                TrimTrailingWhitespace = trailing.IsChecked == true,
                CollapseBlankLines = blanks.IsChecked == true
            };
            DialogResult = true;
        };
        commands.Children.Add(cancel);
        commands.Children.Add(save);
        panel.Children.Add(commands);
        Content = panel;
    }

    private static CheckBox AddOption(Panel panel, string title, bool selected, string hint)
    {
        var option = new CheckBox
        {
            Content = title,
            IsChecked = selected,
            Margin = new Thickness(0, 6, 0, 4),
            ToolTip = hint
        };
        panel.Children.Add(option);
        var description = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 6) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(description);
        return option;
    }
}
