using System.Windows;
using System.Windows.Input;

namespace MyTextEditor.Diff;

public partial class ComparisonPathBar : System.Windows.Controls.UserControl
{
    private bool _folders;
    private bool _composing;
    private Func<bool, IReadOnlyList<string>>? _getHistory;
    private Action<bool, IReadOnlyList<string>>? _remember;

    public ComparisonPathBar()
    {
        InitializeComponent();
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) =>
        {
            // Keep the composition guard until the key that commits the IME has finished routing.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _composing = false));
        });
        LostKeyboardFocus += (_, _) => _composing = false;
    }

    public void ConfigureHistory(Func<bool, IReadOnlyList<string>> getHistory, Action<bool, IReadOnlyList<string>> remember)
    {
        _getHistory = getHistory;
        _remember = remember;
    }

    public void RememberPaths(params string?[] paths)
    {
        var valid = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        if (valid.Length > 0) _remember?.Invoke(_folders, valid);
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Button)sender;
        var input = ReferenceEquals(button, LeftHistoryButton) ? LeftInput : RightInput;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button };
        foreach (var path in _getHistory?.Invoke(_folders) ?? [])
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = new System.Windows.Controls.TextBlock { Text = path, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13 },
                ToolTip = path
            };
            item.Click += (_, _) =>
            {
                input.Text = path;
                input.Focus();
                input.CaretIndex = input.Text.Length;
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "최근 비교 경로가 없습니다", IsEnabled = false });
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
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
        _folders = kind == "폴더";
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
        if (e.Handled || e.Key != Key.Enter || _composing) return;
        // Consume Enter once so it cannot activate another comparison command.
        e.Handled = true;
        if (e.IsRepeat) return;
        SubmitRequested?.Invoke(left);
    }
    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(true);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke(false);
}
