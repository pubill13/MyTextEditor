using System.Windows;
using MyTextEditor.Help;

namespace MyTextEditor;

public partial class MainWindow
{
    private HelpWindow? _helpWindow;

    private void ShowHelpWindow()
    {
        if (_helpWindow is not null)
        {
            if (_helpWindow.WindowState == WindowState.Minimized)
                _helpWindow.WindowState = WindowState.Normal;
            _helpWindow.Activate();
            return;
        }

        var placement = new HelpWindowPlacement(
            _settings.Help.WindowWidth,
            _settings.Help.WindowHeight,
            _settings.Help.WindowLeft,
            _settings.Help.WindowTop);
        var window = new HelpWindow(placement) { Owner = this };
        window.PlacementChangedByUser += HelpWindow_PlacementChanged;
        window.Closed += (_, _) =>
        {
            window.PlacementChangedByUser -= HelpWindow_PlacementChanged;
            if (ReferenceEquals(_helpWindow, window)) _helpWindow = null;
        };
        _helpWindow = window;
        window.Show();
    }

    private void HelpWindow_PlacementChanged(HelpWindowPlacement placement)
    {
        _settings.Help.WindowWidth = placement.Width;
        _settings.Help.WindowHeight = placement.Height;
        _settings.Help.WindowLeft = placement.Left;
        _settings.Help.WindowTop = placement.Top;
        MarkSettingsDirty();
    }
}
