using System.Windows;
using MyTextEditor.Models;
using MyTextEditor.Services;

namespace MyTextEditor;

public partial class MainWindow
{
    private bool _checkingShutdown;
    internal bool CanRetainExternalFiles => !_windowClosed && !_checkingShutdown && !_closingInProgress && !_allowClose;
    private readonly Dictionary<DocumentViewModel, (long Started, int Process, int Index)> _externalFileOrder = [];

    internal Task OpenStartupRequestAsync(FileOpenRequest request) => OpenFilesOrderedAsync(request.Paths, request);

    internal bool TryAcceptExternalRequest(FileOpenRequest request) => TryAcceptExternalFilesCore(request.Paths, request);

    internal bool TryAcceptExternalFiles(string[] paths)
        => TryAcceptExternalFilesCore(paths, null);

    private bool TryAcceptExternalFilesCore(string[] paths, FileOpenRequest? request)
    {
        Dispatcher.VerifyAccess();
        if (!CanRetainExternalFiles || _documentTransferRunning || _closingAllDocuments ||
            _closingDocuments.Count > 0 || _savingDocuments.Count > 0 || !IsEnabled) return false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        OpenExternalFiles(paths, request);
        return true;
    }

    private async void OpenExternalFiles(string[] paths, FileOpenRequest? request)
    {
        // Unlike startup, receiving a file must never remove the user's existing blank document.
        try { await OpenFilesOrderedAsync(paths, request); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportFileOpenDeliveryError(exception);
        }
    }

    private void OrderExternalTab(DocumentViewModel document, FileOpenRequest request, int index)
    {
        if (_externalFileOrder.ContainsKey(document)) return;
        var order = (request.StartedUtcTicks, request.ProcessId, index);
        _externalFileOrder[document] = order;
        var pane = RightDocuments.Contains(document) ? RightDocuments : LeftDocuments;
        var earlierSlot = pane.FirstOrDefault(other => other != document &&
            _externalFileOrder.TryGetValue(other, out var otherOrder) && otherOrder.CompareTo(order) > 0);
        if (earlierSlot is not null)
        {
            // A later process can connect first. Insert the late-arriving earlier launch before it.
            var destination = pane.IndexOf(earlierSlot);
            var source = pane.IndexOf(document);
            pane.Move(source, source < destination ? destination - 1 : destination);
        }
    }

    internal void ReportFileOpenDeliveryError(Exception exception)
    {
        StatusMessage.Text = $"외부 파일 열기: {exception.Message}";
    }
}
