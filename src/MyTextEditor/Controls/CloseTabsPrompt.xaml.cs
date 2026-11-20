using System.Windows;

namespace MyTextEditor.Controls;

public partial class CloseTabsPrompt : Window
{
    public MessageBoxResult Answer { get; private set; } = MessageBoxResult.Cancel;
    public bool DiscardRemaining { get; private set; }

    internal CloseTabsPrompt(string name, int remaining)
    {
        InitializeComponent();
        Question.Text = $"'{name}'의 변경 내용을 저장할까요?";
        if (remaining > 1)
            Explanation.Text = $"‘모두 아니오’를 선택하면 이 문서를 포함해 남은 문서 {remaining}개의 변경 내용을 저장하지 않고 닫습니다.";
        else
            Explanation.Visibility = DiscardAll.Visibility = Visibility.Collapsed;
    }

    private void Answer_Click(object sender, RoutedEventArgs e)
    {
        DiscardRemaining = sender == DiscardAll;
        Answer = sender == Save ? MessageBoxResult.Yes : MessageBoxResult.No;
        DialogResult = true;
    }
}
