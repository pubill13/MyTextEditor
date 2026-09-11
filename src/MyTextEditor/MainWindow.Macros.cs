using System.Windows;
using MyTextEditor.Macros;

namespace MyTextEditor;

public partial class MainWindow
{
    private MacroWindow? _macroWindow;

    private void OpenMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_macroWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new MacroWindow(new MacroWindowCallbacks
        {
            GetCurrentDocument = () => CurrentDocument is { } document
                ? new MacroDocumentSnapshot(document.Id, document.ContentRevision, document.Text, document.NewLine, document.DisplayName)
                : null,
            ApplyResult = (id, revision, text) =>
            {
                var document = FindDocument(id);
                if (_closingInProgress || document is null || document.ContentRevision != revision) return false;
                ClearTransformPreview();
                document.Editor.ReplaceAll(text);
                DocumentTabs.SelectedItem = document;
                StatusMessage.Text = "매크로 결과를 적용했습니다. Ctrl+Z 한 번으로 되돌릴 수 있습니다.";
                return true;
            },
            ReportStatus = message => StatusMessage.Text = message,
            ShowHelp = ShowHelpWindow
        }) { Owner = this };
        window.Closed += (_, _) => { if (ReferenceEquals(_macroWindow, window)) _macroWindow = null; };
        _macroWindow = window;
        window.Show();
    }
}
