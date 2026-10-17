using System.Globalization;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>The Measure tool's units, scales and value text (Services/MeasurementUnits.cs).</summary>
    public class MeasurementUnitsTests
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
        private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

        [Fact]
        public void ARatioBecomesRealUnitsPerPdfPoint()
        {
            // 1 inch of paper is 72 points and stands for 10 feet.
            Assert.Equal(10.0 / 72, MeasurementUnits.UnitsPerPoint(1, "in", 10, "ft"), 12);
            // 1 cm of paper (72 / 2.54 points) stands for 1 m.
            Assert.Equal(2.54 / 72, MeasurementUnits.UnitsPerPoint(1, "cm", 1, "m"), 12);
        }

        [Fact]
        public void ActualSizeIsOneInchPerSeventyTwoPoints()
        {
            Assert.Equal(1.0 / 72, MeasurementUnits.ActualSizeUnitsPerPoint("in"), 12);
            Assert.Equal(25.4 / 72, MeasurementUnits.ActualSizeUnitsPerPoint("mm"), 12);
        }

        [Fact]
        public void AScaleConvertsBetweenUnits()
        {
            double feet = MeasurementUnits.UnitsPerPoint(1, "in", 10, "ft");
            Assert.Equal(120.0 / 72, MeasurementUnits.ConvertScale(feet, "ft", "in"), 12);
            Assert.Equal(feet, MeasurementUnits.ConvertScale(feet, "ft", MeasurementUnits.FeetAndInches), 12);
        }

        [Fact]
        public void RatioTextUsesInvariantNumbersAndNamesFeetForFeetAndInches()
        {
            Assert.Equal("1 in = 10 ft", MeasurementUnits.RatioText(1, "in", 10, "ft"));
            Assert.Equal("0.25 in = 1 ft", MeasurementUnits.RatioText(0.25, "in", 1, MeasurementUnits.FeetAndInches));
            Assert.Equal("1 mm = 1 mm", MeasurementUnits.ActualSizeText("mm"));
        }

        [Fact]
        public void LengthsFollowTheReadersNumberFormat()
        {
            Assert.Equal("1,234.50 ft", MeasurementUnits.FormatLength(1234.5, "ft", 2, English));
            Assert.Equal("1.234,50 ft", MeasurementUnits.FormatLength(1234.5, "ft", 2, German));
            Assert.Equal("12.5 mm", MeasurementUnits.FormatLength(12.5, "mm", 1, English));
        }

        [Fact]
        public void FeetAndInchesReadLikeATape()
        {
            Assert.Equal("3' 4\"", MeasurementUnits.FormatLength(3 + 4 / 12.0, MeasurementUnits.FeetAndInches, 0, English));
            Assert.Equal("3' 4.5\"", MeasurementUnits.FormatLength(3 + 4.5 / 12, MeasurementUnits.FeetAndInches, 1, English));
            // 11.9 inches rounds to a whole foot instead of showing 12 inches.
            Assert.Equal("1' 0\"", MeasurementUnits.FormatLength(11.9 / 12, MeasurementUnits.FeetAndInches, 0, English));
        }

        [Fact]
        public void AreasUseSquaredUnitsAndAnglesUseDegrees()
        {
            Assert.Equal("12.00 m²", MeasurementUnits.FormatArea(12, "m", 2, English));
            Assert.Equal("40.0 ft²", MeasurementUnits.FormatArea(40, MeasurementUnits.FeetAndInches, 1, English));
            Assert.Equal("45.0°", MeasurementUnits.FormatAngle(45, English));
        }

        [Fact]
        public void NarrowNoBreakSpacesBecomeNoBreakSpaces()
        {
            var french = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            french.NumberFormat.NumberGroupSeparator = " ";
            Assert.DoesNotContain(' ', MeasurementUnits.FormatLength(12345.6, "m", 1, french));
        }

        [Fact]
        public void TypedLengthsAcceptTheReadersFormatAndRejectZero()
        {
            Assert.True(MeasurementUnits.TryParseLength("2,5", German, out double german));
            Assert.Equal(2.5, german);
            Assert.True(MeasurementUnits.TryParseLength("2.5", English, out double english));
            Assert.Equal(2.5, english);
            Assert.False(MeasurementUnits.TryParseLength("0", English, out _));
            Assert.False(MeasurementUnits.TryParseLength("abc", English, out _));
        }

        [Fact]
        public void AUnitFromAnotherFileKeepsItsOwnSymbol()
        {
            Assert.Equal("12.00 rods", MeasurementUnits.FormatLength(12, "rods", 2, English));
            Assert.False(MeasurementUnits.CanConvert("rods"));
            Assert.True(MeasurementUnits.CanConvert("pt"));
            Assert.DoesNotContain(MeasurementUnits.Offered, unit => unit.Id == "pt");
        }

        [Fact]
        public void UnknownUnitsFallBackToInches()
        {
            Assert.Equal("in", MeasurementUnits.Find("furlong").Id);
            Assert.False(MeasurementUnits.TryFind("furlong", out _));
            Assert.Equal("mm", MeasurementUnits.DefaultUnitId(metric: true));
            Assert.Equal("in", MeasurementUnits.DefaultUnitId(metric: false));
        }
    }
}
