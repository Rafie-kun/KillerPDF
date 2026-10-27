using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Xunit;
using KillerPDF.Shell;

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

        [Fact]
        public void FlippingPutsTheEdgeButtonsOutermostAtTheOtherEndAndBack() => RunSta(() =>
        {
            var rail = new StackPanel();
            Button Add(string name, double gapBelow)
            {
                var button = new Button { Name = name, Margin = new Thickness(0, 0, 0, gapBelow) };
                rail.Children.Add(button);
                return button;
            }
            Button a = Add("A", 6), b = Add("B", 14), help = Add("Help", 6), lang = Add("Lang", 6), theme = Add("Theme", 0);
            string[] Names() => rail.Children.OfType<FrameworkElement>().Select(e => e.Name).ToArray();
            var order = RailOrder.Attach(rail, new FrameworkElement[] { theme, lang, help },
                new FrameworkElement[] { a, b }, null, _ => { });

            order.SetFlipped(true);
            Assert.True(order.Flipped);
            Assert.Equal(new[] { "Theme", "Lang", "Help", "B", "A" }, Names());
            Assert.Equal(new Thickness(0, 14, 0, 0), b.Margin);   // still the gap between Help and B
            Assert.Equal(new Thickness(0), theme.Margin);

            order.SetFlipped(false);
            Assert.Equal(new[] { "A", "B", "Help", "Lang", "Theme" }, Names());
            Assert.Equal(new Thickness(0, 0, 0, 14), b.Margin);
        });

        [Fact]
        public void AHorizontalRailFlipsItsGapsSideways() => RunSta(() =>
        {
            var rail = new StackPanel { Orientation = Orientation.Horizontal };
            var a = new Button { Name = "A", Margin = new Thickness(0, 0, 6, 0) };
            var theme = new Button { Name = "Theme" };
            rail.Children.Add(a);
            rail.Children.Add(theme);
            var order = RailOrder.Attach(rail, new FrameworkElement[] { theme }, new FrameworkElement[] { a }, null, _ => { });

            order.SetFlipped(true);

            Assert.Same(theme, rail.Children[0]);
            Assert.Equal(new Thickness(6, 0, 0, 0), a.Margin);
        });

        private static void RunSta(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
