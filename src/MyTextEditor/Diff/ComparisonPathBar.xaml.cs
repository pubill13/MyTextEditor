using System.Windows;
using System.Windows.Input;

namespace MyTextEditor.Diff;

public partial class ComparisonPathBar : System.Windows.Controls.UserControl
{
    public ComparisonPathBar() => InitializeComponent();
    public System.Windows.Controls.TextBox LeftInput => LeftPathInput;
    public System.Windows.Controls.TextBox RightInput => RightPathInput;
    public event Action<bool>? SubmitRequested;
    public event Action<bool>? BrowseRequested;
    public event Action<bool, IReadOnlyList<string>>? PathsDropped;
    public static string NormalizePath(string path) => path.Trim().Trim('"');

    private void Path_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Path_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            PathsDropped?.Invoke(ReferenceEquals(sender, LeftPathInput), paths);
        e.Handled = true;
    }

    public void SetKind(string kind)
    {
        LeftLabel.Text = $"왼쪽 {kind}";
        RightLabel.Text = $"오른쪽 {kind}";
    }

    public void RefreshPaths(string? left, string? right)
    {
        if (!LeftPathInput.IsKeyboardFocusWithin) LeftPathInput.Text = left ?? "";
        if (!RightPathInput.IsKeyboardFocusWithin) RightPathInput.Text = right ?? "";
    }

    private void Left_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Submit(true, e);
    private void Right_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Submit(false, e);
    private void Submit(bool left, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SubmitRequested?.Invoke(left);
    }
    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(true);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(false);
}
