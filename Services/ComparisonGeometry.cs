using System;

namespace KillerPDF.Services
{
    /// <summary>
    /// Whether two pages have the same physical size as displayed. Visual comparison renders each
    /// page to the same 1600-pixel long edge, so the raster sizes alone cannot tell a 100-point
    /// page from a 200-point one (BF-U008).
    /// </summary>
    internal static class ComparisonGeometry
    {
        /// <summary>Half a point: far below any real page-size change, above rounding noise.</summary>
        internal const double TolerancePoints = 0.5;

        internal static bool SameDisplayedSize(double leftWidth, double leftHeight, int leftRotation,
            double rightWidth, double rightHeight, int rightRotation)
        {
            (double lw, double lh) = Displayed(leftWidth, leftHeight, leftRotation);
            (double rw, double rh) = Displayed(rightWidth, rightHeight, rightRotation);
            return Math.Abs(lw - rw) <= TolerancePoints && Math.Abs(lh - rh) <= TolerancePoints;
        }

        private static (double Width, double Height) Displayed(double width, double height, int rotation) =>
            ((rotation % 360 + 360) % 360) is 90 or 270 ? (height, width) : (width, height);
    }
}
