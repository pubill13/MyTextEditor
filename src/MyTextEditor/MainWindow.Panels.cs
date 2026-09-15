using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using MyTextEditor.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MyTextEditor;

public partial class MainWindow
{
    private long _panelFocusGeneration;
    private TextComposition? _panelComposition;
    private bool _escapeOwnedByInput;

    private void InitializePanelInteraction()
    {
        PreviewMouseDown += (_, _) => ++_panelFocusGeneration;
        TextCompositionManager.AddPreviewTextInputStartHandler(ToolPanel, (_, e) => _panelComposition = e.TextComposition);
        TextCompositionManager.AddPreviewTextInputHandler(ToolPanel, (_, _) => _panelComposition = null);
        ToolPanel.PreviewKeyDown += (_, e) =>
        {
            _escapeOwnedByInput = e.Key == Key.Escape && (_panelComposition is not null ||
                IsOpenComboAncestor(e.OriginalSource as DependencyObject));
            if (e.Key == Key.Escape && _panelComposition is { } cancelledComposition)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (ReferenceEquals(_panelComposition, cancelledComposition)) _panelComposition = null;
                }));
        };
        FavoriteToolbarHost.SizeChanged += (_, _) => UpdateFavoriteOverflow();
        Loaded += (_, _) => UpdateToolbarLayout();
    }

    private static bool IsOpenComboAncestor(DependencyObject? source)
    {
        for (var element = source; element is Visual; element = VisualTreeHelper.GetParent(element))
            if (element is ComboBox { IsDropDownOpen: true }) return true;
        return false;
    }

    private void SetToolsVisible(bool show, bool focusEditor = false)
    {
        if (!show)
        {
            ++_panelFocusGeneration;
            if (ToolPanel.Visibility == Visibility.Visible && ToolColumn.ActualWidth >= 280)
                _settings.ToolPanelWidth = ToolColumn.ActualWidth;
        }
        var wasVisible = ToolPanel.Visibility == Visibility.Visible && ToolColumn.Width.Value > 0;
        ToolsMenuItem.IsChecked = show;
        ToolsToggleButton.IsChecked = show;
        ToolColumn.MinWidth = show ? 280 : 0;
        if (!show) ToolColumn.Width = new GridLength(0);
        else if (!wasVisible) ToolColumn.Width = new GridLength(Math.Max(280, _settings.ToolPanelWidth));
        ToolSplitterColumn.Width = new GridLength(show ? 5 : 0);
        ToolPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _settings.ToolPanelVisible = show;
        MarkSettingsDirty();
        if (!show && focusEditor) CurrentEditor?.FocusEditor();
    }

    private void HideTools_Click(object sender, RoutedEventArgs e) => SetToolsVisible(false, true);
    private void ToggleToolsButton_Click(object sender, RoutedEventArgs e)
        => SetToolsVisible(ToolsToggleButton.IsChecked == true, true);

    private void ToolTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null || e.Source != ToolTabs) return;
        ++_panelFocusGeneration;
        _settings.SelectedToolTab = Math.Max(0, ToolTabs.SelectedIndex);
        MarkSettingsDirty();
    }

    private void SearchField_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_settings is null) return;
        _settings.LastSearchField = ReferenceEquals(sender, SimpleAnyBox) ? "Any" : ReferenceEquals(sender, SimpleExcludeBox) ? "Exclude" : "All";
        MarkSettingsDirty();
    }

    private void FocusToolInput(int tab, TextBox input, bool selectAll)
    {
        // Complete composition before changing tabs/selection, preserving committed Korean input.
        if (_panelComposition is { } composition)
        {
            TextCompositionManager.CompleteComposition(composition);
            _panelComposition = null;
        }
        SetToolsVisible(true);
        ToolTabs.SelectedIndex = tab;
        var generation = ++_panelFocusGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (generation != _panelFocusGeneration || _closingInProgress || ToolPanel.Visibility != Visibility.Visible) return;
            input.BringIntoView();
            input.Focus();
            if (selectAll) input.SelectAll();
        }));
    }

    private void ToolPanel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || _escapeOwnedByInput || !ToolPanel.IsKeyboardFocusWithin) return;
        SetToolsVisible(false, true);
        e.Handled = true;
    }

    private void ResetConditions_Click(object sender, RoutedEventArgs e)
    {
        SimpleAllBox.Clear(); SimpleAnyBox.Clear(); SimpleExcludeBox.Clear();
        ShowSearchInput();
    }

    private void UpdateFavoriteOverflow()
    {
        if (!IsLoaded || FavoriteToolbarHost is null) return;
        FavoriteOverflowButton.Visibility = Visibility.Visible;
        FavoriteOverflowButton.Measure(new System.Windows.Size(double.PositiveInfinity, 32));
        FavoriteToolsEditButton.Measure(new System.Windows.Size(double.PositiveInfinity, 32));
        double available = Math.Max(0, FavoriteToolbarHost.ActualWidth - FavoriteToolsEditButton.DesiredSize.Width - FavoriteOverflowButton.DesiredSize.Width - 12);
        var overflow = false;
        foreach (var button in FavoriteToolsPanel.Children.OfType<Button>())
        {
            button.Visibility = Visibility.Visible;
            button.Measure(new System.Windows.Size(double.PositiveInfinity, 32));
            var width = button.DesiredSize.Width;
            if (overflow || width > available) { button.Visibility = Visibility.Collapsed; overflow = true; }
            else available -= width;
        }
        FavoriteOverflowButton.IsEnabled = _settings.FavoriteToolIds.Count > 0;
    }

    private void UpdateToolbarLayout()
    {
        if (!IsLoaded) return;
        var unlimited = new System.Windows.Size(double.PositiveInfinity, 40);
        PrimaryToolbarPanel.Measure(unlimited);
        ToolbarFontPanel.Visibility = Visibility.Visible;
        ToolbarFontPanel.Measure(unlimited);
        FavoriteOverflowButton.Measure(unlimited);
        FavoriteToolsEditButton.Measure(unlimited);
        var required = PrimaryToolbarPanel.DesiredSize.Width + ToolbarFontPanel.DesiredSize.Width +
            FavoriteOverflowButton.DesiredSize.Width + FavoriteToolsEditButton.DesiredSize.Width + 24;
        var overflow = MainToolbarGrid.ActualWidth < required;
        ToolbarFontPanel.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
        ToolbarOverflowPanel.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (!overflow) ToolbarOverflowPopup.IsOpen = false;
        UpdateFavoriteOverflow();
    }

    private void FavoriteOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var id in _settings.FavoriteToolIds)
        {
            if (TextTools.FirstOrDefault(tool => tool.Id == id) is not { } tool) continue;
            var item = new MenuItem { Header = tool.DisplayName, Tag = tool };
            item.Click += FavoriteTool_Click;
            menu.Items.Add(item);
        }
        menu.PlacementTarget = FavoriteOverflowButton;
        menu.IsOpen = true;
    }
}
