using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class ComparisonGeometryTests
    {
        [Theory]
        [InlineData(100, 100, 0, 200, 200, 0, false)]    // same shape, different physical size
        [InlineData(612, 792, 0, 612, 792, 0, true)]
        [InlineData(612, 792, 0, 612.3, 792, 0, true)]   // within the half-point tolerance
        [InlineData(612, 792, 0, 613, 792, 0, false)]
        [InlineData(612, 792, 90, 792, 612, 0, true)]    // rotated page displays the same size
        [InlineData(612, 792, 90, 612, 792, 0, false)]
        [InlineData(612, 792, -270, 792, 612, 0, true)]
        public void ComparesThePhysicalDisplayedSize(double lw, double lh, int lr, double rw, double rh, int rr, bool same)
        {
            Assert.Equal(same, ComparisonGeometry.SameDisplayedSize(lw, lh, lr, rw, rh, rr));
        }
    }
}
