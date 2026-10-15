using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF
{
    // Drag and drop in the Outlines panel (#391). Dragging a bookmark moves it with its children:
    // the top or bottom edge of a row drops it above or below that bookmark, the middle of a row
    // drops it inside as the last child, and the bottom edge of an open branch drops it in as the
    // first child. A line or an outline marks the spot, and each move is one undo step.
    public partial class MainWindow
    {
        private bool _outlineDragWired;
        private OutlineNodeRef? _outlineDragArmed;
        private Point _outlineDragStart;
        private OutlineDropMarker? _outlineDropMarker;

        private readonly record struct OutlineDrop(TreeViewItem Item, OutlineNodeRef Target, BookmarkDropPlace Place);

        private void WireOutlineDrag()
        {
            if (_outlineDragWired) return;
            _outlineDragWired = true;
            OutlineTree.AllowDrop = true;
            // Added after the XAML handler, which marks Ctrl and Shift clicks handled, so a
            // multi-selection click never arms a drag.
            OutlineTree.PreviewMouseLeftButtonDown += OutlineDrag_PreviewMouseLeftButtonDown;
            OutlineTree.PreviewMouseMove += OutlineDrag_PreviewMouseMove;
            OutlineTree.PreviewMouseLeftButtonUp += (_, _) => _outlineDragArmed = null;
            OutlineTree.DragOver += OutlineTree_DragOver;
            OutlineTree.DragLeave += (_, _) => HideOutlineDropMarker();
            OutlineTree.Drop += OutlineTree_Drop;
        }

        private void OutlineDrag_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _outlineDragArmed = null;
            if (!CanEditBookmarks || Keyboard.Modifiers != ModifierKeys.None) return;
            var source = e.OriginalSource as DependencyObject;
            if (IsExpanderClick(source)) return;
            for (var d = source; d is not null && d is not TreeViewItem; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
                if (d is TextBox) return;   // inline rename in progress
            if (OutlineItemAt(source)?.Tag is OutlineNodeRef row)
            {
                _outlineDragArmed = row;
                _outlineDragStart = e.GetPosition(OutlineTree);
            }
        }

        private void OutlineDrag_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_outlineDragArmed is not OutlineNodeRef moving || e.LeftButton != MouseButtonState.Pressed) return;
            Vector moved = e.GetPosition(OutlineTree) - _outlineDragStart;
            if (Math.Abs(moved.X) <= SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
            _outlineDragArmed = null;
            ClearBookmarkMultiSelection();
            try
            {
                DragDrop.DoDragDrop(OutlineTree, new DataObject(typeof(OutlineNodeRef), moving), DragDropEffects.Move);
            }
            finally
            {
                HideOutlineDropMarker();
            }
        }

        private void OutlineTree_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            ScrollOutlineWhileDragging(e.GetPosition(OutlineScrollViewer));
            if (e.Data.GetData(typeof(OutlineNodeRef)) is OutlineNodeRef moving
                && OutlineDropAt(e.GetPosition(OutlineTree), moving) is { } drop)
            {
                e.Effects = DragDropEffects.Move;
                ShowOutlineDropMarker(drop);
            }
            else HideOutlineDropMarker();
            e.Handled = true;
        }

        private void OutlineTree_Drop(object sender, DragEventArgs e)
        {
            HideOutlineDropMarker();
            if (e.Data.GetData(typeof(OutlineNodeRef)) is not OutlineNodeRef moving
                || OutlineDropAt(e.GetPosition(OutlineTree), moving) is not { } drop) return;
            e.Handled = true;
            var identity = moving.Identity;
            var target = drop.Target.Identity;
            var place = drop.Place;
            string title = moving.Bookmark.Title;
            int page = moving.PageIndex;
            // Rebuilding the tree inside the drag loop would pull the rows out from under it,
            // so the move runs once the drop has finished.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
            {
                if (!CanEditBookmarks) return;
                ApplyEngineBookmarkEdit(items => BookmarkTreeEdit.Move(items, identity, target, place));
                SelectMovedBookmark(identity, title, page);
            }));
        }

        // Selects the bookmark where it landed without jumping the view to its page. Engine
        // identities usually survive the rewrite; the title and page find it when they do not.
        private void SelectMovedBookmark((int ObjectNumber, int Generation) identity, string title, int page)
        {
            TreeViewItem? item = FindOutlineItem(OutlineTree.Items, identity);
            if (item is null)
            {
                var rows = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
                FlattenBookmarkItems(OutlineTree.Items, visibleOnly: false, rows);
                item = rows.FirstOrDefault(row => row.Ref.Bookmark.Title == title && row.Ref.PageIndex == page).Item;
                for (var parent = item is null ? null : ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem;
                     parent is not null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem)
                    parent.IsExpanded = true;
            }
            if (item is null) return;
            _suppressOutlineNav = true;
            try { item.IsSelected = true; }
            finally { _suppressOutlineNav = false; }
            item.BringIntoView();
        }

        // The drop the pointer describes, or null where nothing may land: the add row, empty
        // space, the dragged bookmark itself, or anywhere inside its own branch.
        private OutlineDrop? OutlineDropAt(Point position, OutlineNodeRef moving)
        {
            if (OutlineItemAt(OutlineTree.InputHitTest(position) as DependencyObject) is not TreeViewItem item
                || item.Tag is not OutlineNodeRef target) return null;
            for (TreeViewItem? row = item; row is not null; row = ItemsControl.ItemsControlFromItemContainer(row) as TreeViewItem)
                if (row.Tag is OutlineNodeRef node && node.Identity == moving.Identity) return null;

            FrameworkElement header = OutlineRowHeader(item);
            double y = OutlineTree.TranslatePoint(position, header).Y;
            double height = Math.Max(1, header.ActualHeight);
            BookmarkDropPlace place = y < height * 0.3 ? BookmarkDropPlace.Before
                : y > height * 0.7 ? BookmarkDropPlace.After
                : BookmarkDropPlace.Inside;
            // Below an open branch's row the next row is its first child, so that is where it lands.
            if (place == BookmarkDropPlace.After && item.IsExpanded && item.Items.Count > 0)
                place = BookmarkDropPlace.InsideFirst;
            return new OutlineDrop(item, target, place);
        }

        // The row's own header border, without its children: the template's "Bd" part.
        private static FrameworkElement OutlineRowHeader(TreeViewItem item)
        {
            item.ApplyTemplate();
            return item.Template?.FindName("Bd", item) as FrameworkElement ?? item;
        }

        private void ShowOutlineDropMarker(OutlineDrop drop)
        {
            if (_outlineDropMarker is null)
            {
                if (AdornerLayer.GetAdornerLayer(OutlineTree) is not AdornerLayer layer) return;
                _outlineDropMarker = new OutlineDropMarker(OutlineTree);
                layer.Add(_outlineDropMarker);
            }
            FrameworkElement header = OutlineRowHeader(drop.Item);
            Point topLeft = header.TranslatePoint(new Point(0, 0), OutlineTree);
            double right = OutlineTree.ActualWidth - 4;
            Rect row = new(topLeft.X, topLeft.Y, Math.Max(8, right - topLeft.X), header.ActualHeight);
            const double ChildIndent = 19;
            (Rect at, bool box) = drop.Place switch
            {
                BookmarkDropPlace.Before => (new Rect(row.Left, row.Top, row.Width, 0), false),
                BookmarkDropPlace.After => (new Rect(row.Left,
                    drop.Item.TranslatePoint(new Point(0, drop.Item.ActualHeight), OutlineTree).Y, row.Width, 0), false),
                BookmarkDropPlace.InsideFirst => (new Rect(row.Left + ChildIndent, row.Bottom,
                    Math.Max(8, row.Width - ChildIndent), 0), false),
                _ => (row, true)
            };
            _outlineDropMarker.Show(at, box, TryFindResource("PrimaryBrush") as Brush ?? Brushes.SteelBlue);
        }

        private void HideOutlineDropMarker() => _outlineDropMarker?.Hide();

        // Scrolls the Outlines list when a drag nears its top or bottom edge.
        private void ScrollOutlineWhileDragging(Point pointer)
        {
            double edge = Math.Min(40, OutlineScrollViewer.ActualHeight / 4);
            if (pointer.Y < edge)
                OutlineScrollViewer.ScrollToVerticalOffset(OutlineScrollViewer.VerticalOffset - (edge - pointer.Y) / 2);
            else if (pointer.Y > OutlineScrollViewer.ActualHeight - edge)
                OutlineScrollViewer.ScrollToVerticalOffset(OutlineScrollViewer.VerticalOffset
                    + (pointer.Y - (OutlineScrollViewer.ActualHeight - edge)) / 2);
        }

        // Draws the drop spot over the tree: a line with a dot for between rows, or an outline
        // around the row that will become the parent.
        private sealed class OutlineDropMarker : Adorner
        {
            private Rect _at;
            private bool _box;
            private Brush _brush = Brushes.SteelBlue;

            internal OutlineDropMarker(UIElement tree) : base(tree)
            {
                IsHitTestVisible = false;
                Visibility = Visibility.Collapsed;
            }

            internal void Show(Rect at, bool box, Brush brush)
            {
                _at = at;
                _box = box;
                _brush = brush;
                Visibility = Visibility.Visible;
                InvalidateVisual();
            }

            internal void Hide() => Visibility = Visibility.Collapsed;

            protected override void OnRender(DrawingContext dc)
            {
                if (_box)
                {
                    dc.DrawRoundedRectangle(null, new Pen(_brush, 1.5), _at, 3, 3);
                    return;
                }
                dc.DrawRectangle(_brush, null, new Rect(_at.Left, _at.Top - 1, _at.Width, 2));
                dc.DrawEllipse(_brush, null, new Point(_at.Left, _at.Top), 3, 3);
            }
        }
    }
}
