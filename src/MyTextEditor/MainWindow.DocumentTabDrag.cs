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
    private int? _tabDropIndex;
    private Point? _tabDetachPosition;
    private Popup? _tabDropPreview;
    private MainWindow? _tabDropWindow;
    private bool _tabDropBlocked;
    private Task _pendingDocumentTransfer = Task.CompletedTask;

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
        if (_documentTransferRunning || _windowClosed) return;
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
        // A tab header is a valid left-pane target even when the split view is off.
        if (ResolveDocumentTabDropIndex(point, false) is not null) return false;
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
        var previousWindow = _tabDropWindow;
        var previousBlocked = _tabDropBlocked;
        var previousDetach = _tabDetachPosition.HasValue;
        var windowPoint = e.GetPosition(this);
        _tabDetachPosition = windowPoint.X < 0 || windowPoint.Y < 0 ||
            windowPoint.X > ActualWidth || windowPoint.Y > ActualHeight
            ? PointToScreen(windowPoint) : null;
        var previousTarget = _tabDropRight;
        var previousIndex = _tabDropIndex;
        _tabDropWindow = null;
        _tabDropBlocked = false;
        _tabDropRight = ResolveDocumentTabDrop(point);
        var screen = PointToScreen(windowPoint);
        var root = GetAncestor(WindowFromPoint(new NativePoint((int)screen.X, (int)screen.Y)), 2);
        var hovered = System.Windows.Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(window => new System.Windows.Interop.WindowInteropHelper(window).Handle == root);
        if (hovered is null && _tabDropPreview is { IsOpen: true, Child: { } preview } &&
            PresentationSource.FromVisual(preview) is System.Windows.Interop.HwndSource previewSource && previewSource.Handle == root)
            hovered = Window.GetWindow(_tabDropPreview.PlacementTarget);
        if (hovered is MainWindow other && other != this)
        {
            _tabDetachPosition = null;
            var targetPoint = other.DocumentPanesGrid.PointFromScreen(screen);
            var inside = targetPoint.X >= 0 && targetPoint.Y >= 0 &&
                targetPoint.X <= other.DocumentPanesGrid.ActualWidth && targetPoint.Y <= other.DocumentPanesGrid.ActualHeight;
            if (inside && other.CanReceiveDocument)
            {
                _tabDropWindow = other;
                _tabDropRight = other.RightDocumentTabs.IsVisible &&
                    screen.X >= other.RightDocumentTabs.PointToScreen(new Point()).X;
                _tabDropIndex = other.ResolveDocumentTabDropIndex(targetPoint, _tabDropRight.Value);
            }
            else _tabDropBlocked = true;
        }
        else if (hovered is not null && hovered != this)
        {
            _tabDetachPosition = null;
            _tabDropBlocked = true;
        }
        else if (hovered is null && root != IntPtr.Zero)
        {
            GetWindowThreadProcessId(root, out var processId);
            if (processId == Environment.ProcessId)
            {
                // Native modal dialogs are not listed in Application.Windows.
                _tabDetachPosition = null;
                _tabDropBlocked = true;
            }
        }
        if (_tabDropWindow is null) _tabDropIndex = _tabDropRight is { } right ? ResolveDocumentTabDropIndex(point, right) : null;
        if (_tabDropWindow is null && _tabDropRight == RightDocuments.Contains(_dragDocument) && _tabDropIndex is null) _tabDropRight = null;
        if (previousWindow != _tabDropWindow || previousBlocked != _tabDropBlocked || previousDetach != _tabDetachPosition.HasValue || previousTarget != _tabDropRight || previousIndex != _tabDropIndex) ShowDocumentTabDropPreview();
        e.Handled = true;
    }

    private void ShowDocumentTabDropPreview()
    {
        if (_tabDropBlocked)
        {
            if (_tabDropPreview is not null) _tabDropPreview.IsOpen = false;
            StatusMessage.Text = "이 위치에는 탭을 놓을 수 없습니다.";
            return;
        }
        if (_tabDetachPosition is not null)
        {
            StatusMessage.Text = "놓으면 새 OmniEdit 창으로 분리합니다. Esc로 취소합니다.";
            if (_tabDropPreview is not null) _tabDropPreview.IsOpen = false;
            return;
        }
        if (_tabDropRight is not { } right)
        {
            if (_tabDropPreview is not null) _tabDropPreview.IsOpen = false;
            return;
        }
        var owner = _tabDropWindow ?? this;
        var grid = owner.DocumentPanesGrid;
        _tabDropPreview ??= new Popup { Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, IsHitTestVisible = false };
        _tabDropPreview.PlacementTarget = grid;
        if (_tabDropIndex is { } insertion)
        {
            var tabs = right ? owner.RightDocumentTabs : owner.DocumentTabs;
            var items = tabs.Items.OfType<DocumentViewModel>().Where(item => item != _dragDocument).ToArray();
            if (items.Length == 0) return;
            var anchor = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(items[Math.Min(insertion, items.Length - 1)]);
            var position = anchor.TranslatePoint(new Point(insertion < items.Length ? 0 : anchor.ActualWidth, 0), grid);
            var marker = new Border { Width = 3, Height = anchor.ActualHeight, IsHitTestVisible = false };
            marker.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            _tabDropPreview.Child = marker;
            _tabDropPreview.HorizontalOffset = position.X;
            _tabDropPreview.VerticalOffset = position.Y;
            _tabDropPreview.IsOpen = true;
            return;
        }
        var split = owner.RightDocumentTabs.IsVisible;
        var target = right ? owner.RightDocumentTabs : owner.DocumentTabs;
        var crossWindow = _tabDropWindow is not null;
        var offset = split ? target.TranslatePoint(new Point(), grid) : new Point(crossWindow ? 0 : grid.ActualWidth / 2, 0);
        var width = split ? target.ActualWidth : grid.ActualWidth / (crossWindow ? 1 : 2);
        var label = new TextBlock { Text = crossWindow ? "이 창의 탭으로 이동" : split ? (right ? "오른쪽으로 이동" : "왼쪽으로 이동") : "오른쪽에 분할하여 놓기", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var border = new Border { Width = Math.Max(0, width), Height = grid.ActualHeight, BorderThickness = new Thickness(3), Opacity = .8, Child = label, IsHitTestVisible = false };
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
        var destination = _tabDropWindow;
        var blocked = _tabDropBlocked;
        var right = _tabDropRight;
        var index = _tabDropIndex;
        var detachPosition = _tabDetachPosition;
        _tabDragging = false;
        _dragDocument = null;
        _tabDropRight = null;
        _tabDropIndex = null;
        _tabDetachPosition = null;
        _tabDropWindow = null;
        _tabDropBlocked = false;
        if (_tabDropPreview is not null)
        {
            _tabDropPreview.IsOpen = false;
            _tabDropPreview.PlacementTarget = DocumentPanesGrid;
        }
        if (Mouse.Captured == DocumentPanesGrid) Mouse.Capture(null);
        if (blocked) return;
        if (apply && document is not null && (destination is not null || detachPosition is not null))
        {
            _pendingDocumentTransfer = CompleteDocumentTransferAsync(document, destination, right ?? false, index, detachPosition);
            return;
        }
        if (apply && document is not null && right.HasValue && Documents.Contains(document))
        {
            var sourceRight = RightDocuments.Contains(document);
            if (sourceRight == right.Value)
            {
                if (index is { } order) MoveDocumentOrder(document, right.Value, order);
            }
            else
            {
                SetDocumentSplit(true);
                MoveToPaneAt(document, right.Value, index);
            }
            FocusCurrentDocumentAfterLayout();
        }
    }

    private async Task CompleteDocumentTransferAsync(DocumentViewModel document, MainWindow? destination, bool right, int? index, Point? position)
    {
        if (!CanTransferDocument(document) || (destination is not null && !destination.CanReceiveDocument)) return;
        try
        {
            _documentTransferRunning = true;
            var ready = await EnsureRecoveryCheckpointAsync(document);
            _documentTransferRunning = false;
            if (!ready || !CanTransferDocument(document)) return;
            if (destination is not null) MoveDocumentToWindow(document, destination, right, index);
            else if (position is { } screen) DetachDocumentWindow(document, screen);
        }
        catch (Exception exception)
        {
            Services.DiagnosticLog.Error("prepare-document-transfer", exception);
            StatusMessage.Text = $"탭 이동을 중단했습니다. 문서를 유지합니다: {exception.Message}";
        }
        finally { _documentTransferRunning = false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    private int? ResolveDocumentTabDropIndex(Point point, bool right)
    {
        var target = right ? RightDocumentTabs : DocumentTabs;
        if (!target.IsVisible || target.Items.Count == 0) return null;
        var screen = DocumentPanesGrid.PointToScreen(point);
        var local = target.PointFromScreen(screen);
        if (local.Y < 0 || local.Y > target.ActualHeight) return null;

        if (local.X < 0 || local.X > target.ActualWidth) return null;
        var insertion = 0;
        int? rowEnd = null;
        foreach (var item in target.Items.OfType<DocumentViewModel>())
        {
            if (target.ItemContainerGenerator.ContainerFromItem(item) is not TabItem tab) continue;
            var tabPoint = tab.PointFromScreen(screen);
            var inRow = tabPoint.Y >= 0 && tabPoint.Y <= tab.ActualHeight;
            if (inRow && tabPoint.X >= 0 && tabPoint.X <= tab.ActualWidth && item == _dragDocument)
                return target.Items.IndexOf(item);
            if (item == _dragDocument) continue;
            if (inRow && tabPoint.X < tab.ActualWidth / 2) return insertion;
            insertion++;
            if (inRow) rowEnd = insertion;
        }
        return rowEnd;
    }
}
