using System.Windows;
using Xunit;

namespace KillerPDF.Tests
{
    public class FlyoutPlacementTests
    {
        [Theory]
        [InlineData(false, 100, -14, -10)]
        [InlineData(false, 350, -14, 124)]
        [InlineData(false, 680, -14, 258)]
        [InlineData(true, 100, 970, -10)]
        [InlineData(true, 350, 970, 124)]
        [InlineData(true, 680, 970, 258)]
        public void ButtonHeightMovesCardWithinPaneWithoutMovingHorizontalInset(
            bool alignRight, double centerY, double x, double y)
        {
            var placement = FlyoutPlacement.AtButtonHeight(
                new Size(244, 460), new Size(1200, 700), alignRight, centerY,
                new Thickness(22, 18, 22, 26))[0];

            Assert.Equal(new Point(x, y), placement.Point);
        }

        [Fact]
        public void ButtonHeightCompensatesActualShadowMarginAndPinsOversizedCard()
        {
            var placement = FlyoutPlacement.AtButtonHeight(
                new Size(244, 800), new Size(1200, 700), false, 680,
                new Thickness(30, 24, 30, 32))[0];

            Assert.Equal(new Point(-22, -16), placement.Point);
        }

        [Theory]
        // A bottom rail: the card's visible bottom sits 8 above the pane's bottom edge.
        [InlineData(true, 1100, 970, 258)]
        // A top rail: the card's visible top sits 8 below the pane's top edge.
        [InlineData(false, 1100, 970, -10)]
        // Centered on the button when there is room, kept 8 inside the left edge when not.
        [InlineData(true, 600, 478, 258)]
        [InlineData(false, 40, -14, -10)]
        public void StripRailCentersCardOnButtonAgainstRailEdge(
            bool alignBottom, double centerX, double x, double y)
        {
            var placement = FlyoutPlacement.AtButtonColumn(
                new Size(244, 460), new Size(1200, 700), alignBottom, centerX,
                new Thickness(22, 18, 22, 26))[0];

            Assert.Equal(new Point(x, y), placement.Point);
        }

        [Fact]
        public void BottomRailPinsOversizedCardBelowToolbar()
        {
            var placement = FlyoutPlacement.AtButtonColumn(
                new Size(244, 800), new Size(1200, 700), true, 600,
                new Thickness(22, 18, 22, 26))[0];

            Assert.Equal(0, placement.Point.Y);
        }

        [Fact]
        public void LeftRailUsesBottomLeftContentCorner()
        {
            var placement = FlyoutPlacement.PaneCorner(
                new Size(244, 460), new Size(1200, 700), alignRight: false)[0];

            Assert.Equal(new Point(-14, 258), placement.Point);
        }

        [Fact]
        public void RightRailMirrorsFlyoutToBottomRightContentCorner()
        {
            var placement = FlyoutPlacement.PaneCorner(
                new Size(244, 460), new Size(1200, 700), alignRight: true)[0];

            Assert.Equal(new Point(970, 258), placement.Point);
        }

        [Fact]
        public void TallFlyoutStaysBelowToolbarOnEitherSide()
        {
            var left = FlyoutPlacement.PaneCorner(
                new Size(244, 800), new Size(1200, 700), alignRight: false)[0];
            var right = FlyoutPlacement.PaneCorner(
                new Size(244, 800), new Size(1200, 700), alignRight: true)[0];

            Assert.Equal(0, left.Point.Y);
            Assert.Equal(0, right.Point.Y);
        }
    }
}
