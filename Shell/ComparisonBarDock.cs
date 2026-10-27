using System.Windows;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Shell
{
    // The comparison bar docks below the documents by default (#427). Dragging it anywhere but
    // its buttons moves it with the pointer, and releasing it in the upper half of the documents
    // docks it above them instead. The choice is remembered for the next comparison.
    public partial class MainWindow
    {
        private bool _comparisonBarDockWired;
        private bool _comparisonBarDragging;
        private double _comparisonBarDragStartY;
        private double _comparisonBarDragTop;

        private void WireComparisonBarDock()
        {
            if (_comparisonBarDockWired) return;
            _comparisonBarDockWired = true;
            // The buttons keep their own hand cursor and handle their own clicks, so only the
            // bar's open space starts a drag.
            ComparisonBar.Cursor = DragCursors.Open;
            ComparisonBar.MouseLeftButtonDown += ComparisonBar_MouseLeftButtonDown;
            ComparisonBar.MouseMove += ComparisonBar_MouseMove;
            ComparisonBar.MouseLeftButtonUp += ComparisonBar_MouseLeftButtonUp;
            ComparisonBar.LostMouseCapture += (_, _) => EndComparisonBarDrag();
            ApplyComparisonBarDock(App.GetSetting("ComparisonBarDock") == "Top");
        }

        private void ComparisonBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ComparisonDetailsPopup.IsOpen = false;
            _comparisonBarDragging = true;
            _comparisonBarDragStartY = e.GetPosition(SplitHost).Y;
            _comparisonBarDragTop = ComparisonBar.TranslatePoint(new Point(0, 0), SplitHost).Y;
            ComparisonBar.CaptureMouse();
            DragCursors.BeginDrag();
            e.Handled = true;
        }

        private void ComparisonBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_comparisonBarDragging) return;
            double offset = e.GetPosition(SplitHost).Y - _comparisonBarDragStartY;
            double highest = -_comparisonBarDragTop;
            double lowest = SplitHost.ActualHeight - _comparisonBarDragTop - ComparisonBar.ActualHeight;
            ComparisonBar.RenderTransform = new TranslateTransform(0, Math.Clamp(offset, highest, Math.Max(highest, lowest)));
        }

        private void ComparisonBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_comparisonBarDragging) return;
            bool top = e.GetPosition(SplitHost).Y < SplitHost.ActualHeight / 2;
            EndComparisonBarDrag();
            ApplyComparisonBarDock(top);
            App.SetSetting("ComparisonBarDock", top ? "Top" : "Bottom");
            e.Handled = true;
        }

        private void EndComparisonBarDrag()
        {
            if (!_comparisonBarDragging) return;
            _comparisonBarDragging = false;
            if (ComparisonBar.IsMouseCaptured) ComparisonBar.ReleaseMouseCapture();
            DragCursors.EndDrag();
            ComparisonBar.RenderTransform = Transform.Identity;
        }

        // Swaps the split host's two rows so the bar's row sits above or below the documents.
        // The details flyout opens away from the edge the bar is docked to.
        private void ApplyComparisonBarDock(bool top)
        {
            var rows = SplitHost.RowDefinitions;
            if (rows.Count != 2) return;
            rows[0].Height = top ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            rows[1].Height = top ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            foreach (UIElement child in SplitHost.Children)
            {
                if (ReferenceEquals(child, ComparisonBar)) Grid.SetRow(child, top ? 0 : 1);
                else if (Grid.GetRowSpan(child) == 1) Grid.SetRow(child, top ? 1 : 0);
            }
            ComparisonDetailsPopup.Placement = top ? PlacementMode.Bottom : PlacementMode.Top;
        }
    }
}
