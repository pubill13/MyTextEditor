using System.Diagnostics;
using System.Windows;
using MyTextEditor.Models;
using Point = System.Windows.Point;

namespace MyTextEditor;

public partial class MainWindow
{
    private bool _documentTransferRunning;

    private bool CanTransferDocument(DocumentViewModel document) =>
        !_windowClosed && !_checkingShutdown && !_closingInProgress && !_allowClose &&
        !_documentTransferRunning && !_closingAllDocuments && _closingDocuments.Count == 0 &&
        _savingDocuments.Count == 0 && _pendingFileOpens == 0 && !_encodingReloads.Contains(document) &&
        Documents.Contains(document) && IsEnabled;

    internal bool CanReceiveDocument =>
        !_windowClosed && !_closingInProgress && !_checkingShutdown && !_allowClose &&
        !_documentTransferRunning && !_closingAllDocuments && _closingDocuments.Count == 0 &&
        _savingDocuments.Count == 0 && _pendingFileOpens == 0 && IsEnabled && IsVisible;

    private MainWindow? DetachDocumentWindow(DocumentViewModel document, Point screenPosition)
    {
        if (!CanTransferDocument(document)) return null;
        var watch = Stopwatch.StartNew();
        MainWindow? destination = null;
        try
        {
            destination = new MainWindow(createDocument: false);
            if (!_settingsReady)
            {
                destination._settingsReady = false;
                destination._settingsSaveTimer.Stop();
            }
            var monitor = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)screenPosition.X, (int)screenPosition.Y));
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var area = monitor.WorkingArea;
            destination.WindowStartupLocation = WindowStartupLocation.Manual;
            destination.WindowState = WindowState.Normal;
            destination.Left = Math.Clamp(screenPosition.X / dpi.DpiScaleX - 80,
                area.Left / dpi.DpiScaleX, Math.Max(area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - destination.Width));
            destination.Top = Math.Clamp(screenPosition.Y / dpi.DpiScaleY - 14,
                area.Top / dpi.DpiScaleY, Math.Max(area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - destination.Height));
            destination.Show();
            var initializeMs = watch.Elapsed.TotalMilliseconds;
            if (!MoveDocumentToWindow(document, destination, false, 0))
            {
                CloseFailedTransferWindow(destination);
                return null;
            }
            Services.DiagnosticLog.Measure("detach-window", watch.Elapsed.TotalMilliseconds, $"initializeMs={initializeMs:F1}");
            return destination;
        }
        catch (Exception exception)
        {
            Services.DiagnosticLog.Error("detach-window", exception);
            if (destination is not null && !destination.Documents.Contains(document)) CloseFailedTransferWindow(destination);
            StatusMessage.Text = $"새 창을 만들지 못했습니다. 문서를 유지합니다: {exception.Message}";
            return null;
        }
    }

    private static void CloseFailedTransferWindow(MainWindow window)
    {
        window.StopFileSync();
        window._settingsSaveTimer.Stop();
        window._allowClose = true;
        window.Close();
    }

    private bool MoveDocumentToWindow(DocumentViewModel document, MainWindow destination, bool right, int? index)
    {
        if (ReferenceEquals(this, destination) || !CanTransferDocument(document) || !destination.CanReceiveDocument)
            return false;
        var watch = Stopwatch.StartNew();
        var wasRight = RightDocuments.Contains(document);
        var sourceIndex = (wasRight ? RightDocuments : LeftDocuments).IndexOf(document);
        var sourceSelected = CurrentDocument;
        var destinationSelected = destination.CurrentDocument;
        var sourceSplit = RightDocumentTabs.IsVisible;
        var destinationSplit = destination.RightDocumentTabs.IsVisible;
        _fileSyncStates.TryGetValue(document, out var syncState);
        var hasExternalOrder = _externalFileOrder.TryGetValue(document, out var externalOrder);
        _documentTransferRunning = destination._documentTransferRunning = true;
        ++_documentFocusGeneration;
        ++destination._documentFocusGeneration;
        ++_resultFocusGeneration;
        ++destination._resultFocusGeneration;
        try
        {
            ClearTransferredDocumentFocus(document);
            DisconnectEditor(document.Editor);
            Documents.Remove(document);
            DocumentTabs.UpdateLayout();
            RightDocumentTabs.UpdateLayout();
            _fileSyncStates.Remove(document);

            destination._activeDocumentTabs = right ? destination.RightDocumentTabs : destination.DocumentTabs;
            if (right) destination.SetDocumentSplit(true);
            destination.Documents.Add(document);
            destination.ConnectEditor(document);
            if (syncState is not null) destination._fileSyncStates[document] = syncState;
            destination.MoveDocumentOrder(document, right, index ?? (right ? destination.RightDocuments : destination.LeftDocuments).Count - 1);
            destination.SelectDocument(document);
            destination.ApplyEditorAppearance(document.Editor);
            destination.UpdateLayout();
            NormalizeDocumentPanes();
            RefreshDiffSourceStates();
            RefreshSearchSessionState();
            UpdateStatus();
            StatusMessage.Text = "문서 탭을 다른 OmniEdit 창으로 옮겼습니다.";
            destination.Activate();
            _externalFileOrder.Remove(document);
            if (hasExternalOrder) destination._externalFileOrder[document] = externalOrder;
            Services.DiagnosticLog.Measure("move-document-window", watch.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (Exception exception)
        {
            Services.DiagnosticLog.Error("move-document-window", exception);
            destination.ClearTransferredDocumentFocus(document);
            destination.DisconnectEditor(document.Editor);
            destination.Documents.Remove(document);
            destination._fileSyncStates.Remove(document);
            destination._externalFileOrder.Remove(document);
            if (hasExternalOrder) _externalFileOrder[document] = externalOrder;
            destination.UpdateLayout();
            if (!Documents.Contains(document))
            {
                _activeDocumentTabs = wasRight ? RightDocumentTabs : DocumentTabs;
                Documents.Add(document);
            }
            DisconnectEditor(document.Editor);
            ConnectEditor(document);
            SetDocumentSplit(sourceSplit);
            MoveDocumentOrder(document, wasRight, sourceIndex);
            if (syncState is not null) _fileSyncStates[document] = syncState;
            ApplyEditorAppearance(document.Editor);
            SelectDocument(sourceSelected is not null && Documents.Contains(sourceSelected) ? sourceSelected : document);
            destination.SetDocumentSplit(destinationSplit);
            if (destinationSelected is not null && destination.Documents.Contains(destinationSelected))
                destination.SelectDocument(destinationSelected);
            StatusMessage.Text = $"탭을 옮기지 못했습니다. 원래 문서를 유지합니다: {exception.Message}";
            return false;
        }
        finally
        {
            _documentTransferRunning = destination._documentTransferRunning = false;
            // Do not queue a focus callback here. The destination activation and the
            // next user activation must win over a callback captured before reparenting.
        }
    }

    private void ClearTransferredDocumentFocus(DocumentViewModel document)
    {
        var logicalFocus = System.Windows.Input.FocusManager.GetFocusedElement(this);
        if (!ReferenceEquals(logicalFocus, document.Editor) && !document.Editor.IsKeyboardFocusWithin && !document.Editor.IsEditorFocused) return;
        if (document.Editor.IsEditorFocused)
            SetFocus(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        // WPF retains logical focus after a WindowsFormsHost is reparented. Clear it
        // before removal so activating the old window cannot focus the new owner.
        System.Windows.Input.FocusManager.SetFocusedElement(this, null);
        // Clear the keyboard scope even when WPF reports focus on the host wrapper.
        // The native child can retain the HWND focus after it is reparented.
        System.Windows.Input.Keyboard.ClearFocus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr handle);
}
