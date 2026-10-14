using System.Windows;

namespace MyTextEditor;

public partial class MainWindow
{
    private bool _checkingShutdown;

    internal bool TryAcceptExternalFiles(string[] paths)
    {
        Dispatcher.VerifyAccess();
        if (_checkingShutdown || _closingInProgress || _allowClose || _closingAllDocuments ||
            _closingDocuments.Count > 0 || _savingDocuments.Count > 0 || !IsEnabled) return false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        OpenExternalFiles(paths);
        return true;
    }

    private async void OpenExternalFiles(string[] paths)
    {
        // Unlike startup, receiving a file must never remove the user's existing blank document.
        try { await OpenFilesAsync(paths); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportFileOpenDeliveryError(exception);
        }
    }

    internal void ReportFileOpenDeliveryError(Exception exception)
    {
        StatusMessage.Text = $"외부 파일 열기: {exception.Message}";
    }
}
