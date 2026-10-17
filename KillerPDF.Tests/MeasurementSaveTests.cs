using System.Globalization;
using System.IO;
using System.Windows;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>
    /// Measurements saved into a PDF and read back (PdfEngineIntegration), and the values and labels
    /// the overlay, burn and save share (MeasurementGeometry).
    /// </summary>
    public sealed class MeasurementSaveTests
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

        [Fact]
        public void MeasurementsRoundTripThroughTheFileCropRelative()
        {
            string path = TempPdf(new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build());
            try
            {
                var scale = new PdfMeasurementProfile("1 in = 10 ft", 10.0 / 72, "ft", 2);
                PdfEngineIntegration.WriteMeasurements(path,
                [
                    new PdfEngineIntegration.MeasurementData(0, PdfMeasurementKind.Distance,
                        [new PdfPoint(72, 72), new PdfPoint(144, 72)], false, scale, "10.00 ft",
                        new PdfRgbColor(1, 0, 0), 2, 1, new PdfPoint(108, 84), 10),
                    new PdfEngineIntegration.MeasurementData(1, PdfMeasurementKind.Area,
                        [new PdfPoint(0, 0), new PdfPoint(72, 0), new PdfPoint(72, 72)], true, scale,
                        "25.00 ft²", new PdfRgbColor(0, 0, 1), 1, 0.5, new PdfPoint(48, 24), 9),
                    new PdfEngineIntegration.MeasurementData(1, PdfMeasurementKind.Angle,
                        [new PdfPoint(100, 200), new PdfPoint(100, 100), new PdfPoint(200, 100)], false, null,
                        "90.0°", new PdfRgbColor(0, 0, 0), 1, 1, null, null),
                ]);

                List<PdfEngineIntegration.MeasurementData> read =
                    PdfEngineIntegration.ReadMeasurements(PdfDocument.Open(File.ReadAllBytes(path)));

                Assert.Equal(3, read.Count);
                Assert.Equal(PdfMeasurementKind.Distance, read[0].Kind);
                Assert.Equal(new PdfPoint(72, 72), read[0].Points[0]);
                Assert.Equal("1 in = 10 ft", read[0].Profile!.Name);
                Assert.Equal(new PdfPoint(108, 84), read[0].LabelCenter);
                Assert.Equal(10, read[0].LabelSize);
                Assert.Equal(PdfMeasurementKind.Area, read[1].Kind);
                Assert.Equal(0.5, read[1].Opacity);
                Assert.Equal(PdfMeasurementKind.Angle, read[2].Kind);

                // Saving again replaces the measurements rather than adding to them.
                PdfEngineIntegration.WriteMeasurements(path, [read[0]]);
                Assert.Single(PdfEngineIntegration.ReadMeasurements(PdfDocument.Open(File.ReadAllBytes(path))));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TheWorkingCopyShedsMeasurementsAndKeepsEverythingElse()
        {
            string path = TempPdf(new PdfDocumentBuilder().AddBlankPage().Build());
            string stripped = Path.Combine(Path.GetTempPath(), $"killerpdf-measure-{Guid.NewGuid():N}.pdf");
            try
            {
                PdfEngineIntegration.WriteMeasurements(path,
                [
                    new PdfEngineIntegration.MeasurementData(0, PdfMeasurementKind.Perimeter,
                        [new PdfPoint(10, 10), new PdfPoint(60, 10), new PdfPoint(60, 60)], true,
                        PdfEngineIntegration.ActualSizeProfile, "1.89 in", new PdfRgbColor(1, 0, 0), 1, 1, null, null),
                ]);
                Assert.Equal(1, PdfEngineIntegration.RemoveMeasurements(path, stripped));
                Assert.Empty(PdfEngineIntegration.ReadMeasurements(PdfDocument.Open(File.ReadAllBytes(stripped))));
                Assert.Single(PdfEngineIntegration.ReadMeasurements(PdfDocument.Open(File.ReadAllBytes(path))));
            }
            finally
            {
                File.Delete(path);
                if (File.Exists(stripped)) File.Delete(stripped);
            }
        }

        [Fact]
        public void PageScalesAreSavedPerPageAndRemovable()
        {
            string path = TempPdf(new PdfDocumentBuilder().AddBlankPage().AddBlankPage().AddBlankPage().Build());
            try
            {
                var scale = new PdfMeasurementProfile("1 cm = 1 m", 2.54 / 72, "m", 2);
                PdfEngineIntegration.SetPageScales(path, [0, 2], scale);
                PdfMeasurementProfileMap map = PdfEngineIntegration.ReadPageScales(PdfDocument.Open(File.ReadAllBytes(path)));
                Assert.Equal("1 cm = 1 m", map.Resolve(0).Name);
                Assert.Equal(2.54 / 72, map.Resolve(2).UnitsPerPoint, 9);
                Assert.Throws<KeyNotFoundException>(() => map.Resolve(1));

                PdfEngineIntegration.SetPageScales(path, [0], null);
                map = PdfEngineIntegration.ReadPageScales(PdfDocument.Open(File.ReadAllBytes(path)));
                Assert.Throws<KeyNotFoundException>(() => map.Resolve(0));
                Assert.Equal("1 cm = 1 m", map.Resolve(2).Name);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void GeometryGivesValuesInTheMeasurementsScale()
        {
            // One canvas unit is half a point; the scale is 10 ft per inch.
            var distance = Measure(MeasurementKind.Distance, new Point(0, 0), new Point(144, 0));
            Assert.Equal(10, MeasurementGeometry.Value(distance, 0.5), 9);
            Assert.Equal("10.00 ft", MeasurementGeometry.Label(distance, 0.5, English));

            var area = Measure(MeasurementKind.Area, new Point(0, 0), new Point(144, 0), new Point(144, 144), new Point(0, 144));
            Assert.Equal(100, MeasurementGeometry.Value(area, 0.5), 9);
            Assert.Equal("100.00 ft²", MeasurementGeometry.Label(area, 0.5, English));

            var open = Measure(MeasurementKind.Perimeter, new Point(0, 0), new Point(144, 0), new Point(144, 144));
            Assert.Equal(20, MeasurementGeometry.Value(open, 0.5), 9);
            open.Closed = true;
            Assert.Equal(20 + 10 * Math.Sqrt(2), MeasurementGeometry.Value(open, 0.5), 9);

            var angle = Measure(MeasurementKind.Angle, new Point(10, 0), new Point(0, 0), new Point(0, 10));
            Assert.Equal("90.0°", MeasurementGeometry.Label(angle, 0.5, English));
        }

        [Fact]
        public void LabelsSitAboveDistancesAndInsideAngles()
        {
            var distance = Measure(MeasurementKind.Distance, new Point(0, 100), new Point(200, 100));
            Point label = MeasurementGeometry.AutoLabelCenter(distance);
            Assert.Equal(100, label.X, 6);
            Assert.True(label.Y < 100);

            var angle = Measure(MeasurementKind.Angle, new Point(100, 0), new Point(0, 0), new Point(0, 100));
            Point inside = MeasurementGeometry.AutoLabelCenter(angle);
            Assert.True(inside.X > 0 && inside.Y > 0);

            // A six-character label must not cover the arc, whichever way the angle opens.
            foreach (var corner in new[] { (new Point(100, 0), new Point(0, 100)), (new Point(100, 0), new Point(100, 30)), (new Point(0, 100), new Point(30, 100)) })
            {
                var opened = Measure(MeasurementKind.Angle, corner.Item1, new Point(0, 0), corner.Item2);
                Point center = MeasurementGeometry.AutoLabelCenter(opened);
                Rect box = MeasurementGeometry.LabelBox(center, opened.LabelSize * 0.55 * 6, opened.LabelSize);
                double nearX = Math.Clamp(0, box.Left, box.Right), nearY = Math.Clamp(0, box.Top, box.Bottom);
                Assert.True(Math.Sqrt(nearX * nearX + nearY * nearY) > MeasurementGeometry.ArcRadius(opened));
            }
        }

        [Fact]
        public void DegenerateMeasurementsAreNotUsable()
        {
            Assert.False(MeasurementGeometry.IsUsable(Measure(MeasurementKind.Distance, new Point(5, 5), new Point(5, 5))));
            Assert.False(MeasurementGeometry.IsUsable(Measure(MeasurementKind.Area, new Point(0, 0), new Point(10, 0), new Point(20, 0))));
            Assert.True(MeasurementGeometry.IsUsable(Measure(MeasurementKind.Perimeter, new Point(0, 0), new Point(10, 0))));
        }

        private static MeasurementAnnotation Measure(MeasurementKind kind, params Point[] points) => new()
        {
            Kind = kind,
            Points = [.. points],
            Unit = "ft",
            UnitsPerPoint = 10.0 / 72,
            Precision = 2,
            ScaleName = "1 in = 10 ft",
            LabelSize = 20
        };

        private static string TempPdf(byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), $"killerpdf-measure-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
