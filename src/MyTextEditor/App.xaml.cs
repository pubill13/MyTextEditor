using System.Windows;

namespace MyTextEditor;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) await window.OpenStartupFilesAsync(e.Args);
    }
}
