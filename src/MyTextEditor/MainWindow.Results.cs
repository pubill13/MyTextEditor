using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyTextEditor;

public partial class MainWindow
{
    private Window? _resultsWindow;
    private bool _resultsMinimized;

    private void RememberResultHeight()
    {
        if (_resultsWindow is null && !_resultsMinimized && ResultPanel.Visibility == Visibility.Visible && ResultRow.ActualHeight >= 120)
            _settings.ResultPanelHeight = ResultRow.ActualHeight;
    }

    private void ExpandResults()
    {
        RememberResultHeight();
        _resultsMinimized = false;
        ResultPanel.Visibility = Visibility.Visible;
        ResultsMenuItem.IsChecked = true;
        if (_resultsWindow is { } window)
        {
            ShowResultRestoreBar("별도 결과 창 열기 ↗");
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            return;
        }
        ResultRestoreBar.Visibility = Visibility.Collapsed;
        ResultRow.MinHeight = 120;
        ResultRow.Height = new GridLength(Math.Max(120, _settings.ResultPanelHeight));
        ResultSplitterRow.Height = new GridLength(5);
    }

    private void ShowResultRestoreBar(string label)
    {
        RestoreResultsButton.Content = label;
        ResultRestoreBar.Visibility = Visibility.Visible;
        ResultRow.MinHeight = 0;
        ResultRow.Height = new GridLength(28);
        ResultSplitterRow.Height = new GridLength(0);
    }

    private void MinimizeResults_Click(object sender, RoutedEventArgs e)
    {
        RememberResultHeight();
        _resultsMinimized = true;
        if (_resultsWindow is { } window) window.Hide();
        else ResultPanel.Visibility = Visibility.Collapsed;
        ShowResultRestoreBar(_resultsWindow is null ? "검색 결과 펼치기 ▴" : "별도 결과 창 열기 ↗");
        ResultsMenuItem.IsChecked = false;
        MarkSettingsDirty();
    }

    private void RestoreResults_Click(object sender, RoutedEventArgs e)
    {
        ExpandResults();
        _resultsWindow?.Activate();
        MarkSettingsDirty();
    }

    private void DetachResults_Click(object sender, RoutedEventArgs e)
    {
        if (_resultsWindow is { } existing) { existing.Close(); return; }
        RememberResultHeight();
        ResultPanel.DataContext = this;
        MainContentGrid.Children.Remove(ResultPanel);
        var window = new Window
        {
            Title = "검색 결과 · OmniEdit", Owner = this, DataContext = this,
            Width = 1000, Height = 460, MinWidth = 640, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = ResultPanel
        };
        window.Resources.MergedDictionaries.Add(Resources);
        window.SetResourceReference(Window.BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.Closed += (_, _) =>
        {
            window.Content = null;
            _resultsWindow = null;
            if (_closingInProgress || _allowClose) return;
            MainContentGrid.Children.Add(ResultPanel);
            DetachResultsButton.Content = "↗";
            ExpandResults();
            HideResultPanelIfEmpty();
            MarkSettingsDirty();
        };
        _resultsWindow = window;
        DetachResultsButton.Content = "↙";
        ExpandResults();
        MarkSettingsDirty();
    }

    private void CopyResultCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _activeResultsList?.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void CopyResultCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        CopySelectedResults_Click(sender, e);
        e.Handled = true;
    }
}
