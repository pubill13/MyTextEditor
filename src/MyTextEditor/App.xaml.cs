using System.Windows;

namespace MyTextEditor;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // A Win32 executable icon is not inherited by WPF's native window handles.
        var icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/OmniEdit;component/Assets/AppIcon.ico", UriKind.Absolute));
        icon.Freeze();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window && window.Icon is null) window.Icon = icon;
            }));
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) await window.OpenStartupFilesAsync(e.Args);
    }
}
