using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using MyTextEditor.Services;

namespace MyTextEditor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var paths = args.Select(Path.GetFullPath).ToArray();
            using var coordinator = new FileOpenCoordinator(Dispatcher.CurrentDispatcher);
            if (!coordinator.TryBecomeReceiver())
            {
                coordinator.ForwardAsync(paths).GetAwaiter().GetResult();
                return 0;
            }
            return RunApplication(coordinator, FileOpenRequest.Create(paths));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowDeliveryError(exception);
            return 1;
        }
    }

    // Forwarding launches never construct App or load WPF resources/native editors/fonts.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(FileOpenCoordinator coordinator, FileOpenRequest request)
    {
        var app = new App(coordinator, request);
        app.InitializeComponent();
        return app.Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShowDeliveryError(Exception exception) => System.Windows.MessageBox.Show(
        $"파일을 기존 창에서 열 수 없습니다.\n\n{exception.Message}", "OmniEdit 파일 열기",
        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
}
