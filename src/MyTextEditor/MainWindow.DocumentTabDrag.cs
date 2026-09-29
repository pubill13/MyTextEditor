using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyTextEditor.Models;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using TabItem = System.Windows.Controls.TabItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace MyTextEditor;

public partial class MainWindow
{
    private DocumentViewModel? _dragDocument;
    private Point _tabDragStart;
    private bool _tabDragging;
    private bool? _tabDropRight;
    private Popup? _tabDropPreview;

    private void InitializeDocumentTabDrag()
    {
        DocumentPanesGrid.PreviewMouseLeftButtonDown += DocumentTabDragStart;
        DocumentPanesGrid.PreviewMouseMove += DocumentTabDragMove;
        DocumentPanesGrid.PreviewMouseLeftButtonUp += DocumentTabDragEnd;
        DocumentPanesGrid.LostMouseCapture += (_, _) => { if (_tabDragging) FinishDocumentTabDrag(false); };
        PreviewKeyDown += (_, e) =>
        {
            if (_tabDragging && e.Key == Key.Escape) { FinishDocumentTabDrag(false); e.Handled = true; }
        };
        Deactivated += (_, _) => FinishDocumentTabDrag(false);
        Closed += (_, _) => FinishDocumentTabDrag(false);
    }

    private void DocumentTabDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragDocument = null;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = node is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase) return;
            if (node is TabItem { DataContext: DocumentViewModel document })
            {
                // Capturing in the preview event can reroute TabItem's normal mouse-down.
                // Select first so a plain click still switches the native editor.
                SelectDocument(document);
                _dragDocument = document;
                _tabDragStart = e.GetPosition(DocumentPanesGrid);
                // Capture before the pointer can cross into the native editor between mouse messages.
                Mouse.Capture(DocumentPanesGrid, CaptureMode.Element);
                return;
            }
        }
    }

    private bool? ResolveDocumentTabDrop(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > DocumentPanesGrid.ActualWidth || point.Y > DocumentPanesGrid.ActualHeight) return null;
        if (RightDocumentTabs.IsVisible)
        {
            var rightStart = RightDocumentTabs.TranslatePoint(new Point(), DocumentPanesGrid).X;
            return point.X >= rightStart;
        }
        // Keep the rest of the editor neutral; only the edge creates a split.
        return point.X >= DocumentPanesGrid.ActualWidth - Math.Min(160, DocumentPanesGrid.ActualWidth * .25) ? true : null;
    }

    private void DocumentTabDragMove(object sender, MouseEventArgs e)
    {
        if (_dragDocument is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishDocumentTabDrag(false); return; }
        var point = e.GetPosition(DocumentPanesGrid);
        if (!_tabDragging)
        {
            if (Math.Abs(point.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _tabDragging = Mouse.Capture(DocumentPanesGrid, CaptureMode.Element);
            if (!_tabDragging) { FinishDocumentTabDrag(false); return; }
        }
        var previousTarget = _tabDropRight;
        _tabDropRight = ResolveDocumentTabDrop(point);
        if (_tabDropRight == RightDocuments.Contains(_dragDocument)) _tabDropRight = null;
        if (previousTarget != _tabDropRight) ShowDocumentTabDropPreview();
        e.Handled = true;
    }

    private void ShowDocumentTabDropPreview()
    {
        if (_tabDropRight is not { } right)
        {
            if (_tabDropPreview is not null) _tabDropPreview.IsOpen = false;
            return;
        }
        var split = RightDocumentTabs.IsVisible;
        var target = right ? RightDocumentTabs : DocumentTabs;
        var offset = split ? target.TranslatePoint(new Point(), DocumentPanesGrid) : new Point(DocumentPanesGrid.ActualWidth / 2, 0);
        var width = split ? target.ActualWidth : DocumentPanesGrid.ActualWidth / 2;
        _tabDropPreview ??= new Popup { PlacementTarget = DocumentPanesGrid, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false };
        var label = new TextBlock { Text = split ? (right ? "오른쪽으로 이동" : "왼쪽으로 이동") : "오른쪽에 분할하여 놓기", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var border = new Border { Width = Math.Max(0, width), Height = DocumentPanesGrid.ActualHeight, BorderThickness = new Thickness(3), Opacity = .8, Child = label, IsHitTestVisible = false };
        border.SetResourceReference(Border.BackgroundProperty, "SelectionBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _tabDropPreview.Child = border;
        _tabDropPreview.HorizontalOffset = offset.X;
        _tabDropPreview.VerticalOffset = offset.Y;
        // A Popup stays above the native Scintilla host, unlike a WPF adorner.
        _tabDropPreview.IsOpen = true;
    }

    private void DocumentTabDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_tabDragging) { FinishDocumentTabDrag(false); return; }
        FinishDocumentTabDrag(true);
        e.Handled = true;
    }

    private void FinishDocumentTabDrag(bool apply)
    {
        var document = _dragDocument;
        var right = _tabDropRight;
        _tabDragging = false;
        _dragDocument = null;
        _tabDropRight = null;
        if (_tabDropPreview is not null) _tabDropPreview.IsOpen = false;
        if (Mouse.Captured == DocumentPanesGrid) Mouse.Capture(null);
        if (apply && document is not null && right.HasValue && Documents.Contains(document))
        {
            SetDocumentSplit(true);
            MoveToPane(document, right.Value);
            FocusCurrentDocumentAfterLayout();
        }
    }
}
