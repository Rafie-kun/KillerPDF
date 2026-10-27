using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Wraps a tree row's header so that, inside a ColumnFlowPanel, a title too long for its
    /// column is cut short with an ellipsis, and scrolls across to show the rest while the row
    /// is hovered (it pauses at each end and scrolls back). Anywhere else it does nothing, so
    /// the same row template serves a sidebar column and a top or bottom strip.
    ///
    /// The width it gets is the panel's column cap, less the panel's indent and whatever sits
    /// between this host and the row's left edge (the expander column, the row border).
    /// Text in a TextBlock is trimmed with an ellipsis; any other content is clipped.
    /// </summary>
    internal sealed class HoverTicker : Decorator
    {
        /// <summary>How fast the title scrolls, in pixels a second.</summary>
        private const double PixelsPerSecond = 45;

        /// <summary>How long it rests at each end before scrolling on.</summary>
        private const double PauseSeconds = 0.9;

        /// <summary>The element whose hover starts the scroll: the whole row, not just its text.</summary>
        public static readonly DependencyProperty HoverSourceProperty = DependencyProperty.Register(
            nameof(HoverSource), typeof(UIElement), typeof(HoverTicker),
            new PropertyMetadata(null, (d, e) => ((HoverTicker)d).OnHoverSourceChanged(
                e.OldValue as UIElement, e.NewValue as UIElement)));

        public UIElement? HoverSource
        {
            get => (UIElement?)GetValue(HoverSourceProperty);
            set => SetValue(HoverSourceProperty, value);
        }

        private readonly TranslateTransform _shift = new TranslateTransform();
        private double _natural;     // the content's full width
        private bool _limited;       // inside a ColumnFlowPanel, so trimmed to the column
        private bool _scrolling;     // hovered and too long: showing the whole title, moving

        protected override Size MeasureOverride(Size constraint)
        {
            UIElement? child = Child;
            if (child == null) return new Size();
            double limit = Limit();
            _limited = !double.IsInfinity(limit);
            if (!_limited)
            {
                StopScrolling();
                SetTrimming(child, false);
                child.Measure(constraint);
                return child.DesiredSize;
            }

            child.Measure(new Size(double.PositiveInfinity, constraint.Height));
            _natural = child.DesiredSize.Width;
            // After the first measure, so a presenter has made its TextBlock by now.
            SetTrimming(child, true);
            double width = Math.Min(Math.Min(limit, constraint.Width), _natural);
            // While scrolling the content keeps its full width and slides under the clip;
            // otherwise it measures to the cut-down width, which is what trims the text.
            if (!_scrolling) child.Measure(new Size(width, constraint.Height));
            return new Size(width, child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            UIElement? child = Child;
            if (child == null) return arrangeSize;
            double width = _scrolling ? Math.Max(_natural, arrangeSize.Width) : arrangeSize.Width;
            child.Arrange(new Rect(0, 0, width, arrangeSize.Height));
            child.RenderTransform = _shift;
            ClipToBounds = _limited;
            return arrangeSize;
        }

        private void OnHoverSourceChanged(UIElement? oldSource, UIElement? newSource)
        {
            if (oldSource != null)
            {
                oldSource.MouseEnter -= HoverStarted;
                oldSource.MouseLeave -= HoverEnded;
            }
            if (newSource != null)
            {
                newSource.MouseEnter += HoverStarted;
                newSource.MouseLeave += HoverEnded;
            }
        }

        private void HoverStarted(object sender, MouseEventArgs e)
        {
            double overflow = _natural - ActualWidth;
            if (!_limited || overflow < 1) return;
            _scrolling = true;
            InvalidateMeasure();
            double travel = overflow / PixelsPerSecond;
            var scroll = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            scroll.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scroll.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds))));
            scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds + travel))));
            scroll.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 * PauseSeconds + travel))));
            scroll.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 * PauseSeconds + 2 * travel))));
            _shift.BeginAnimation(TranslateTransform.XProperty, scroll);
        }

        private void HoverEnded(object sender, MouseEventArgs e) => StopScrolling();

        private void StopScrolling()
        {
            if (!_scrolling) return;
            _scrolling = false;
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = 0;
            InvalidateMeasure();
        }

        // The column cap less the panel's indent and everything between this host and the left
        // edge of its row. Not in a ColumnFlowPanel: no limit.
        private double Limit()
        {
            double inset = 0;
            DependencyObject child = this;
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (child is FrameworkElement element) inset += element.Margin.Left + element.Margin.Right;
                if (node is TreeViewItem item)
                {
                    return VisualTreeHelper.GetParent(item) is ColumnFlowPanel panel
                        ? Math.Max(0, ColumnFlowPanel.MaxColumnWidth - panel.Indent - inset)
                        : double.PositiveInfinity;
                }
                if (node is Grid grid && child is UIElement cell)
                    for (int c = 0; c < Grid.GetColumn(cell) && c < grid.ColumnDefinitions.Count; c++)
                    {
                        GridLength w = grid.ColumnDefinitions[c].Width;
                        inset += w.IsAbsolute ? w.Value : grid.ColumnDefinitions[c].ActualWidth;
                    }
                else if (node is Border border)
                    inset += border.BorderThickness.Left + border.BorderThickness.Right
                           + border.Padding.Left + border.Padding.Right;
                child = node;
            }
            return double.PositiveInfinity;
        }

        // Marks the TextBlocks this host trimmed, so only those go back when it stops limiting.
        private static readonly DependencyProperty TrimmedHereProperty = DependencyProperty.RegisterAttached(
            "TrimmedHere", typeof(bool), typeof(HoverTicker), new PropertyMetadata(false));

        // Text is cut with an ellipsis only while limited. A TextBlock that already trims its
        // own way is left alone.
        private static void SetTrimming(DependencyObject node, bool trim)
        {
            if (node is TextBlock text)
            {
                if (trim && text.TextTrimming == TextTrimming.None)
                {
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    text.SetValue(TrimmedHereProperty, true);
                }
                else if (!trim && (bool)text.GetValue(TrimmedHereProperty))
                {
                    text.TextTrimming = TextTrimming.None;
                    text.ClearValue(TrimmedHereProperty);
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                SetTrimming(VisualTreeHelper.GetChild(node, i), trim);
        }
    }
}
