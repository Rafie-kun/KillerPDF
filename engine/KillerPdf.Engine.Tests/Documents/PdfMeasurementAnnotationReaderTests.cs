using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMeasurementAnnotationReaderTests
{
    [Fact]
    public void DistanceRoundTripsWithItsScaleStyleAndCaption()
    {
        var profile = new PdfMeasurementProfile("1 in = 10 ft", 10.0 / 72, "ft", 3);
        byte[] saved = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddTextNote(0, 10, 20, "Not a measurement")
            .AddDistanceMeasurement(0, new PdfPoint(100, 100), new PdfPoint(244, 100), profile,
                new PdfRgbColor(1, 0, 0), lineWidth: 2, opacity: 0.5, contents: "20.000 ft",
                caption: new PdfMeasurementCaption(new PdfPoint(172, 112), 9))
            .Build();
        PdfDocument document = PdfDocument.Open(saved);

        PdfMeasurementAnnotationInfo info = Assert.Single(PdfMeasurementAnnotationReader.Read(document));

        Assert.Equal(0, info.PageIndex);
        Assert.Equal(1, info.AnnotationIndex);
        Assert.Equal(PdfMeasurementKind.Distance, info.Kind);
        Assert.Equal([new PdfMeasurementPoint(100, 100), new PdfMeasurementPoint(244, 100)], info.Points);
        Assert.Equal("1 in = 10 ft", info.Profile!.Name);
        Assert.Equal(10.0 / 72, info.Profile.UnitsPerPoint, 9);
        Assert.Equal("ft", info.Profile.UnitSymbol);
        Assert.Equal(3, info.Profile.Precision);
        Assert.Equal("20.000 ft", info.Contents);
        Assert.Equal(new PdfRgbColor(1, 0, 0), info.Color);
        Assert.Equal(2, info.LineWidth);
        Assert.Equal(0.5, info.Opacity);
        Assert.Equal(new PdfMeasurementPoint(172, 112), info.CaptionCenter);
        Assert.Equal(9, info.CaptionFontSize);
    }

    [Fact]
    public void LineCaptionCarriesTheStandardOffsetAndIsDrawnInTheAppearance()
    {
        var profile = new PdfMeasurementProfile("1 in = 1 in", 1.0 / 72, "in", 2);
        PdfDocument document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddDistanceMeasurement(0, new PdfPoint(100, 100), new PdfPoint(100, 244), profile,
                caption: new PdfMeasurementCaption(new PdfPoint(90, 172)))
            .Build());
        PdfDictionary annotation = Annotation(document, 0);

        Assert.True(Assert.IsType<PdfBoolean>(annotation[Name("Cap")]).Value);
        PdfArray offset = Assert.IsType<PdfArray>(annotation[Name("CO")]);
        // The line runs up the page, so 10 points to its left is 10 points across it.
        Assert.Equal(0, Number(offset[0]), 6);
        Assert.Equal(10, Number(offset[1]), 6);
        Assert.Equal("2.00 in", Text(annotation[Name("Contents")]));
        PdfStream appearance = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        string content = Encoding.ASCII.GetString(appearance.EncodedData.Span);
        Assert.Contains("(2.00 in) Tj", content);
        Assert.Contains("/Helv", content);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(Assert.IsType<PdfDictionary>(
            appearance.Dictionary[Name("Resources")])[Name("Font")]);
        Assert.True(fonts.ContainsKey(Name("Helv")));
        // The annotation rectangle grows to hold the label left of the line.
        PdfArray rectangle = Assert.IsType<PdfArray>(annotation[Name("Rect")]);
        Assert.True(Number(rectangle[0]) < 85);

        // A caption from /CO alone, with no KillerPDF position, still reads back.
        PdfMeasurementAnnotationInfo info = Assert.Single(PdfMeasurementAnnotationReader.Read(document));
        Assert.Equal(90, info.CaptionCenter!.Value.X, 6);
        Assert.Equal(172, info.CaptionCenter.Value.Y, 6);
    }

    [Fact]
    public void PerimeterAreaAndAngleReadBackAsTheirKinds()
    {
        var profile = new PdfMeasurementProfile("1 in = 1 m", 1.0 / 72, "m", 2);
        PdfPoint[] triangle = [new(10, 10), new(82, 10), new(82, 82)];
        PdfDocument document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 300).Build()))
            .AddPerimeterMeasurement(0, triangle, profile)
            .AddAreaMeasurement(0, triangle, profile, fillColor: new PdfRgbColor(0, 1, 0))
            .AddAngleMeasurement(0, new PdfPoint(150, 200), new PdfPoint(150, 150),
                new PdfPoint(200, 150))
            .Build());

        IReadOnlyList<PdfMeasurementAnnotationInfo> infos = PdfMeasurementAnnotationReader.Read(document);

        Assert.Equal(3, infos.Count);
        Assert.Equal(PdfMeasurementKind.Perimeter, infos[0].Kind);
        Assert.True(infos[0].Closed);
        Assert.Equal(3, infos[0].Points.Count);
        Assert.Equal(1.0 / 72, infos[0].Profile!.UnitsPerPoint, 9);
        Assert.Equal(PdfMeasurementKind.Area, infos[1].Kind);
        Assert.Equal(3, infos[1].Points.Count);
        Assert.Equal(new PdfRgbColor(0, 1, 0), infos[1].FillColor);
        Assert.Equal(PdfMeasurementKind.Angle, infos[2].Kind);
        Assert.Null(infos[2].Profile);
        Assert.Equal(new PdfMeasurementPoint(150, 150), infos[2].Points[1]);
    }

    [Fact]
    public void FeetAndInchesChainsAnInchesFormatAndReadsBack()
    {
        var profile = new PdfMeasurementProfile("1/4 in = 1 ft", 1.0 / 18, "ft-in", 1);
        PdfDocument document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddDistanceMeasurement(0, new PdfPoint(0, 0), new PdfPoint(60, 0), profile,
                contents: "3' 4\"")
            .Build());
        PdfDictionary measure = Assert.IsType<PdfDictionary>(
            Annotation(document, 0)[Name("Measure")]);
        PdfArray scale = Assert.IsType<PdfArray>(measure[Name("X")]);

        Assert.Equal(2, scale.Count);
        Assert.Equal("ft", Text(Assert.IsType<PdfDictionary>(scale[0])[Name("U")]));
        Assert.Equal("in", Text(Assert.IsType<PdfDictionary>(scale[1])[Name("U")]));
        Assert.Equal(12, Number(Assert.IsType<PdfDictionary>(scale[1])[Name("C")]));
        PdfMeasurementProfile read = Assert.Single(PdfMeasurementAnnotationReader.Read(document)).Profile!;
        Assert.Equal("ft-in", read.UnitSymbol);
        Assert.Equal(1.0 / 18, read.UnitsPerPoint, 9);
        Assert.Equal(1, read.Precision);
    }

    [Fact]
    public void PageScaleIsWrittenAsAViewportAndCanBeRemoved()
    {
        var profile = new PdfMeasurementProfile("1 in = 20 ft", 20.0 / 72, "ft", 2);
        byte[] source = new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build();
        byte[] scaled = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .SetPageMeasurementScale(1, profile)
            .AddDistanceMeasurement(0, new PdfPoint(0, 0), new PdfPoint(72, 0), profile)
            .Build();
        PdfDocument document = PdfDocument.Open(scaled);

        PdfMeasurementProfileAssignment assignment = Assert.Single(
            PdfMeasurementAnnotationReader.ReadPageScales(document));
        Assert.Equal(1, assignment.PageIndex);
        Assert.Null(assignment.Region);
        Assert.Equal("1 in = 20 ft", assignment.Profile.Name);
        Assert.Equal(20.0 / 72, assignment.Profile.UnitsPerPoint, 9);
        Assert.Equal(20.0, new PdfMeasurementProfileMap([assignment]).Resolve(1)
            .UnitsPerPoint * 72, 9);
        _ = new PdfIncrementalPageEditor(document);

        // A viewport and a new annotation on the same page share one page update.
        byte[] both = new PdfIncrementalAnnotationEditor(document)
            .SetPageMeasurementScale(0, profile, new PdfContentBounds(0, 0, 300, 400))
            .AddDistanceMeasurement(0, new PdfPoint(10, 10), new PdfPoint(82, 10), profile)
            .Build();
        PdfDocument withRegion = PdfDocument.Open(both);
        IReadOnlyList<PdfMeasurementProfileAssignment> scales =
            PdfMeasurementAnnotationReader.ReadPageScales(withRegion);
        Assert.Equal(2, scales.Count);
        Assert.Equal(new PdfContentBounds(0, 0, 300, 400), scales[0].Region);
        Assert.Equal(2, PdfMeasurementAnnotationReader.Read(withRegion).Count);

        byte[] removed = new PdfIncrementalAnnotationEditor(withRegion)
            .SetPageMeasurementScale(0, null)
            .SetPageMeasurementScale(1, null)
            .Build();
        Assert.Empty(PdfMeasurementAnnotationReader.ReadPageScales(PdfDocument.Open(removed)));
        Assert.Equal(2, PdfMeasurementAnnotationReader.Read(PdfDocument.Open(removed)).Count);
    }

    [Fact]
    public void InvalidCaptionAndScaleArgumentsAreRejected()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        var profile = new PdfMeasurementProfile("1 in = 1 in", 1.0 / 72, "in");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfMeasurementCaption(new PdfPoint(0, 0), 0.5));
        Assert.Throws<ArgumentException>(() => editor.AddLine(0, new PdfPoint(0, 0),
            new PdfPoint(10, 0), measurement: profile,
            caption: new PdfMeasurementCaption(new PdfPoint(5, 5))));
        Assert.Throws<ArgumentException>(() => editor.SetPageMeasurementScale(0, profile,
            new PdfContentBounds(10, 10, 5, 20)));
        Assert.Throws<ArgumentException>(() => editor.SetPageMeasurementScale(0, null,
            new PdfContentBounds(0, 0, 10, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetPageMeasurementScale(3, profile));
    }

    [Fact]
    public void RendererCanLeaveMeasurementsToTheViewer()
    {
        var profile = new PdfMeasurementProfile("1 in = 1 in", 1.0 / 72, "in");
        PdfDocument document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build()))
            .AddDistanceMeasurement(0, new PdfPoint(10, 50), new PdfPoint(90, 50), profile,
                new PdfRgbColor(1, 0, 0), lineWidth: 20)
            .Build());
        var renderer = new PdfPageRenderer(document);

        byte[] shown = renderer.Render(0, new PdfRenderOptions(50, 50)).Pixels.ToArray();
        byte[] hidden = renderer.Render(0, new PdfRenderOptions(50, 50)
            { IncludeMeasurementAnnotations = false }).Pixels.ToArray();

        Assert.NotEqual(shown, hidden);
        Assert.All(hidden, value => Assert.Equal(255, value));
    }

    private static PdfDictionary Annotation(PdfDocument document, int index)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfArray annotations = Assert.IsType<PdfArray>(Resolve(document,
            tree.Pages[0].Dictionary[Name("Annots")]));
        return Assert.IsType<PdfDictionary>(Resolve(document, annotations[index]));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => double.NaN
    };

    private static string Text(PdfObject value) => PdfUnicodeEncoding.DecodeTextString(
        Assert.IsType<PdfString>(value).Bytes.Span, "test");

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
