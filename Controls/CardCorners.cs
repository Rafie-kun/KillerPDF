using System;
using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>The document card's corners while its tab strip is showing, and the clip inside them.</summary>
    internal static class CardCorners
    {
        /// <summary>
        /// The card's corners with tabs showing. The two on the tab side take the tabs' own
        /// softening rather than the card's full radius; one with the active tab right over it
        /// stays square, where that tab runs straight down into the card's side. The other two keep
        /// the card radius. Square throughout on a flat theme, whose card radius is 0.
        /// </summary>
        internal static CornerRadius AtTabBand(double cardRadius, double tabRadius, bool firstActive, bool lastActive, bool bottom)
        {
            double t = Math.Min(tabRadius, cardRadius);
            double left = firstActive ? 0 : t, right = lastActive ? 0 : t;
            return bottom
                ? new CornerRadius(cardRadius, cardRadius, right, left)
                : new CornerRadius(left, right, cardRadius, cardRadius);
        }

        /// <summary>
        /// The card's content area, each corner rounded 1px less than the card's own, which is where
        /// the inner edge of the card's 1px border curve falls. A Border with a CornerRadius does not
        /// clip its child, so without this the canvas squares the card's corners straight back off.
        /// </summary>
        internal static Geometry ContentClip(Size size, CornerRadius card)
        {
            double tl = Inner(card.TopLeft), tr = Inner(card.TopRight), br = Inner(card.BottomRight), bl = Inner(card.BottomLeft);
            double w = size.Width, h = size.Height;
            var shape = new StreamGeometry();
            using (StreamGeometryContext c = shape.Open())
            {
                c.BeginFigure(new Point(tl, 0), isFilled: true, isClosed: true);
                c.LineTo(new Point(w - tr, 0), true, false);
                if (tr > 0) c.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(w, h - br), true, false);
                if (br > 0) c.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(bl, h), true, false);
                if (bl > 0) c.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(new Point(0, tl), true, false);
                if (tl > 0) c.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
            }
            shape.Freeze();
            return shape;

            double Inner(double radius) => Math.Min(Math.Max(0, radius - 1), Math.Min(size.Width, size.Height) / 2);
        }
    }
}
