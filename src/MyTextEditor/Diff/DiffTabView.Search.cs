using System.Windows;
using System.Windows.Input;
using MyTextEditor.Controls;

namespace MyTextEditor.Diff;

public partial class DiffTabView
{
    private ScintillaEditorHost? _lastFocusedEditor;

    private void SearchEditor_Focused(object? sender, EventArgs e)
    {
        _lastFocusedEditor = sender as ScintillaEditorHost;
        FindSide.SelectedIndex = ReferenceEquals(sender, _rightEditor) ? 1 : 0;
    }

    private void OpenFind_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void OpenReplace_Click(object sender, RoutedEventArgs e)
    { OpenFind(); ReplaceInputs.Visibility = Visibility.Visible; }

    private void OpenFind(ScintillaEditorHost? editor = null)
    {
        if (!_initialized || _resourcesReleased) return;
        editor ??= _lastFocusedEditor ?? _leftEditor;
        FindSide.SelectedIndex = ReferenceEquals(editor, _rightEditor) ? 1 : 0;
        if (editor.HasSelection)
        {
            var selection = editor.SelectedText;
            if (selection.Length <= 1000 && !selection.Contains('\n') && !selection.Contains('\r'))
                FindInput.Text = selection;
        }
        FindBar.Visibility = Visibility.Visible;
        ReplaceInputs.Visibility = Visibility.Collapsed;
        FindInput.Focus();
        FindInput.SelectAll();
    }

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindInEditor(true);
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindInEditor(false);

    private void FindInEditor(bool previous)
    {
        if (!_initialized || _resourcesReleased) return;
        if (string.IsNullOrEmpty(FindInput.Text)) { OpenFind(); return; }
        var editor = FindSide.SelectedIndex == 1 ? _rightEditor : _leftEditor;
        var found = editor.FindOccurrence(FindInput.Text, FindMatchCase.IsChecked == true,
            FindWholeWord.IsChecked == true, previous, out var wrapped);
        FindStatus.Text = !found ? "일치하는 텍스트가 없습니다." : wrapped ? "반대쪽 끝에서 이어 찾았습니다." : "일치 항목을 찾았습니다.";
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (_initialized && !_resourcesReleased)
            (FindSide.SelectedIndex == 1 ? _rightEditor : _leftEditor).FocusEditor();
    }

    private ScintillaEditorHost? EditableSearchTarget()
    {
        if (!_initialized || _resourcesReleased || string.IsNullOrEmpty(FindInput.Text)) return null;
        var editor = FindSide.SelectedIndex == 1 ? _rightEditor : _leftEditor;
        if (!editor.IsReadOnly) return editor;
        FindStatus.Text = "편집 잠금을 해제한 뒤 바꿀 수 있습니다.";
        return null;
    }

    private void ReplaceCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (EditableSearchTarget() is not { } editor) return;
        var range = editor.SelectionRange;
        // Re-find from the selection start so whole-word rules exactly match native finding.
        editor.SelectUtf16Range(range.Start, 0, false);
        if (!editor.FindOccurrence(FindInput.Text, FindMatchCase.IsChecked == true,
            FindWholeWord.IsChecked == true, false, out _))
        { editor.SelectUtf16Range(range.Start, range.Length, false); FindStatus.Text = "일치하는 텍스트가 없습니다."; return; }
        if (editor.SelectionRange != range)
        { FindStatus.Text = "일치 항목을 찾았습니다. 바꾸기를 누르면 변경합니다."; return; }
        if (editor.SelectedText != ReplaceInput.Text) editor.ReplaceRange(range, ReplaceInput.Text);
        editor.SelectUtf16Range(range.Start + ReplaceInput.Text.Length, 0, false);
        FindStatus.Text = "1개 항목을 바꿨습니다. Ctrl+Z로 되돌릴 수 있습니다.";
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (EditableSearchTarget() is not { } editor) return;
        var originalSelection = editor.SelectionRange;
        var original = editor.GetText();
        var output = new System.Text.StringBuilder(original.Length);
        var end = 0; var count = 0;
        editor.SelectUtf16Range(0, 0, false);
        while (editor.FindOccurrence(FindInput.Text, FindMatchCase.IsChecked == true,
            FindWholeWord.IsChecked == true, false, out var wrapped) && !wrapped)
        {
            var match = editor.SelectionRange;
            if (match.Start < end) break;
            output.Append(original, end, match.Start - end).Append(ReplaceInput.Text);
            end = match.End; count++;
        }
        output.Append(original, end, original.Length - end);
        var result = output.ToString();
        if (count > 0 && result != original) editor.ReplaceAll(result);
        else editor.SelectUtf16Range(originalSelection.Start, originalSelection.Length, false);
        FindStatus.Text = count == 0 ? "일치하는 텍스트가 없습니다." : result == original ? "변경할 내용이 없습니다." : $"{count:N0}개 항목을 바꿨습니다. Ctrl+Z로 되돌릴 수 있습니다.";
    }

    private void FindBar_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // IME composition keys must remain with the text input control.
        if (e.Handled || e.Key == Key.ImeProcessed) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (FindSide.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            // Handle the combo before WindowsFormsHost preprocesses the system key.
            FindSide.IsDropDownOpen = !FindSide.IsDropDownOpen;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (FindInput.IsKeyboardFocusWithin)
            {
                FindInEditor(Keyboard.Modifiers == ModifierKeys.Shift);
                FindInput.Focus();
                e.Handled = true;
            }
            else if (ReplaceInput.IsKeyboardFocusWithin)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift) FindInEditor(true);
                else ReplaceCurrent_Click(ReplaceInput, e);
                ReplaceInput.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && !FindSide.IsDropDownOpen)
        { CloseFind_Click(sender, e); e.Handled = true; }
    }
}
