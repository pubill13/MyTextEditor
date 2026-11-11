using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MyTextEditor.Services;

namespace MyTextEditor.Diff;

public partial class DiffTabView
{
    private readonly DispatcherTimer _externalChangeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _externalChangeLifetime = new();
    private readonly Dictionary<DiffEndpoint, ExternalSideState> _externalSideStates = [];
    private bool _externalPolling;
    private bool _externalCheckScheduled;
    private bool _externalPaused;
    private int _externalOperationCount;
    private bool _externalReloadApplied;
    private int _externalCheckGeneration;

    private readonly record struct ExternalFileStamp(bool Exists, long Length, DateTime LastWriteUtc, DateTime CreationUtc);
    private readonly record struct ExternalVersion(long? Revision, ExternalFileStamp? File);
    private sealed class ExternalSideState(string? path)
    {
        public string? Path { get; } = path;
        public ExternalFileStamp? Baseline { get; set; }
        public ExternalVersion? Pending { get; set; }
        public long PendingSince { get; set; }
        public ExternalVersion? LastPrompted { get; set; }
        public bool HasChanges { get; set; }
    }

    private void InitializeExternalChanges()
    {
        _externalChangeTimer.Tick += ExternalChangeTimer_Tick;
        _externalChangeTimer.Start();
        RequestExternalChangeCheck();
    }

    private void StopExternalChanges()
    {
        _externalChangeTimer.Stop();
        _externalChangeTimer.Tick -= ExternalChangeTimer_Tick;
        _externalChangeLifetime.Cancel();
        _externalSideStates.Clear();
    }

    private void PauseExternalChanges() { _externalPaused = true; ++_externalCheckGeneration; }
    private void ResumeExternalChanges() { _externalPaused = false; RequestExternalChangeCheck(); }

    private bool CanCheckExternalChanges => _initialized && !_resourcesReleased && !_externalPaused && _externalOperationCount == 0 && _isActive &&
        IsVisible && Window.GetWindow(this) is { IsVisible: true };

    private async void ExternalChangeTimer_Tick(object? sender, EventArgs e) => await PollExternalChangesAsync();

    private void RequestExternalChangeCheck()
    {
        if (_externalCheckScheduled || !CanCheckExternalChanges) return;
        _externalCheckScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            _externalCheckScheduled = false;
            await PollExternalChangesAsync();
        }));
    }

    private ExternalSideState GetExternalSideState(DiffEndpoint endpoint)
    {
        if (!_externalSideStates.TryGetValue(endpoint, out var state) || state.Path != endpoint.FilePath)
            _externalSideStates[endpoint] = state = new ExternalSideState(endpoint.FilePath);
        return state;
    }

    private bool HasPendingExternalChange(bool left)
    {
        var endpoint = left ? _left : _right;
        if (endpoint.SourceDocumentId is { } id)
            return _callbacks.GetSourceRevision(id) != endpoint.SourceRevision;
        return _externalSideStates.TryGetValue(endpoint, out var state) && state.HasChanges;
    }

    private async Task ReloadExternalChangesAsync() => await PollExternalChangesAsync(manual: true);

    private async Task PollExternalChangesAsync(bool manual = false)
    {
        // Native editor callbacks can install the WinForms synchronization context.
        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
        {
            await Dispatcher.InvokeAsync(() => PollExternalChangesAsync(manual)).Task.Unwrap();
            return;
        }
        if (_externalPolling || !CanCheckExternalChanges) return;
        _externalPolling = true;
        _externalReloadApplied = false;
        try
        {
            foreach (var unused in _externalSideStates.Keys.Where(endpoint => endpoint != _left && endpoint != _right).ToArray())
                _externalSideStates.Remove(unused);
            await CheckExternalSideAsync(true, manual);
            if (CanCheckExternalChanges) await CheckExternalSideAsync(false, manual);
            if (CanCheckExternalChanges) CheckStaleSources();
        }
        finally
        {
            _externalPolling = false;
            if (_externalReloadApplied && CanCheckExternalChanges && _needsComparison && IsReady) await CompareNowAsync();
        }
    }

    private static ExternalFileStamp ReadExternalFileStamp(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : new(false, 0, default, default);
    }

    private async Task CheckExternalSideAsync(bool left, bool manual)
    {
        var endpoint = left ? _left : _right;
        if (!endpoint.IsReady || endpoint.SourceDocumentId is null && endpoint.FilePath is null) return;
        var editor = left ? _leftEditor : _rightEditor;
        var state = GetExternalSideState(endpoint);
        var generation = _externalCheckGeneration;
        bool IsCurrent() => CanCheckExternalChanges && generation == _externalCheckGeneration &&
            ReferenceEquals(endpoint, left ? _left : _right) && endpoint.FilePath == state.Path;
        try
        {
            ExternalVersion version;
            if (endpoint.SourceDocumentId is { } id)
            {
                var sourceRevision = _callbacks.GetSourceRevision(id);
                if (sourceRevision < 0)
                {
                    state.HasChanges = true;
                    if (manual) StatusText.Text = "원본 문서가 이미 닫혀 다시 불러올 수 없습니다. 현재 비교 내용은 유지합니다.";
                    return;
                }
                version = new(sourceRevision, null);
                state.HasChanges = sourceRevision != endpoint.SourceRevision;
            }
            else
            {
                var stamp = await Task.Run(() => ReadExternalFileStamp(state.Path!), _externalChangeLifetime.Token);
                if (!IsCurrent()) return;
                // A save updates the endpoint stamp; treat it as the new baseline too.
                if (state.Baseline is null || state.Baseline.Value.Length != endpoint.SourceFileLength ||
                    state.Baseline.Value.LastWriteUtc != endpoint.SourceFileLastWriteUtc)
                {
                    state.Baseline = endpoint.SourceFileLength is { } length && endpoint.SourceFileLastWriteUtc is { } written
                        ? new(true, length, written, stamp.CreationUtc) : stamp;
                    endpoint.SourceFileLength ??= stamp.Length;
                    endpoint.SourceFileLastWriteUtc ??= stamp.LastWriteUtc;
                }
                version = new(null, stamp);
                state.HasChanges = stamp != state.Baseline;
                if (!stamp.Exists)
                {
                    state.HasChanges = true;
                    StatusText.Text = $"{endpoint.DisplayName}: 파일이 이동되거나 삭제되었습니다. 현재 내용은 유지합니다.";
                    return;
                }
            }
            if (!state.HasChanges) { state.Pending = null; return; }
            if (!manual && state.LastPrompted == version) return;
            if (state.Pending != version)
            {
                state.Pending = version;
                state.PendingSince = Stopwatch.GetTimestamp();
                if (!manual) return;
            }
            if (!manual && Stopwatch.GetElapsedTime(state.PendingSince).TotalMilliseconds < 500) return;
            if (!IsCurrent()) return;
            var bufferRevision = editor.ContentRevision;
            var dirty = left ? _leftDirty : _rightDirty;
            var alreadyApproved = !dirty && endpoint.SourceDocumentId is { } documentId &&
                ExternalChangeConfirmation.WasDocumentReloadApproved(documentId, version.Revision!.Value);
            var approval = alreadyApproved ? true : ExternalChangeConfirmation.TryConfirm(Window.GetWindow(this),
                $"{(left ? "왼쪽" : "오른쪽")} {endpoint.DisplayName}", dirty);
            if (approval is null) return;
            state.LastPrompted = version;
            if (approval != true)
            {
                StatusText.Text = "현재 비교 내용을 유지합니다. 같은 변경에는 다시 묻지 않습니다. 원본 다시 불러오기로 수동 반영할 수 있습니다.";
                return;
            }
            if (!IsCurrent() || editor.ContentRevision != bufferRevision) return;

            DiffSourceSnapshot snapshot;
            if (endpoint.SourceDocumentId is { } sourceId)
            {
                if (_callbacks.GetSourceSnapshot(sourceId) is not { } source || source.Revision != version.Revision) return;
                snapshot = source;
            }
            else
            {
                var loaded = await _fileService.LoadAsync(state.Path!, _externalChangeLifetime.Token);
                var afterRead = await Task.Run(() => ReadExternalFileStamp(state.Path!), _externalChangeLifetime.Token);
                if (version.File != afterRead) return;
                snapshot = new(endpoint.DisplayName, loaded.Text, loaded.FilePath, loaded.Encoding,
                    loaded.HasByteOrderMark, loaded.NewLine, endpoint.SourceRevision);
            }
            if (!IsCurrent() || editor.ContentRevision != bufferRevision ||
                endpoint.SourceDocumentId is { } currentId && _callbacks.GetSourceRevision(currentId) != snapshot.Revision) return;
            ApplyExternalSnapshot(left, snapshot);
            if (version.File is { } appliedFile)
            {
                state.Baseline = appliedFile;
                endpoint.SourceFileLength = appliedFile.Length;
                endpoint.SourceFileLastWriteUtc = appliedFile.LastWriteUtc;
            }
            state.HasChanges = false;
            state.Pending = null;
        }
        catch (OperationCanceledException) when (_externalChangeLifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (IsCurrent())
            {
                state.HasChanges = true;
                StatusText.Text = $"{endpoint.DisplayName}: 원본 변경을 읽지 못했습니다. '원본 다시 불러오기'로 재시도하세요. {exception.Message}";
            }
        }
    }

    private void ApplyExternalSnapshot(bool left, DiffSourceSnapshot snapshot)
    {
        var endpoint = left ? _left : _right;
        var editor = left ? _leftEditor : _rightEditor;
        var leftTop = _leftEditor.FirstVisibleLine;
        var rightTop = _rightEditor.FirstVisibleLine;
        var leftX = _leftEditor.HorizontalOffset;
        var rightX = _rightEditor.HorizontalOffset;
        var selection = editor.SelectionRange;
        var readOnly = editor.IsReadOnly;
        var wasSyncing = _syncingScroll;
        _comparisonCancellation?.Cancel();
        _inlineCancellation?.Cancel();
        _recompareTimer.Stop();
        ++_generation;
        ClearSnapshotCache();
        InvalidateDisplayedResult();
        _needsComparison = true;
        _externalReloadApplied = true;
        _updatingEditors = true;
        _syncingScroll = true;
        try
        {
            editor.IsReadOnly = false;
            editor.ReloadUtf8(Encoding.UTF8.GetBytes(snapshot.Text));
            editor.SetNewLine(snapshot.NewLine);
            var start = Math.Min(selection.Start, snapshot.Text.Length);
            var end = Math.Min(selection.End, snapshot.Text.Length);
            if (start > 0 && start < snapshot.Text.Length && char.IsLowSurrogate(snapshot.Text[start]) && char.IsHighSurrogate(snapshot.Text[start - 1])) start--;
            if (end > 0 && end < snapshot.Text.Length && char.IsLowSurrogate(snapshot.Text[end]) && char.IsHighSurrogate(snapshot.Text[end - 1])) end--;
            editor.SetSelection(new(start, end - start));
            _leftEditor.SetFirstVisibleLine(leftTop);
            _rightEditor.SetFirstVisibleLine(rightTop);
            _leftEditor.SetHorizontalOffset(leftX);
            _rightEditor.SetHorizontalOffset(rightX);
        }
        finally
        {
            editor.IsReadOnly = readOnly;
            _updatingEditors = false;
            _syncingScroll = wasSyncing;
        }
        endpoint.DisplayName = snapshot.DisplayName;
        endpoint.Text = snapshot.Text;
        endpoint.FilePath = snapshot.FilePath;
        endpoint.Encoding = snapshot.Encoding;
        endpoint.HasByteOrderMark = snapshot.HasByteOrderMark;
        endpoint.NewLine = snapshot.NewLine;
        endpoint.SourceRevision = snapshot.Revision;
        endpoint.HasUnsavedSourceSnapshot = false;
        if (left) _leftDirty = false; else _rightDirty = false;
        RefreshEndpointHeaders();
        CheckStaleSources();
    }
}
