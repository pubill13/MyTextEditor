using System.Diagnostics;
using System.IO;
using System.Text;
using MyTextEditor.Controls;
using MyTextEditor.Core;
using MyTextEditor.Models;
using MyTextEditor.Services;

namespace MyTextEditor;

internal sealed class RecoveryCaptureState
{
    internal CancellationTokenSource? Cancellation;
    internal (long Revision, DocumentEncodingKind Encoding, string? Path, string NewLine)? Saved;
}

public partial class MainWindow
{
    private static App? RecoveryApplication => System.Windows.Application.Current as App;

    private Task<bool> EnsureRecoveryCheckpointAsync(DocumentViewModel document) => CaptureRecoveryAsync(document, required: true);

    internal async Task CaptureRecoveryDocumentsAsync()
    {
        if (_windowClosed || _closingInProgress || _closingAllDocuments || _documentTransferRunning) return;
        foreach (var document in Documents.ToArray())
        {
            if (_windowClosed || _closingInProgress || _closingAllDocuments || _documentTransferRunning) return;
            if (document.IsModified && !_savingDocuments.Contains(document) && !_closingDocuments.Contains(document) &&
                !_encodingReloads.Contains(document)) await CaptureRecoveryAsync(document, required: false);
        }
    }

    private async Task<bool> CaptureRecoveryAsync(DocumentViewModel document, bool required)
    {
        var app = RecoveryApplication;
        // Plain Application instances used by the verification harness do not own a recovery session.
        if (app is null) return true;
        if (app.RecoveryStore is null)
        {
            if (required) StatusMessage.Text = "자동 복구 저장소를 사용할 수 없어 문서 이동을 중단했습니다. 먼저 문서를 저장하세요.";
            return !document.IsModified;
        }
        var acquired = false;
        RecoveryCaptureState? state = null;
        CancellationTokenSource? capture = null;
        try
        {
            await app.RecoveryGate.WaitAsync(app.RecoveryCancellation.Token);
            acquired = true;
            if (_windowClosed || (!required && (_closingInProgress || _closingAllDocuments || _closingDocuments.Contains(document) || _savingDocuments.Contains(document))) || !Documents.Contains(document)) return false;
            if (!document.IsModified) return true;
            if (!app.RecoveryStates.TryGetValue(document.Id, out state))
                app.RecoveryStates.Add(document.Id, state = new RecoveryCaptureState());
            var signature = (document.ContentRevision, DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark), document.FilePath, document.NewLine);
            if (state.Saved == signature) return true;
            capture = CancellationTokenSource.CreateLinkedTokenSource(app.RecoveryCancellation.Token);
            state.Cancellation = capture;
            var selection = document.Editor.SelectionRange;
            if (required) StatusMessage.Text = "문서를 옮기기 전 최신 자동 복구본을 저장하는 중…";
            var watch = Stopwatch.StartNew();
            var text = await document.Editor.CaptureTextAsync(capture.Token);
            bool IsCurrent() => !_windowClosed && (required || (!_closingInProgress && !_closingAllDocuments && !_closingDocuments.Contains(document) && !_savingDocuments.Contains(document))) && Documents.Contains(document) && document.IsModified &&
                signature == (document.ContentRevision, DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark), document.FilePath, document.NewLine);
            capture.Token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return false;
            await app.RecoveryStore.WriteAsync(new RecoverySnapshot(document.Id, signature.FilePath, signature.Item2,
                signature.NewLine, selection.Start, selection.Length, text, DateTimeOffset.UtcNow), capture.Token);
            capture.Token.ThrowIfCancellationRequested();
            if (!IsCurrent())
            {
                if (required) StatusMessage.Text = "복구본 저장 중 문서가 변경되어 이동을 중단했습니다. 다시 시도하세요.";
                return false;
            }
            state.Saved = signature;
            DiagnosticLog.Measure("recovery-checkpoint", watch.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (required && !_windowClosed) StatusMessage.Text = "문서가 변경되거나 닫혀 복구본 저장과 이동을 중단했습니다.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Error("recovery-checkpoint", exception);
            if (!_windowClosed) StatusMessage.Text = $"자동 복구본을 저장하지 못했습니다. 문서를 유지합니다: {exception.Message}";
            return false;
        }
        finally
        {
            if (state is not null && ReferenceEquals(state.Cancellation, capture)) state.Cancellation = null;
            capture?.Dispose();
            if (acquired) app.RecoveryGate.Release();
        }
    }

    private void CancelRecoveryCapture(DocumentViewModel document)
    {
        if (RecoveryApplication is not { } app || !app.RecoveryStates.TryGetValue(document.Id, out var state) || state is null) return;
        state.Cancellation?.Cancel();
        state.Saved = null;
    }

    private async Task<bool> DiscardRecoveryAsync(DocumentViewModel document)
    {
        var app = RecoveryApplication;
        if (app?.RecoveryStore is null) return true;
        var revision = document.ContentRevision;
        var encoding = DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark);
        var readOnly = document.Editor.IsReadOnly;
        document.Editor.IsReadOnly = true;
        CancelRecoveryCapture(document);
        await app.RecoveryGate.WaitAsync();
        var changed = false;
        try
        {
            if (revision != document.ContentRevision || encoding != DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark))
                return false;
            await app.RecoveryStore.DiscardAsync(document.Id);
            app.RecoveryStates.Remove(document.Id);
            changed = revision != document.ContentRevision || encoding != DocumentEncodings.FromEncoding(document.Encoding, document.HasByteOrderMark);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("recovery-discard", exception);
            if (!_windowClosed) StatusMessage.Text = $"자동 복구본을 정리하지 못했습니다: {exception.Message}";
            return false;
        }
        finally
        {
            app.RecoveryGate.Release();
            if (!_windowClosed && Documents.Contains(document)) document.Editor.IsReadOnly = readOnly;
        }
        if (changed)
        {
            await EnsureRecoveryCheckpointAsync(document);
            StatusMessage.Text = "복구본 정리 중 문서가 변경되었습니다. 변경 내용을 확인한 뒤 다시 닫으세요.";
            return false;
        }
        return true;
    }

    internal void ReportRecoveryError(string message)
    {
        if (!_windowClosed) StatusMessage.Text = message;
    }

    internal async Task<bool> RestoreRecoveryAsync(RecoverySnapshot snapshot)
    {
        // Encode away from the dispatcher; the native editor requires a single UTF-8 load.
        var utf8 = await Task.Run(() => Encoding.UTF8.GetBytes(snapshot.Text));
        if (_windowClosed || _closingInProgress) return false;
        var editor = new ScintillaEditorHost();
        var document = new DocumentViewModel
        {
            Editor = editor, FilePath = snapshot.FilePath,
            Encoding = DocumentEncodings.GetEncoding(snapshot.Encoding), HasByteOrderMark = DocumentEncodings.HasBom(snapshot.Encoding),
            NewLine = snapshot.NewLine, RecoveryModified = true
        };
        try
        {
            document.AcceptEncodingChanges();
            editor.SetNewLine(document.NewLine);
            editor.LoadUtf8(utf8);
            ConfigureEditor(document);
            Documents.Add(document);
            SelectDocument(document);
            editor.SetSelection(new EditorTextRange(snapshot.SelectionStart, snapshot.SelectionLength));
            EmptyDocumentState.Visibility = System.Windows.Visibility.Collapsed;
            if (!await EnsureRecoveryCheckpointAsync(document)) return false;
            StatusMessage.Text = "자동 복구 문서를 미저장 탭으로 열었습니다. 내용을 확인한 뒤 저장하세요. Undo 기록은 복원되지 않습니다.";
            return true;
        }
        catch
        {
            if (!Documents.Contains(document)) editor.ReleaseResources();
            throw;
        }
    }
}
