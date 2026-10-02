using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MyTextEditor;

public partial class MainWindow
{
    private Window? _toolsWindow;
    private bool IsToolsVisible => ToolPanel.Visibility == Visibility.Visible &&
        (_toolsWindow is null || _toolsWindow.IsVisible);

    private void DetachTools_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsWindow is { } existing)
        {
            DockToolsWindow(existing);
            return;
        }
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
        window.PreviewMouseDown += (_, _) => { ++_panelFocusGeneration; ++_documentFocusGeneration; };
        window.PreviewKeyDown += (_, _) => ++_documentFocusGeneration;
        window.LocationChanged += (_, _) => RememberToolsWindowPlacement(window);
        window.SizeChanged += (_, _) => RememberToolsWindowPlacement(window);
        window.Closing += (_, args) =>
        {
            RememberToolsWindowPlacement(window);
            if (_closingInProgress || _allowClose || !ReferenceEquals(_toolsWindow, window)) return;
            args.Cancel = true;
            SetToolsVisible(false);
        };
        window.Closed += (_, _) =>
        {
            window.Content = null;
            if (ReferenceEquals(_toolsWindow, window)) _toolsWindow = null;
        };
        _toolsWindow = window;
        UpdateToolsDockButton(true);
        RefreshToolsTarget();
        SetToolsVisible(true);
    }

    private void DockToolsWindow(Window window)
    {
        window.Hide();
        window.Content = null;
        _toolsWindow = null;
        window.Close();
        MainContentGrid.Children.Add(ToolPanel);
        ToolColumn.MinWidth = 280;
        ToolColumn.Width = new GridLength(Math.Max(280, _settings.ToolPanelWidth));
        ToolSplitterColumn.Width = new GridLength(5);
        ToolPanel.Visibility = Visibility.Visible;
        UpdateToolsDockButton(false);
        SetToolsVisible(true);
        RefreshToolsTarget();
        Activate();
    }

    private void UpdateToolsDockButton(bool detached)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform,
            StrokeThickness = 1.4,
            Data = System.Windows.Media.Geometry.Parse(detached
                ? "M1,1 H15 V15 H1 Z M11,1 V15 M4,8 H9 M6,5 L9,8 6,11"
                : "M5,1 H15 V11 H12 M1,5 H11 V15 H1 Z")
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush");
        DetachToolsButton.Content = icon;
        DetachToolsButton.ToolTip = detached ? "검색·정리를 우측 패널에 붙이기" : "검색·정리를 별도 창으로 열기";
        AutomationProperties.SetName(DetachToolsButton, DetachToolsButton.ToolTip.ToString());
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
