using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyTextEditor.Models;
using TabControl = System.Windows.Controls.TabControl;

namespace MyTextEditor;

public partial class MainWindow
{
    public ObservableCollection<DocumentViewModel> LeftDocuments { get; } = [];
    public ObservableCollection<DocumentViewModel> RightDocuments { get; } = [];
    private TabControl? _activeDocumentTabs;
    private TabControl ActiveDocumentTabs => _activeDocumentTabs ?? DocumentTabs;

    private void InitializeDocumentPanes()
    {
        Documents.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) { LeftDocuments.Clear(); RightDocuments.Clear(); }
            if (e.OldItems is not null)
                foreach (DocumentViewModel document in e.OldItems) { LeftDocuments.Remove(document); RightDocuments.Remove(document); }
            if (e.NewItems is not null)
                foreach (DocumentViewModel document in e.NewItems)
                    (ActiveDocumentTabs == RightDocumentTabs ? RightDocuments : LeftDocuments).Add(document);
        };
    }

    private void LogCleanupOptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LogCleanupOptionsDialog(_settings.LogCleanup) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.LogCleanup = dialog.Preferences;
        MarkSettingsDirty();
        StatusMessage.Text = "로그 정리 옵션을 저장했습니다. 미리보기로 결과를 확인하세요.";
    }

    private void SelectDocument(DocumentViewModel document)
    {
        var tabs = RightDocuments.Contains(document) ? RightDocumentTabs : DocumentTabs;
        _activeDocumentTabs = tabs;
        tabs.SelectedItem = document;
        UpdateStatus();
    }

    private void DocumentPane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _activeDocumentTabs = (TabControl)sender;
        UpdateStatus();
    }

    private void DocumentPane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _activeDocumentTabs = (TabControl)sender;
        UpdateStatus();
    }

    private void SetDocumentSplit(bool enabled)
    {
        SplitDocumentsMenuItem.IsChecked = enabled;
        RightDocumentTabs.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        DocumentPaneSplitterColumn.Width = new GridLength(enabled ? 5 : 0);
        RightDocumentColumn.Width = enabled ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void ToggleDocumentSplit_Click(object sender, RoutedEventArgs e)
    {
        if (SplitDocumentsMenuItem.IsChecked)
        {
            SetDocumentSplit(true);
            if (LeftDocuments.Count > 1 && CurrentDocument is { } document) MoveToPane(document, true);
            else if (RightDocuments.Count == 0) { _activeDocumentTabs = RightDocumentTabs; NewDocument(); }
        }
        else
        {
            var active = CurrentDocument;
            foreach (var document in RightDocuments.ToArray()) MoveToPane(document, false);
            SetDocumentSplit(false);
            _activeDocumentTabs = DocumentTabs;
            if (active is not null) SelectDocument(active);
        }
        FocusCurrentDocumentAfterLayout();
    }

    private void MoveDocumentPane_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentViewModel document) return;
        SetDocumentSplit(true);
        MoveToPane(document, !RightDocuments.Contains(document));
        FocusCurrentDocumentAfterLayout();
    }

    private void MoveToPane(DocumentViewModel document, bool right)
    {
        var source = RightDocuments.Contains(document) ? RightDocuments : LeftDocuments;
        source.Remove(document);
        DocumentTabs.UpdateLayout(); RightDocumentTabs.UpdateLayout();
        (right ? RightDocuments : LeftDocuments).Add(document);
        SelectDocument(document);
    }

    private void FocusCurrentDocumentAfterLayout()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_closingInProgress || _closingAllDocuments) return;
            if (CurrentDocument is { } document) document.Editor.FocusEditor();
            else { Focus(); Keyboard.Focus(this); }
        }));
    }
}
