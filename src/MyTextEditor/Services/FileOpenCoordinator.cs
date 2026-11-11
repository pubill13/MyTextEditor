using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Threading;

namespace MyTextEditor.Services;

public sealed record FileOpenRequest(string[] Paths, long StartedUtcTicks, int ProcessId)
{
    public static FileOpenRequest Create(string[] paths) =>
        new(paths, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Environment.ProcessId);
}

/// <summary>Receives paths before WPF startup; delivery acknowledgements never wait for the UI.</summary>
public sealed class FileOpenCoordinator : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly Dispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly Mutex _receiverMutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEvent _started = new(false);
    private readonly Thread _receiverThread;
    private readonly object _gate = new();
    private readonly List<FileOpenRequest> _pending = [];
    private Func<FileOpenRequest, bool>? _accept;
    private Action<Exception>? _report;
    private DispatcherTimer? _retryTimer;
    private bool _drainScheduled;
    private bool _accepting = true;
    private volatile bool _ownsMutex;
    private volatile bool _disposed;

    public FileOpenCoordinator(Dispatcher dispatcher, string? instanceName = null)
    {
        _dispatcher = dispatcher;
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows 사용자 ID를 확인할 수 없습니다.");
        _pipeName = $"OmniEdit.FileOpen.v2.{user}.{Process.GetCurrentProcess().SessionId}" +
            (instanceName is null ? string.Empty : "." + instanceName);
        _receiverMutex = new Mutex(false, @"Local\" + _pipeName);
        _receiverThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "OmniEdit file receiver" };
        _receiverThread.Start();
        _ready.Wait();
    }

    public bool TryBecomeReceiver() => _ownsMutex;
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public void Start(Func<string[], bool> accept, Action<Exception> report) =>
        StartRequests(request => accept(request.Paths), report);

    public void StartRequests(Func<FileOpenRequest, bool> accept, Action<Exception> report)
    {
        _dispatcher.VerifyAccess();
        lock (_gate)
        {
            _accept = accept;
            _report = report;
            _retryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
            _retryTimer.Tick += Retry_Tick;
            ScheduleDrain();
            _started.Set();
        }
    }

    // Closing may reject new launches only after all acknowledged requests have left this queue.
    public bool TryPauseIfIdle()
    {
        lock (_gate)
        {
            if (_pending.Count != 0) return false;
            _accepting = false;
            return true;
        }
    }

    public void ResumeRequests()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _accepting = true;
            ScheduleDrain();
        }
    }

    private void ReceiveLoop()
    {
        try
        {
            try { _ownsMutex = _receiverMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            _ready.Set();
            if (!_ownsMutex)
            {
                // A forwarding-only launch must never receive and acknowledge its own request.
                if (WaitHandle.WaitAny([_stop.Token.WaitHandle, _started]) == 0) return;
                try { _ownsMutex = WaitHandle.WaitAny([_stop.Token.WaitHandle, _receiverMutex]) == 1; }
                catch (AbandonedMutexException) { _ownsMutex = true; }
            }
            if (_ownsMutex && !_stop.IsCancellationRequested) ServeAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _ready.Set();
            if (_ownsMutex) _receiverMutex.ReleaseMutex();
            _ownsMutex = false;
            _receiverMutex.Dispose();
        }
    }

    public async Task ForwardAsync(string[] paths)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(FileOpenRequest.Create(paths));
        if (bytes.Length > MaxMessageBytes) throw new IOException("한 번에 전달할 파일 경로가 너무 많습니다.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var pid = new byte[4];
            await client.ReadExactlyAsync(pid, timeout.Token).ConfigureAwait(false);
            AllowSetForegroundWindow(BitConverter.ToInt32(pid));
            await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var reply = new byte[1];
            await client.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            if (reply[0] != 1) throw new IOException("기존 창이 종료 확인 중이라 요청을 접수하지 않았습니다. 확인을 마친 뒤 다시 열어 주세요.");
        }
        catch (OperationCanceledException exception)
        {
            throw new IOException("실행 중인 OmniEdit에 파일 열기 요청을 전달하지 못했습니다. 프로그램 상태를 확인한 뒤 다시 시도하세요.", exception);
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var token = requestTimeout.Token;
                try
                {
                    await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), token).ConfigureAwait(false);
                    var size = new byte[4];
                    await pipe.ReadExactlyAsync(size, token).ConfigureAwait(false);
                    var length = BitConverter.ToInt32(size);
                    if (length <= 0 || length > MaxMessageBytes) throw new IOException("잘못된 파일 열기 요청 크기입니다.");
                    var bytes = new byte[length];
                    await pipe.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                    var request = JsonSerializer.Deserialize<FileOpenRequest>(bytes);
                    if (request is null || request.Paths is null || request.StartedUtcTicks <= 0 || request.ProcessId <= 0 ||
                        request.Paths.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
                        throw new IOException("파일 열기 요청에 올바른 절대 경로가 없습니다.");
                    bool accepted;
                    lock (_gate)
                    {
                        accepted = !_disposed && _accepting;
                        if (accepted) { _pending.Add(request); ScheduleDrain(); }
                    }
                    await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
                    await pipe.FlushAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException)
                {
                    if (!_stop.IsCancellationRequested) Report(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Report(exception); }
    }

    // Called under _gate; one dispatcher callback drains requests accumulated during startup/I/O.
    private void ScheduleDrain()
    {
        if (_disposed || _accept is null || _pending.Count == 0 || _drainScheduled || _dispatcher.HasShutdownStarted) return;
        _drainScheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainRequests));
    }

    private void DrainRequests()
    {
        _retryTimer?.Stop();
        for (var count = 0; count < 64; count++)
        {
            FileOpenRequest request;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0) { _drainScheduled = false; return; }
                request = _pending.MinBy(item => (item.StartedUtcTicks, item.ProcessId))!;
            }
            if (!_accept!(request))
            {
                lock (_gate) _drainScheduled = false;
                _retryTimer!.Start();
                return;
            }
            lock (_gate) _pending.Remove(request);
        }
        lock (_gate) { _drainScheduled = false; ScheduleDrain(); }
    }

    private void Retry_Tick(object? sender, EventArgs e)
    {
        _retryTimer!.Stop();
        lock (_gate) ScheduleDrain();
    }

    private void Report(Exception exception)
    {
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => _report?.Invoke(exception));
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _retryTimer?.Stop();
        if (_retryTimer is not null) _retryTimer.Tick -= Retry_Tick;
        _stop.Cancel();
        _receiverThread.Join();
        _stop.Dispose();
        _started.Dispose();
        _ready.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
