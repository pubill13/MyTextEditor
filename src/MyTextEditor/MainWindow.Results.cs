using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyTextEditor.Models;

namespace MyTextEditor;

public partial class MainWindow
{
    private Window? _resultsWindow;
    private bool _resultsMinimized;
    private int _resultFocusGeneration;

    private bool TryHandleResultsShortcut(KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.W || !ResultPanel.IsKeyboardFocusWithin) return false;
        var modifiers = Keyboard.Modifiers;
        if (modifiers != ModifierKeys.Control && modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return false;
        // Consume even on an empty result/preview area, never closing a source document.
        e.Handled = true;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            CloseAllResults_Click(this, new RoutedEventArgs());
        else if (ActiveSearchSession is { } session)
            CloseSearchSession(session);
        return true;
    }

    private void CloseSearchSession(SearchResultSession session)
    {
        var index = SearchSessions.IndexOf(session);
        if (index < 0) return;
        var restoreFocus = ResultPanel.IsKeyboardFocusWithin;
        var wasActive = ReferenceEquals(ActiveSearchSession, session);
        SearchSessions.Remove(session);
        var snapshots = session.Snapshot is { } single ? new[] { single } : session.Rows.Select(row => row.Source?.Snapshot).OfType<SearchSnapshot>().Distinct();
        foreach (var snapshot in snapshots)
        {
            snapshot.ReferenceCount--;
            if (snapshot.ReferenceCount == 0) _searchSnapshots.Remove((snapshot.Source, snapshot.Revision));
        }
        if (wasActive && SearchSessions.Count > 0)
            SearchResultTabs.SelectedIndex = Math.Min(index, SearchSessions.Count - 1);
        if (SearchSessions.Count == 0 && _pendingTransformedText is not null)
        {
            SearchResultsView.Visibility = Visibility.Collapsed;
            TransformPreviewView.Visibility = Visibility.Visible;
        }
        RefreshSearchSessionState();
        HideResultPanelIfEmpty();
        if (restoreFocus) QueueResultFocus();
    }

    private void QueueResultFocus()
    {
        var generation = ++_resultFocusGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != _resultFocusGeneration || _closingInProgress || _allowClose) return;
            if (SearchSessions.Count == 0)
            {
                if (TransformPreviewView.Visibility == Visibility.Visible) PreviewGrid.Focus();
                else
                {
                    Activate();
                    // Result tabs can be closed while their source selection is being updated;
                    // use the selected document first and the visible document as a safe fallback.
                    (CurrentEditor ?? Documents.FirstOrDefault()?.Editor)?.FocusEditor();
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => (CurrentEditor ?? Documents.FirstOrDefault()?.Editor)?.FocusEditor()));
                }
            }
            else if (_activeResultsList is { } list && list.DataContext == ActiveSearchSession && list.Items.Count > 0)
                list.Focus();
            else if (SearchResultTabs.ItemContainerGenerator.ContainerFromItem(ActiveSearchSession!) is TabItem tab)
                tab.Focus();
            else SearchResultTabs.Focus();
        }));
    }

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
            Title = "검색 결과 · OmniEdit", Owner = this, DataContext = this, Icon = Icon,
            Width = 1000, Height = 460, MinWidth = 640, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = ResultPanel
        };
        window.Resources.MergedDictionaries.Add(Resources);
        window.SetResourceReference(Window.BackgroundProperty, "SurfaceBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.PreviewKeyDown += (_, e) => TryHandleResultsShortcut(e);
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
