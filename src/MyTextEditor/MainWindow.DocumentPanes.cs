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

    private long _documentFocusGeneration;

    private void InitializeDocumentPanes()
    {
        InitializeDocumentTabDrag();
        Deactivated += (_, _) => ++_documentFocusGeneration;
        PreviewMouseDown += (_, _) => ++_documentFocusGeneration;
        PreviewKeyDown += (_, _) => ++_documentFocusGeneration;
        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox)
                ++_documentFocusGeneration;
        };
        Loaded += (_, _) => FocusCurrentDocumentAfterLayout();
        Documents.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) { LeftDocuments.Clear(); RightDocuments.Clear(); }
            if (e.OldItems is not null)
                foreach (DocumentViewModel document in e.OldItems) { LeftDocuments.Remove(document); RightDocuments.Remove(document); }
            if (e.NewItems is not null)
                foreach (DocumentViewModel document in e.NewItems)
                    (ActiveDocumentTabs == RightDocumentTabs ? RightDocuments : LeftDocuments).Add(document);
            // File drops add documents without going through NewDocument; the empty overlay must
            // follow the collection or it covers WPF tab headers above the native editor.
            EmptyDocumentState.Visibility = Documents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
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
        var generation = ++_documentFocusGeneration;
        var expectedDocument = CurrentDocument;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_closingInProgress || _closingAllDocuments || generation != _documentFocusGeneration ||
                !ReferenceEquals(expectedDocument, CurrentDocument)) return;
            if (CurrentDocument is { } document)
            {
                ActiveDocumentTabs.UpdateLayout();
                document.Editor.Focus();
                document.Editor.FocusEditor();
            }
            else { Focus(); Keyboard.Focus(this); }
        }));
    }
}
