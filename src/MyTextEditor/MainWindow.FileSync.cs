using System.IO;
using System.Diagnostics;
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
        public FileSyncStamp? PendingStamp { get; set; }
        public long PendingSince { get; set; }
    }

    private static Task<FileSyncStamp> ReadFileSyncStampAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadFileSyncStamp(path, cancellationToken), cancellationToken);

    private static FileSyncStamp ReadFileSyncStamp(string path, CancellationToken cancellationToken)
    {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists
                ? new FileSyncStamp(true, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc)
                : new FileSyncStamp(false, 0, default, default);
    }

    // One background sweep avoids scheduling one worker and UI continuation per open file.
    private static Task<(DocumentViewModel Document, FileSyncState State, FileSyncStamp Stamp, string? Error)[]> ReadFileSyncBatchAsync(
        KeyValuePair<DocumentViewModel, FileSyncState>[] entries, CancellationToken cancellationToken) => Task.Run(() =>
        entries.Select(entry =>
        {
            try { return (entry.Key, entry.Value, ReadFileSyncStamp(entry.Value.Path, cancellationToken), (string?)null); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { return (entry.Key, entry.Value, default(FileSyncStamp), exception.Message); }
        }).ToArray(), cancellationToken);

    private void InitializeFileSync()
    {
        SyncFilesMenuItem.IsChecked = _settings.AutoReloadFiles;
        _fileSyncTimer.Tick += FileSyncTimer_Tick;
        Closed += (_, _) => StopFileSync();
        if (_settings.AutoReloadFiles) _fileSyncTimer.Start();
        UpdateFileSyncTooltip();
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
        _settings.AutoReloadFiles = SyncFilesMenuItem.IsChecked == true;
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
        UpdateFileSyncTooltip();
        StatusMessage.Text = _settings.AutoReloadFiles
            ? "외부 파일 자동 반영 ON · 저장하지 않은 수정 내용은 덮어쓰지 않습니다."
            : "외부 파일 자동 반영 OFF";
    }

    private async void FileSyncTimer_Tick(object? sender, EventArgs e) => await PollFileChangesAsync();

    private bool CanCheckExternalFile(DocumentViewModel document) =>
        !_fileSyncStopped && _settings.AutoReloadFiles && !_closingInProgress && !_allowClose &&
        Documents.Contains(document) && !_savingDocuments.Contains(document) && !_closingDocuments.Contains(document) && !_encodingReloads.Contains(document);

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
            var entries = _fileSyncStates.Where(entry => CanCheckExternalFile(entry.Key) && entry.Key.FilePath == entry.Value.Path).ToArray();
            var observations = await ReadFileSyncBatchAsync(entries, cancellationToken);
            foreach (var (document, state, observed, error) in observations)
            {
                if (generation != _fileSyncGeneration) break;
                if (!CanCheckExternalFile(document) || document.FilePath != state.Path) continue;
                try
                {
                    if (!IsCurrentFileSync(document, state, generation)) continue;
                    if (error is not null) { ReportFileSyncNotice(document, state, $"외부 변경 확인 실패: {error}"); continue; }
                    if (!observed.Exists)
                    {
                        state.WasMissing = true;
                        ReportFileSyncNotice(document, state, "파일이 이동되거나 삭제되었습니다. 현재 편집 내용은 유지하며 파일이 다시 생기면 확인합니다.");
                        continue;
                    }
                    if (observed == state.Stamp && !state.WasMissing) { state.PendingStamp = null; continue; }
                    if (document.IsModified)
                    {
                        ReportFileSyncNotice(document, state, "외부 파일이 변경되었지만 저장하지 않은 수정 내용이 있어 자동 반영하지 않았습니다.");
                        continue;
                    }

                    if (state.PendingStamp != observed)
                    {
                        state.PendingStamp = observed;
                        state.PendingSince = Stopwatch.GetTimestamp();
                        ReportFileSyncNotice(document, state, "외부 변경 감지 · 파일 기록이 안정될 때까지 기다립니다.");
                        continue;
                    }
                    if (Stopwatch.GetElapsedTime(state.PendingSince).TotalMilliseconds < 500) continue;
                    if (!ReferenceEquals(document, CurrentDocument))
                    {
                        ReportFileSyncNotice(document, state, "외부 변경 대기 · 이 문서를 선택하면 반영합니다.");
                        continue;
                    }

                    var revision = document.ContentRevision;
                    SyncFilesMenuItem.ToolTip = $"외부 변경 반영 ON · {document.DisplayName} 읽는 중";
                    var buffer = await _fileService.LoadBufferAsync(state.Path, cancellationToken);
                    var afterRead = await ReadFileSyncStampAsync(state.Path, cancellationToken);
                    // A file may still be being written, or the user may edit/save/close while I/O runs.
                    if (observed != afterRead || !IsCurrentFileSync(document, state, generation) ||
                        document.IsModified || document.ContentRevision != revision || !ReferenceEquals(document, CurrentDocument)) continue;

                    var applyWatch = Stopwatch.StartNew();
                    var line = document.Editor.CurrentLine;
                    var firstVisibleLine = document.Editor.FirstVisibleLine;
                    document.Encoding = buffer.OriginalEncoding;
                    document.HasByteOrderMark = buffer.HasByteOrderMark;
                    document.AcceptEncodingChanges();
                    document.NewLine = buffer.NewLine;
                    document.Editor.SetNewLine(buffer.NewLine);
                    document.Editor.ReloadUtf8(buffer.Utf8Buffer);
                    document.Editor.GoToLine(line + 1, focusEditor: false);
                    document.Editor.SetFirstVisibleLine(firstVisibleLine);
                    applyWatch.Stop();
                    document.IsModified = false;
                    state.Stamp = afterRead;
                    state.WasMissing = false;
                    state.LastNotice = null;
                    state.PendingStamp = null;
                    UpdateStatus();
                    StatusMessage.Text = $"{document.DisplayName}: 외부 변경 반영 · 읽기 {buffer.ReadElapsed.TotalMilliseconds:F0}ms · 변환 {buffer.DecodeElapsed.TotalMilliseconds:F0}ms · 화면 반영 {applyWatch.Elapsed.TotalMilliseconds:F0}ms";
                    LastFileSyncMeasurement = new FileSyncMeasurement(buffer.Utf8Buffer.Length, buffer.ReadElapsed.TotalMilliseconds, buffer.DecodeElapsed.TotalMilliseconds, applyWatch.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    if (IsCurrentFileSync(document, state, generation))
                        ReportFileSyncNotice(document, state, $"외부 변경을 읽지 못했습니다. 다음 확인 때 다시 시도합니다. {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_fileSyncPollCancellation, pollCancellation)) _fileSyncPollCancellation = null;
            _fileSyncPolling = false;
            UpdateFileSyncTooltip();
        }
    }

    internal readonly record struct FileSyncMeasurement(long Utf8Bytes, double ReadMilliseconds, double DecodeMilliseconds, double ApplyMilliseconds);
    internal FileSyncMeasurement? LastFileSyncMeasurement { get; private set; }

    private void UpdateFileSyncTooltip()
    {
        var notice = CurrentDocument is { } document && _fileSyncStates.TryGetValue(document, out var state) ? state.LastNotice : null;
        SyncFilesMenuItem.ToolTip = !_settings.AutoReloadFiles
            ? "외부 변경 반영 OFF · 켜면 저장된 파일의 외부 변경을 감시합니다."
            : notice is null ? "외부 변경 반영 ON · 기록이 안정된 활성 문서만 다시 읽습니다. 미저장 수정은 덮어쓰지 않습니다."
            : $"외부 변경 반영 ON · {notice}";
    }

    private bool IsCurrentFileSync(DocumentViewModel document, FileSyncState state, long generation) =>
        generation == _fileSyncGeneration && CanCheckExternalFile(document) && document.FilePath == state.Path &&
        _fileSyncStates.TryGetValue(document, out var current) && ReferenceEquals(current, state);

    private void ReportFileSyncNotice(DocumentViewModel document, FileSyncState state, string message)
    {
        if (state.LastNotice == message) return;
        state.LastNotice = message;
        StatusMessage.Text = $"{document.DisplayName}: {message}";
        SyncFilesMenuItem.ToolTip = $"외부 변경 반영 ON · {document.DisplayName}: {message}";
    }
}

