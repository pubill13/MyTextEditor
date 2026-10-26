using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MyTextEditor.Core;
using MyTextEditor.Models;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace MyTextEditor;

public partial class MainWindow
{
    private readonly HashSet<DocumentViewModel> _encodingReloads = [];

    private void WordWrap_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentEditor is not { } editor) return;
        editor.SetWordWrap(WordWrapMenuItem.IsChecked);
        _settings.DefaultWordWrap = editor.WordWrap;
        MarkSettingsDirty();
        StatusMessage.Text = editor.WordWrap ? "자동 줄바꿈 ON · 파일의 개행은 변경하지 않습니다." : "자동 줄바꿈 OFF";
    }

    private void EncodingMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (e.Source != sender) return;
        EncodingFileMenu.Items.Clear();
        foreach (var item in BuildEncodingMenu()) EncodingFileMenu.Items.Add(item);
    }

    private void EncodingStatus_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as FrameworkElement };
        foreach (var item in BuildEncodingMenu()) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private IEnumerable<MenuItem> BuildEncodingMenu()
    {
        var document = CurrentDocument;
        var available = document is not null && !_savingDocuments.Contains(document) && !_encodingReloads.Contains(document);
        var save = new MenuItem { Header = "저장 인코딩 변경", IsEnabled = available };
        var reload = new MenuItem { Header = "이 인코딩으로 다시 열기", IsEnabled = available && document?.FilePath is not null };
        foreach (var kind in Enum.GetValues<DocumentEncodingKind>())
        {
            var choice = kind;
            var item = new MenuItem { Header = DocumentEncodings.GetDisplayName(choice), IsCheckable = true,
                IsChecked = document is not null && DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark) == choice };
            item.Click += (_, _) =>
            {
                if (document is null || !Documents.Contains(document) || _savingDocuments.Contains(document) || _encodingReloads.Contains(document)) return;
                if (DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark) == choice) return;
                document.ChangeEncoding(choice);
                UpdateStatus();
                StatusMessage.Text = "저장 인코딩을 변경했습니다. 파일 저장 시 반영됩니다.";
            };
            save.Items.Add(item);
            var read = new MenuItem { Header = DocumentEncodings.GetDisplayName(choice) };
            read.Click += async (_, _) => { if (document is not null) await ReloadWithEncodingAsync(document, choice); };
            reload.Items.Add(read);
        }
        return [save, reload];
    }

    private async Task ReloadWithEncodingAsync(DocumentViewModel document, DocumentEncodingKind kind)
    {
        if (!Documents.Contains(document) || _savingDocuments.Contains(document) || !_encodingReloads.Add(document)) return;
        try
        {
            if (document.IsModified)
            {
                var answer = MessageBox.Show(this, "다시 열기 전에 변경 내용을 저장할까요?\n저장하지 않으면 현재 편집 내용이 대체됩니다.", "인코딩으로 다시 열기", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !await SaveDocumentCoreAsync(document, false, allowEncodingReload: true))) return;
            }
            if (document.FilePath is not { } path) return;
            var revision = document.ContentRevision;
            var stamp = await ReadFileSyncStampAsync(path);
            var buffer = await Task.Run(() => _fileService.LoadBufferAsync(path, kind));
            var after = await ReadFileSyncStampAsync(path);
            if (!Documents.Contains(document) || document.ContentRevision != revision || document.FilePath != path || stamp != after || _closingInProgress)
            { StatusMessage.Text = "문서 또는 파일이 변경되어 인코딩 다시 열기를 취소했습니다."; return; }
            var line = document.Editor.CurrentLine;
            document.Editor.ReloadUtf8(buffer.Utf8Buffer);
            document.Encoding = buffer.OriginalEncoding;
            document.HasByteOrderMark = buffer.HasByteOrderMark;
            document.AcceptEncodingChanges();
            document.IsModified = false;
            document.NewLine = buffer.NewLine;
            document.Editor.SetNewLine(buffer.NewLine);
            document.Editor.GoToLine(line + 1, focusEditor: false);
            TrackFileSyncDocument(document, after);
            UpdateStatus();
            StatusMessage.Text = $"{document.DisplayName}: {DocumentEncodings.GetDisplayName(kind)}로 다시 열었습니다.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // A wrong encoding is a recoverable choice: keep the current buffer and let the
            // user choose another encoding without stacking a modal dialog on every attempt.
            StatusMessage.Text = $"{document.DisplayName}: 인코딩으로 다시 열지 못했습니다. 현재 내용을 유지합니다. {exception.Message}";
            UpdateStatus();
        }
        finally { _encodingReloads.Remove(document); }
    }
}
