using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Models;

namespace MyTextEditor;

public partial class MainWindow
{
    private readonly Dictionary<DocumentViewModel, FileSyncState> _fileSyncStates = [];
    private readonly DispatcherTimer _fileSyncTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _fileSyncLifetime = new();
    private bool _fileSyncPolling;
    private bool _fileSyncStopped;
    private long _fileSyncGeneration;
    private CancellationTokenSource? _fileSyncPollCancellation;

    private readonly record struct FileSyncStamp(bool Exists, long Length, DateTime LastWriteUtc, DateTime CreationUtc);

    private sealed class FileSyncState(string path, FileSyncStamp stamp)
    {
        public string Path { get; } = path;
        public FileSyncStamp Stamp { get; set; } = stamp;
        public bool WasMissing { get; set; }
        public string? LastNotice { get; set; }
    }

    private static Task<FileSyncStamp> ReadFileSyncStampAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists
                ? new FileSyncStamp(true, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc)
                : new FileSyncStamp(false, 0, default, default);
        }, cancellationToken);

    private void InitializeFileSync()
    {
        SyncFilesMenuItem.IsChecked = _settings.AutoReloadFiles;
        _fileSyncTimer.Tick += FileSyncTimer_Tick;
        Closed += (_, _) => StopFileSync();
        if (_settings.AutoReloadFiles) _fileSyncTimer.Start();
    }

    private void StopFileSync()
    {
        if (_fileSyncStopped) return;
        _fileSyncStopped = true;
        _fileSyncGeneration++;
        _fileSyncTimer.Stop();
        _fileSyncTimer.Tick -= FileSyncTimer_Tick;
        _fileSyncPollCancellation?.Cancel();
        _fileSyncPollCancellation = null;
        _fileSyncLifetime.Cancel();
        _fileSyncStates.Clear();
    }

    // Register the stamp captured before loading, so changes made during loading are detected too.
    private void TrackFileSyncDocument(DocumentViewModel document, FileSyncStamp stamp)
    {
        if (document.FilePath is not { } path || _fileSyncStopped) return;
        _fileSyncStates[document] = new FileSyncState(path, stamp);
    }

    private async Task RefreshFileSyncBaselineAsync(DocumentViewModel document)
    {
        if (document.FilePath is not { } path || _fileSyncStopped) return;
        try
        {
            var stamp = await ReadFileSyncStampAsync(path, _fileSyncLifetime.Token);
            if (!_fileSyncStopped && Documents.Contains(document) && document.FilePath == path)
                TrackFileSyncDocument(document, stamp);
        }
        catch (OperationCanceledException) when (_fileSyncStopped) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = $"{document.DisplayName}: 저장 후 외부 변경 감시 상태를 확인하지 못했습니다. {exception.Message}";
        }
    }

    private void SyncFiles_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoReloadFiles = SyncFilesMenuItem.IsChecked;
        _fileSyncGeneration++;
        if (_settings.AutoReloadFiles)
        {
            _fileSyncTimer.Start();
        }
        else
        {
            _fileSyncTimer.Stop();
            _fileSyncPollCancellation?.Cancel();
        }
        MarkSettingsDirty();
        StatusMessage.Text = _settings.AutoReloadFiles
            ? "외부 파일 자동 반영 ON · 저장하지 않은 수정 내용은 덮어쓰지 않습니다."
            : "외부 파일 자동 반영 OFF";
    }

    private async void FileSyncTimer_Tick(object? sender, EventArgs e) => await PollFileChangesAsync();

    private bool CanCheckExternalFile(DocumentViewModel document) =>
        !_fileSyncStopped && _settings.AutoReloadFiles && !_closingInProgress && !_allowClose &&
        Documents.Contains(document) && !_savingDocuments.Contains(document) && !_closingDocuments.Contains(document);

    private async Task PollFileChangesAsync()
    {
        if (_fileSyncPolling || _fileSyncStopped || !_settings.AutoReloadFiles || _closingInProgress) return;
        _fileSyncPolling = true;
        var generation = _fileSyncGeneration;
        using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(_fileSyncLifetime.Token);
        _fileSyncPollCancellation = pollCancellation;
        var cancellationToken = pollCancellation.Token;
        try
        {
            foreach (var removed in _fileSyncStates.Keys.Where(document => !Documents.Contains(document)).ToArray())
                _fileSyncStates.Remove(removed);
            foreach (var (document, state) in _fileSyncStates.ToArray())
            {
                if (generation != _fileSyncGeneration) break;
                if (!CanCheckExternalFile(document) || document.FilePath != state.Path) continue;
                try
                {
                    var observed = await ReadFileSyncStampAsync(state.Path, cancellationToken);
                    if (!IsCurrentFileSync(document, state, generation)) continue;
                    if (!observed.Exists)
                    {
                        state.WasMissing = true;
                        ReportFileSyncNotice(document, state, "파일이 이동되거나 삭제되었습니다. 현재 편집 내용은 유지하며 파일이 다시 생기면 확인합니다.");
                        continue;
                    }
                    if (observed == state.Stamp && !state.WasMissing) continue;
                    if (document.IsModified)
                    {
                        ReportFileSyncNotice(document, state, "외부 파일이 변경되었지만 저장하지 않은 수정 내용이 있어 자동 반영하지 않았습니다.");
                        continue;
                    }

                    var revision = document.ContentRevision;
                    var buffer = await _fileService.LoadBufferAsync(state.Path, cancellationToken);
                    var afterRead = await ReadFileSyncStampAsync(state.Path, cancellationToken);
                    // A file may still be being written, or the user may edit/save/close while I/O runs.
                    if (observed != afterRead || !IsCurrentFileSync(document, state, generation) ||
                        document.IsModified || document.ContentRevision != revision) continue;

                    var line = document.Editor.CurrentLine;
                    var firstVisibleLine = document.Editor.FirstVisibleLine;
                    document.Encoding = buffer.OriginalEncoding;
                    document.HasByteOrderMark = buffer.HasByteOrderMark;
                    document.NewLine = buffer.NewLine;
                    document.Editor.SetNewLine(buffer.NewLine);
                    document.Editor.ReloadUtf8(buffer.Utf8Buffer);
                    document.Editor.GoToLine(line + 1, focusEditor: false);
                    document.Editor.SetFirstVisibleLine(firstVisibleLine);
                    document.IsModified = false;
                    state.Stamp = afterRead;
                    state.WasMissing = false;
                    state.LastNotice = null;
                    UpdateStatus();
                    StatusMessage.Text = $"{document.DisplayName}: 외부 파일 변경을 자동 반영했습니다.";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    if (IsCurrentFileSync(document, state, generation))
                        ReportFileSyncNotice(document, state, $"외부 변경을 읽지 못했습니다. 다음 확인 때 다시 시도합니다. {exception.Message}");
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_fileSyncPollCancellation, pollCancellation)) _fileSyncPollCancellation = null;
            _fileSyncPolling = false;
        }
    }

    private bool IsCurrentFileSync(DocumentViewModel document, FileSyncState state, long generation) =>
        generation == _fileSyncGeneration && CanCheckExternalFile(document) && document.FilePath == state.Path &&
        _fileSyncStates.TryGetValue(document, out var current) && ReferenceEquals(current, state);

    private void ReportFileSyncNotice(DocumentViewModel document, FileSyncState state, string message)
    {
        if (state.LastNotice == message) return;
        state.LastNotice = message;
        StatusMessage.Text = $"{document.DisplayName}: {message}";
    }
}
