using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF
{
    /// <summary>
    /// Lays its children out top to bottom in columns that run left to right, starting a new
    /// column whenever the next child would run past the bottom. A top or bottom sidebar strip
    /// is wide and short, so its tree panels (an outline, a comment list) use this as their
    /// ItemsPanel and scroll sideways instead of showing one narrow column and clipping the rest.
    ///
    /// EVEN COLUMNS: every column is as wide as the widest row in the tree, up to
    /// MaxColumnWidth, so the columns sit on one even pitch; when they run past the strip, it
    /// scrolls. A title longer than the cap is cut short by the HoverTicker around the row's
    /// header and scrolls into view while hovered. The outermost panel measures the width and
    /// the nested ones read it from there.
    ///
    /// NESTED TREES: a TreeViewItem's template puts its children in an Auto row, which hands
    /// them an unbounded height, so a branch's own panel cannot wrap from what it is given.
    /// Instead it takes the outer panel's column height less the header rows of the items it
    /// sits under, and the column width less their indents. That is exactly the room a branch
    /// has when it is too tall for the column it would start in, because the outer panel then
    /// moves it to the top of a fresh column. Its children wrap into sub-columns beside each
    /// other on the same pitch, each ending where an outer column ends, so the outer columns
    /// after the branch stay on the grid too.
    /// (From KillerUI/Controls/ColumnFlowPanel.cs.)
    /// </summary>
    internal sealed class ColumnFlowPanel : Panel
    {
        /// <summary>Space between the outermost columns.</summary>
        internal const double ColumnGap = 12;

        /// <summary>The widest a column grows, indent and expander included.</summary>
        internal const double MaxColumnWidth = 320;

        private double _columnHeight = double.PositiveInfinity;
        private double _rootColumnWidth;   // the outermost panel only: the widest row, indent included
        private double _columnWidth;       // this panel's columns: the root width less this panel's indent
        private double _indent;            // how far this panel sits in from the outer column's left edge

        // A nested panel's columns are narrower by its indent, so its gap is wider by the same
        // amount: every column then ends where the outer column on the same pitch ends.
        private double Gap => ColumnGap + _indent;

        /// <summary>How far this panel sits in from the outer column's left edge.</summary>
        internal double Indent => _indent;

        protected override Size MeasureOverride(Size availableSize)
        {
            bool root = !double.IsInfinity(availableSize.Height);
            if (root)
            {
                _columnHeight = availableSize.Height;
                _columnWidth = _rootColumnWidth;
                _indent = 0;
            }
            else
            {
                Inherit();
            }

            var childSize = new Size(double.PositiveInfinity, _columnHeight);
            foreach (UIElement child in InternalChildren) child.Measure(childSize);

            if (root)
            {
                // Row headers measure the same whatever the column width, so the widest one is
                // settled now. When it moves, the nested panels measure again with the new width
                // on the next layout pass, and their new sizes bring this panel back round.
                double widest = WidestRow();
                if (Math.Abs(widest - _rootColumnWidth) > 0.5)
                {
                    _rootColumnWidth = _columnWidth = widest;
                    InvalidateNestedPanels(this);
                }
            }

            double width = 0, height = 0;
            foreach (var column in Columns())
            {
                width += (width > 0 ? Gap : 0) + column.Width;
                height = Math.Max(height, column.Height);
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            foreach (var column in Columns())
            {
                double y = 0;
                foreach (UIElement child in column.Children)
                {
                    child.Arrange(new Rect(x, y, column.Width, child.DesiredSize.Height));
                    y += child.DesiredSize.Height;
                }
                x += column.Width + Gap;
            }
            return finalSize;
        }

        private sealed class Column
        {
            internal readonly List<UIElement> Children = new List<UIElement>();
            internal double Width, Height;
        }

        // Splits the measured children into columns no taller than the column height. A child
        // taller than a whole column still gets one to itself rather than being dropped. A
        // column is the shared width, or wider for a branch that wrapped into sub-columns.
        private List<Column> Columns()
        {
            var columns = new List<Column>();
            Column? current = null;
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                Size size = child.DesiredSize;
                if (current == null || (current.Children.Count > 0 && current.Height + size.Height > _columnHeight))
                {
                    current = new Column { Width = _columnWidth };
                    columns.Add(current);
                }
                current.Children.Add(child);
                current.Height += size.Height;
                current.Width = Math.Max(current.Width, size.Width);
            }
            return columns;
        }

        // Takes the nearest outer panel's column height less the header rows of the tree items
        // in between, and its column width less their indents. With no outer panel to ask, there is no
        // bottom, and the children stack in one column at their own widths.
        private void Inherit()
        {
            double headers = 0, indent = 0;
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is TreeViewItem item)
                {
                    headers += HeaderHeight(item);
                    indent += ChildIndent(item);
                }
                else if (node is ColumnFlowPanel outer && !double.IsInfinity(outer._columnHeight))
                {
                    // The nearest outer panel's own room is already counted from the root, so
                    // only the rows and indents between it and this panel come off.
                    _columnHeight = Math.Max(outer._columnHeight - headers, 0);
                    _columnWidth = Math.Max(outer._columnWidth - indent, 0);
                    _indent = outer._indent + indent;
                    return;
                }
            }
            _columnHeight = double.PositiveInfinity;
            _columnWidth = 0;
            _indent = 0;
        }

        // The widest row header in the tree, each counted from the outer column's left edge.
        // Rows inside a folded branch are collapsed and skipped.
        private double WidestRow()
        {
            double widest = 0;
            void Walk(DependencyObject node, double indent)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(node, i);
                    if (child is UIElement element && element.Visibility == Visibility.Collapsed) continue;
                    if (child is TreeViewItem item)
                    {
                        if (Header(item) is FrameworkElement header)
                            widest = Math.Max(widest, indent + header.DesiredSize.Width);
                        Walk(item, indent + ChildIndent(item));
                    }
                    else Walk(child, indent);
                }
            }
            Walk(this, 0);
            return Math.Min(widest, MaxColumnWidth);
        }

        private static void InvalidateNestedPanels(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is ColumnFlowPanel panel) panel.InvalidateMeasure();
                InvalidateNestedPanels(child);
            }
        }

        // The item's own row above its children: the header border in the family template, or
        // the header presenter in a template without one. It is measured before the children's
        // panel, so its size is current while that panel measures.
        private static FrameworkElement? Header(TreeViewItem item)
        {
            foreach (string name in new[] { "Bd", "PART_Header" })
                if (item.Template?.FindName(name, item) is FrameworkElement header)
                    return header;
            return null;
        }

        private static double HeaderHeight(TreeViewItem item) => Header(item)?.DesiredSize.Height ?? 0;

        // How far an item's children sit in from its own left edge: the template's columns
        // before the children's host (the expander column in the family template) and its margin.
        private static double ChildIndent(TreeViewItem item)
        {
            if (!(item.Template?.FindName("ItemsHost", item) is FrameworkElement host)) return 0;
            double x = host.Margin.Left;
            if (VisualTreeHelper.GetParent(host) is Grid grid)
                for (int c = 0; c < Grid.GetColumn(host) && c < grid.ColumnDefinitions.Count; c++)
                {
                    GridLength w = grid.ColumnDefinitions[c].Width;
                    x += w.IsAbsolute ? w.Value : grid.ColumnDefinitions[c].ActualWidth;
                }
            return x;
        }
    }
}
