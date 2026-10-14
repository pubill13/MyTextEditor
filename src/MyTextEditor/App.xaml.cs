using System.Windows;
using System.IO;
using MyTextEditor.Services;

namespace MyTextEditor;

public partial class App : System.Windows.Application
{
    private FileOpenCoordinator? _fileOpenCoordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string[] paths;
        try
        {
            paths = e.Args.Select(Path.GetFullPath).ToArray();
            _fileOpenCoordinator = new FileOpenCoordinator(Dispatcher);
            if (!_fileOpenCoordinator.TryBecomeReceiver() && paths.Length > 0)
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
        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        // Queue the launch files before enabling external requests; the normal open gate preserves order.
        var startup = paths.Length > 0 ? window.OpenStartupFilesAsync(paths) : Task.CompletedTask;
        _fileOpenCoordinator.Start(window.TryAcceptExternalFiles, window.ReportFileOpenDeliveryError);
        await startup;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fileOpenCoordinator?.Dispose();
        base.OnExit(e);
    }
}
