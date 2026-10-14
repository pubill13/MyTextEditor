using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Threading;

namespace MyTextEditor.Services;

/// <summary>One receiver per user/session; other no-argument launches remain independent windows.</summary>
public sealed class FileOpenCoordinator : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly Dispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly Mutex _receiverMutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEvent _started = new(false);
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _receiverThread;
    private Func<string[], bool>? _accept;
    private Action<Exception>? _report;
    private volatile bool _ownsMutex;
    private volatile bool _disposed;

    public FileOpenCoordinator(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows 사용자 ID를 확인할 수 없습니다.");
        _pipeName = $"OmniEdit.FileOpen.v1.{user}.{Process.GetCurrentProcess().SessionId}";
        _receiverMutex = new Mutex(false, @"Local\" + _pipeName);
        _receiverThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "OmniEdit file receiver" };
        _receiverThread.Start();
        _ready.Wait();
    }

    public bool TryBecomeReceiver()
    {
        _dispatcher.VerifyAccess();
        return _ownsMutex;
    }

    public void Start(Func<string[], bool> accept, Action<Exception> report)
    {
        _dispatcher.VerifyAccess();
        _accept = accept;
        _report = report;
        _started.Set();
    }

    private void ReceiveLoop()
    {
        try
        {
            try { _ownsMutex = _receiverMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            _ready.Set();
            if (WaitHandle.WaitAny([_stop.Token.WaitHandle, _started]) == 0) return;
            if (!_ownsMutex)
            {
                // Wait at the OS mutex rather than polling: an existing window takes over on release.
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (bytes.Length > MaxMessageBytes) throw new IOException("한 번에 전달할 파일 경로가 너무 많습니다.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(timeout.Token);
            var pid = new byte[4];
            await client.ReadExactlyAsync(pid, timeout.Token);
            // Explorer granted foreground rights to this launch; pass them to the receiving process.
            AllowSetForegroundWindow(BitConverter.ToInt32(pid));
            await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token);
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
            var reply = new byte[1];
            await client.ReadExactlyAsync(reply, timeout.Token);
            if (reply[0] != 1) throw new IOException("기존 창이 종료 또는 저장 확인 중이라 파일 열기를 접수하지 않았습니다. 확인을 마친 뒤 다시 열어 주세요.");
        }
        catch (OperationCanceledException exception)
        {
            throw new IOException("3초 안에 기존 창의 파일 열기 접수를 확인하지 못했습니다. 기존 창을 확인한 뒤 다시 시도하세요.", exception);
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
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), request.Token);
                    var size = new byte[4];
                    await pipe.ReadExactlyAsync(size, request.Token);
                    var length = BitConverter.ToInt32(size);
                    if (length <= 0 || length > MaxMessageBytes) throw new IOException("잘못된 파일 열기 요청 크기입니다.");
                    var bytes = new byte[length];
                    await pipe.ReadExactlyAsync(bytes, request.Token);
                    var paths = JsonSerializer.Deserialize<string[]>(bytes);
                    if (paths is null || paths.Length == 0 || paths.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
                        throw new IOException("파일 열기 요청에 올바른 절대 경로가 없습니다.");
                    var accepted = await _dispatcher.InvokeAsync(() =>
                        !request.IsCancellationRequested && !_disposed && _accept!(paths), DispatcherPriority.Normal, request.Token);
                    await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, request.Token);
                    await pipe.FlushAsync(request.Token);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException)
                {
                    // A disconnected/malformed sender must not stop later file-open requests.
                    if (!_stop.IsCancellationRequested) Report(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Report(exception);
        }
    }

    private void Report(Exception exception)
    {
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(() => _report?.Invoke(exception));
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        // Cancellation also removes queued Dispatcher callbacks, so this cannot wait on UI work.
        _receiverThread.Join();
        _stop.Dispose();
        _started.Dispose();
        _ready.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
