using System;
using Xunit;

namespace KillerPDF.Tests
{
    public class RailOrderTests
    {
        private static readonly string[] Edge = { "Theme", "Lang", "Help", "Settings" };

        [Fact]
        public void TheEdgeButtonsEndTheRailWithThemeOutermost()
            => Assert.Equal(new[] { "A", "B", "C", "Settings", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A", "B", "C" }, Edge, null));

        [Fact]
        public void AnAppWithoutASettingsButtonLeadsWithThemeLocaleAndHelp()
            => Assert.Equal(new[] { "A", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A" }, new[] { "Theme", "Lang", "Help" }, null));

        [Fact]
        public void ASavedOrderRearrangesOnlyTheMovableButtons()
            => Assert.Equal(new[] { "C", "A", "B", "Settings", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A", "B", "C" }, Edge, "C,A,B"));

        [Fact]
        public void SavedEdgeButtonsAndUnknownNamesAreIgnored()
            => Assert.Equal(new[] { "B", "A", "Settings", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A", "B" }, Edge, "Theme,Gone,B,Help,A,B"));

        [Fact]
        public void AButtonMissingFromTheSavedOrderKeepsItsDefaultPlace()
            => Assert.Equal(new[] { "C", "B", "A", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A", "B", "C" }, new[] { "Theme", "Lang", "Help" }, "C,A"));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" , ,")]
        public void AnEmptySavedOrderIsTheDefault(string? saved)
            => Assert.Equal(new[] { "A", "B", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A", "B" }, new[] { "Theme", "Lang", "Help" }, saved));

        [Fact]
        public void TheSavedTextRoundTrips()
        {
            string saved = RailOrder.Serialize(new[] { "B", "C", "A" });
            Assert.Equal(new[] { "B", "C", "A", "Help", "Lang", "Theme" },
                RailOrder.Arrange(new[] { "A", "B", "C" }, new[] { "Theme", "Lang", "Help" }, saved));
        }

        [Theory]
        [InlineData(-50, 0)]
        [InlineData(10, 0)]
        [InlineData(23, 0)]
        [InlineData(25, 1)]
        [InlineData(60, 2)]
        [InlineData(500, 2)]
        public void ADraggedButtonDropsIntoTheNearestSlot(double pointer, int slot)
            => Assert.Equal(slot, RailOrder.NearestSlot(new[] { 12.0, 36.0, 60.0 }, pointer));

        [Fact]
        public void ThereIsNoSlotOnAnEmptyRail()
            => Assert.Equal(-1, RailOrder.NearestSlot(Array.Empty<double>(), 10));
    }
}
