using System.Windows;
using System.IO;
using MyTextEditor.Services;
using System.Windows.Threading;

namespace MyTextEditor;

public partial class App : System.Windows.Application
{
    private FileOpenCoordinator? _fileOpenCoordinator;
    internal RecoveryStore? RecoveryStore { get; private set; }
    internal SemaphoreSlim RecoveryGate { get; } = new(1, 1);
    internal CancellationTokenSource RecoveryCancellation { get; } = new();
    internal Dictionary<Guid, RecoveryCaptureState> RecoveryStates { get; } = [];
    private DispatcherTimer? _recoveryTimer;
    private bool _recoveryTickRunning;

    private bool TryOpenAdditionalWindow()
    {
        if (Dispatcher.HasShutdownStarted || !Windows.OfType<MainWindow>().Any(window => window.CanReceiveDocument))
            return false;
        var window = new MainWindow();
        window.Show();
        window.Activate();
        return true;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => DiagnosticLog.Error("dispatcher-unhandled", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) DiagnosticLog.Error("process-unhandled", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => DiagnosticLog.Error("task-unobserved", args.Exception);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string[] paths;
        try
        {
            paths = e.Args.Select(Path.GetFullPath).ToArray();
            _fileOpenCoordinator = new FileOpenCoordinator(Dispatcher);
            if (!_fileOpenCoordinator.TryBecomeReceiver())
            {
                await _fileOpenCoordinator.ForwardAsync(paths);
                Shutdown();
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Windows.MessageBox.Show($"파일을 기존 창에서 열 수 없습니다.\n\n{exception.Message}", "OmniEdit 파일 열기", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // A Win32 executable icon is not inherited by WPF's native window handles.
        var icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/OmniEdit;component/Assets/AppIcon.ico", UriKind.Absolute));
        icon.Freeze();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window && window.Icon is null) window.Icon = icon;
            }));
        Exception? recoveryInitializationError = null;
        try { RecoveryStore = await Task.Run(() => new RecoveryStore()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("recovery-initialize", exception);
            recoveryInitializationError = exception;
        }
        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        window.Show();
        if (RecoveryStore is not null)
        {
            _recoveryTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, RecoveryTimer_Tick, Dispatcher);
            _recoveryTimer.Start();
        }
        if (recoveryInitializationError is not null)
        {
            window.ReportRecoveryError($"자동 복구 저장소를 열지 못했습니다: {recoveryInitializationError.Message}");
            System.Windows.MessageBox.Show(window, "자동 복구 저장소를 열지 못했습니다. 미저장 문서는 자동 복구되지 않습니다.\n\n" + recoveryInitializationError.Message,
                "OmniEdit 자동 복구", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // Queue the launch files before enabling external requests; the normal open gate preserves order.
        var startup = paths.Length > 0 ? window.OpenStartupFilesAsync(paths) : Task.CompletedTask;
        _fileOpenCoordinator.Start(
            files => files.Length == 0 ? TryOpenAdditionalWindow() : Windows.OfType<MainWindow>().FirstOrDefault(candidate => candidate.IsActive)?.TryAcceptExternalFiles(files)
                ?? Windows.OfType<MainWindow>().FirstOrDefault()?.TryAcceptExternalFiles(files) ?? false,
            error => Windows.OfType<MainWindow>().FirstOrDefault()?.ReportFileOpenDeliveryError(error));
        await startup;
        if (RecoveryStore is not null && Windows.OfType<MainWindow>().Contains(window))
        {
            await OfferRecoveryAsync(window);
        }
    }

    private async void RecoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (_recoveryTickRunning || RecoveryCancellation.IsCancellationRequested) return;
        _recoveryTickRunning = true;
        try
        {
            foreach (var window in Windows.OfType<MainWindow>().ToArray())
                await window.CaptureRecoveryDocumentsAsync();
        }
        finally { _recoveryTickRunning = false; }
    }

    private async Task OfferRecoveryAsync(MainWindow window)
    {
        IReadOnlyList<RecoveryClaim> claims = [];
        try
        {
            claims = await RecoveryStore!.ClaimOrphanSessionsAsync(RecoveryCancellation.Token);
            if (!Windows.OfType<MainWindow>().Contains(window)) return;
            var count = claims.Sum(claim => claim.Documents.Count);
            var issues = claims.Sum(claim => claim.Issues.Count);
            if (count == 0)
            {
                if (issues > 0) window.ReportRecoveryError($"읽지 못한 복구 파일 {issues}개를 그대로 보존했습니다.");
                return;
            }
            var answer = System.Windows.MessageBox.Show(window,
                $"이전에 종료된 세션의 미저장 문서 {count}개가 있습니다. 복구할까요?\n\n예: 미저장 탭으로 열기 (Undo 기록 제외)\n아니오: 복구본을 보관하고 다음 실행 시 다시 확인" +
                (issues > 0 ? $"\n\n손상되어 읽지 못한 복구 파일 {issues}개도 그대로 보존합니다." : ""),
                "OmniEdit 자동 복구", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            foreach (var claim in claims)
            foreach (var info in claim.Documents)
            {
                if (!Windows.OfType<MainWindow>().Contains(window)) return;
                try
                {
                    var snapshot = await claim.ReadAsync(info.DocumentId, RecoveryCancellation.Token);
                    // Keep the old session's only copy until this session has a durable checkpoint.
                    if (await window.RestoreRecoveryAsync(snapshot)) await claim.DiscardAsync(info.DocumentId, RecoveryCancellation.Token);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Error("recovery-restore", exception);
                    window.ReportRecoveryError($"문서를 복구하지 못했습니다. 기존 복구본을 보존합니다: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (RecoveryCancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("recovery-discover", exception);
            window.ReportRecoveryError($"이전 복구본을 확인하지 못했습니다: {exception.Message}");
        }
        finally
        {
            foreach (var claim in claims) await claim.DisposeAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _recoveryTimer?.Stop();
        RecoveryCancellation.Cancel();
        RecoveryStore?.Dispose();
        _fileOpenCoordinator?.Dispose();
        base.OnExit(e);
    }
}
