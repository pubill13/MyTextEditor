using System.Windows;
using System.Windows.Controls;

namespace MyTextEditor;

public partial class MainWindow
{
    private Window? _toolsWindow;
    private bool IsToolsVisible => ToolPanel.Visibility == Visibility.Visible &&
        (_toolsWindow is null || _toolsWindow.IsVisible);

    private void DetachTools_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsWindow is { } existing) { existing.Close(); return; }
        if (ToolColumn.ActualWidth >= 280) _settings.ToolPanelWidth = ToolColumn.ActualWidth;
        ToolPanel.DataContext = this;
        MainContentGrid.Children.Remove(ToolPanel);
        ToolColumn.MinWidth = 0;
        ToolColumn.Width = new GridLength(0);
        ToolSplitterColumn.Width = new GridLength(0);
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = "검색·정리 · OmniEdit", Owner = this, Icon = Icon, DataContext = this,
            Width = Math.Clamp(_settings.ToolsWindowWidth, 360, Math.Max(360, area.Width)),
            Height = Math.Clamp(_settings.ToolsWindowHeight, 420, Math.Max(420, area.Height)),
            MinWidth = 360, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = ToolPanel
        };
        if (_settings.ToolsWindowLeft is double left && _settings.ToolsWindowTop is double top)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - window.Width));
            window.Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
        }
        window.Resources.MergedDictionaries.Add(Resources);
        window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.KeyDown += Window_KeyDown;
        window.PreviewMouseDown += (_, _) => ++_panelFocusGeneration;
        window.LocationChanged += (_, _) => RememberToolsWindowPlacement(window);
        window.SizeChanged += (_, _) => RememberToolsWindowPlacement(window);
        window.Closed += (_, _) =>
        {
            RememberToolsWindowPlacement(window);
            window.Content = null;
            _toolsWindow = null;
            if (_closingInProgress || _allowClose) return;
            MainContentGrid.Children.Add(ToolPanel);
            DetachToolsButton.Content = "별도 창으로";
            DetachToolsButton.ToolTip = "검색·정리를 별도 창으로 열기";
            RefreshToolsTarget();
            SetToolsVisible(true);
            Activate();
        };
        _toolsWindow = window;
        DetachToolsButton.Content = "다시 붙이기";
        DetachToolsButton.ToolTip = "검색·정리를 메인 창 오른쪽에 다시 붙이기";
        RefreshToolsTarget();
        SetToolsVisible(true);
    }

    private void RefreshToolsTarget()
    {
        if (ToolsTargetText is null) return;
        ToolsTargetText.Visibility = _toolsWindow is null ? Visibility.Collapsed : Visibility.Visible;
        ToolsTargetText.Text = $"대상: {CurrentDocument?.DisplayName ?? "열린 문서 없음"}";
        ToolsTargetText.ToolTip = CurrentDocument?.FilePath ?? ToolsTargetText.Text;
    }

    private void RememberToolsWindowPlacement(Window window)
    {
        if (!window.IsLoaded || window.WindowState != WindowState.Normal) return;
        _settings.ToolsWindowWidth = window.Width;
        _settings.ToolsWindowHeight = window.Height;
        _settings.ToolsWindowLeft = window.Left;
        _settings.ToolsWindowTop = window.Top;
        MarkSettingsDirty();
    }

    private void CloseToolsWindowForShutdown()
    {
        if (_toolsWindow is not { } window) return;
        RememberToolsWindowPlacement(window);
        window.Close();
    }
}
