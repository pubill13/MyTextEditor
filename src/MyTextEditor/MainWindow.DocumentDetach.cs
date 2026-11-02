using System.Windows;
using MyTextEditor.Models;
using Point = System.Windows.Point;

namespace MyTextEditor;

public partial class MainWindow
{
    private MainWindow? DetachDocumentWindow(DocumentViewModel document, Point screenPosition)
    {
        if (!Documents.Contains(document) || _checkingShutdown || _closingInProgress ||
            _closingAllDocuments || _closingDocuments.Count > 0 || _savingDocuments.Count > 0 ||
            _pendingFileOpens > 0 || _encodingReloads.Contains(document))
        {
            StatusMessage.Text = "파일 열기·저장·닫기가 끝난 뒤 탭을 분리하세요.";
            return null;
        }

        MainWindow? destination = null;
        var wasRight = RightDocuments.Contains(document);
        var sourceIndex = (wasRight ? RightDocuments : LeftDocuments).IndexOf(document);
        var wrap = document.Editor.WordWrap;
        _fileSyncStates.TryGetValue(document, out var syncState);
        try
        {
            destination = new MainWindow();
            if (!_settingsReady)
            {
                destination._settingsReady = false;
                destination._settingsSaveTimer.Stop();
            }
            // Screen coordinates are physical pixels; WPF placement uses DIPs.
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
            foreach (var placeholder in destination.Documents.ToArray()) destination.RemoveDocument(placeholder);

            DisconnectEditor(document.Editor);
            Documents.Remove(document);
            DocumentTabs.UpdateLayout();
            RightDocumentTabs.UpdateLayout();
            _fileSyncStates.Remove(document);

            destination.ConfigureEditor(document);
            document.Editor.SetWordWrap(wrap);
            destination.Documents.Add(document);
            if (syncState is not null) destination._fileSyncStates[document] = syncState;
            destination.SelectDocument(document);
            destination.UpdateLayout();

            NormalizeDocumentPanes();
            RefreshDiffSourceStates();
            RefreshSearchSessionState();
            UpdateStatus();
            destination.Activate();
            destination.FocusCurrentDocumentAfterLayout();
            StatusMessage.Text = "문서 탭을 새 OmniEdit 창으로 옮겼습니다.";
            return destination;
        }
        catch (Exception exception)
        {
            // A failed reparent must never release the only editor holding unsaved text.
            if (destination is not null)
            {
                destination.DisconnectEditor(document.Editor);
                destination.Documents.Remove(document);
                destination.UpdateLayout();
                destination._fileSyncStates.Remove(document);
            }
            if (!Documents.Contains(document))
            {
                ConfigureEditor(document);
                document.Editor.SetWordWrap(wrap);
                _activeDocumentTabs = wasRight ? RightDocumentTabs : DocumentTabs;
                Documents.Add(document);
                if (wasRight) SetDocumentSplit(true);
                MoveDocumentOrder(document, wasRight, sourceIndex);
                if (syncState is not null) _fileSyncStates[document] = syncState;
            }
            else
            {
                DisconnectEditor(document.Editor);
                ConfigureEditor(document);
                document.Editor.SetWordWrap(wrap);
            }
            SelectDocument(document);
            if (destination is not null)
            {
                destination.StopFileSync();
                destination._settingsSaveTimer.Stop();
                destination._allowClose = true;
                destination.Close();
            }
            StatusMessage.Text = $"새 창으로 옮기지 못했습니다. 원래 문서를 유지합니다: {exception.Message}";
            return null;
        }
    }
}
