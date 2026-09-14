using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyTextEditor.Controls;

namespace MyTextEditor;

public partial class MainWindow
{
    private bool _syncingFontSize;

    private void SyncFontSizeInputs()
    {
        _syncingFontSize = true;
        try
        {
            foreach (var combo in new[] { FontSizeCombo, OverflowFontSizeCombo })
            {
                var value = _settings.EditorFontSize.ToString("0");
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == value);
                combo.Text = value;
            }
        }
        finally { _syncingFontSize = false; }
    }

    private void FontSize_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsLoaded && !_syncingFontSize && sender is System.Windows.Controls.ComboBox combo) CommitFontSize(combo);
    }

    private void FontSize_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.ComboBox combo) return;
        CommitFontSize(combo);
        e.Handled = true;
    }

    private void CommitFontSize(System.Windows.Controls.ComboBox combo)
    {
        if (int.TryParse(combo.Text, out int size) && size is >= 6 and <= 72)
            ApplyEditorFontSize(size, OverflowFontSizeCombo);
        else
        {
            SyncFontSizeInputs();
            StatusMessage.Text = "글자 크기는 6~72pt 정수로 입력하세요. 이전 크기를 유지합니다.";
        }
    }

    private void SaveHighlightColor(string color)
    {
        _settings.HighlightColor = color;
        foreach (var document in Documents) document.Editor.HighlightColor = color;
        _diffWorkspaceWindow?.SetHighlightColor(color);
        MarkSettingsDirty();
    }

    private void GoToLine_Click(object sender, RoutedEventArgs e) => ExecuteShortcut(EditorShortcut.GoToLine);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ExecuteShortcut(EditorShortcut.ZoomIn);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ExecuteShortcut(EditorShortcut.ZoomOut);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ExecuteShortcut(EditorShortcut.ZoomReset);
}
