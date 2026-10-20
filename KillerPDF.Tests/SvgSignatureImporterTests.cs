using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SvgSignatureImporterTests
{
    [Fact]
    public void Import_RejectsAbsurdlyDeepNestingInsteadOfOverflowingTheStack()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-svg-deep-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "deep.svg");
            const int depth = 50_000;
            File.WriteAllText(file,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">"
                + string.Concat(Enumerable.Repeat("<g>", depth)) + "<path d=\"M0 0L1 1\"/>"
                + string.Concat(Enumerable.Repeat("</g>", depth)) + "</svg>");

            Assert.Throws<InvalidDataException>(() => SvgSignatureImporter.Import(file));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_ReadsPassiveVectorPathsAndRejectsExternalContent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-svg-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string valid = Path.Combine(root, "signature.svg");
            File.WriteAllText(valid,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 150\"><path d=\"M 10 80 C 80 10 160 140 390 60\"/></svg>");
            SvgSignatureDocument signature = SvgSignatureImporter.Import(valid);

            Assert.Equal(400, signature.Width);
            Assert.Equal(150, signature.Height);
            Assert.Equal("M 10 80 C 80 10 160 140 390 60", Assert.Single(signature.Paths));

            string external = Path.Combine(root, "external.svg");
            File.WriteAllText(external,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><image href=\"https://example.com/a.png\"/><path d=\"M0 0L1 1\"/></svg>");
            Assert.Throws<InvalidDataException>(() => SvgSignatureImporter.Import(external));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_KeepsStyledOutlinesInViewBoxUnitsAndDropsThePaper()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-svg-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "styled.svg");
            File.WriteAllText(file,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"10 20 200 100\">" +
                "<rect x=\"10\" y=\"20\" width=\"200\" height=\"100\" fill=\"#fff\"/>" +
                "<g transform=\"translate(5 0)\" style=\"fill:none;stroke:#0000ff;stroke-width:2;stroke-linecap:round\">" +
                "<path d=\"M10 20 L110 70\"/></g>" +
                "<circle cx=\"60\" cy=\"70\" r=\"5\" fill=\"rgb(255,0,0)\" fill-opacity=\"0.5\"/>" +
                "<path d=\"M0 0 L5 5\" display=\"none\"/></svg>");
            SvgSignatureDocument signature = SvgSignatureImporter.Import(file);

            Assert.Equal(2, signature.Shapes.Count);
            SignatureVector line = signature.Shapes[0];
            Assert.Equal("M 5 0 L 105 50", line.Data);
            Assert.Null(line.Fill);
            Assert.Equal(0xFF0000FFu, line.Stroke);
            Assert.Equal(2, line.StrokeWidth);
            Assert.Equal(1, line.LineCap);
            SignatureVector dot = signature.Shapes[1];
            Assert.Equal(0x80FF0000u, dot.Fill);
            Assert.StartsWith("M 55 50 C", dot.Data);

            var (vectors, width, height) = signature.Fit(400, 150);
            Assert.Equal(300, width);
            Assert.Equal(150, height);
            Assert.Equal("M 7.5 0 L 157.5 75", vectors[0].Data);
            Assert.Equal(3, vectors[0].StrokeWidth);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Import_AppliesNestedTransformsInSvgOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-svg-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "turned.svg");
            File.WriteAllText(file,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                "<g transform=\"translate(50,50)\"><path transform=\"scale(2) rotate(90)\" d=\"M0 0 L10 0\"/></g></svg>");
            SvgSignatureDocument signature = SvgSignatureImporter.Import(file);

            Assert.Equal("M 50 50 L 50 70", Assert.Single(signature.Shapes).Data);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
