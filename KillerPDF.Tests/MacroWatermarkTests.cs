using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Parsing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class MacroWatermarkTests
{
    [Theory]
    [InlineData("", "32", ".4", "30")]
    [InlineData("Line\nbreak", "32", ".4", "30")]
    [InlineData("Text", "NaN", ".4", "30")]
    [InlineData("Text", "3,2", ".4", "30")]
    [InlineData("Text", "0", ".4", "30")]
    [InlineData("Text", "32", "0", "30")]
    [InlineData("Text", "32", "1.1", "30")]
    [InlineData("Text", "32", ".4", "Infinity")]
    [InlineData("Text", "32", ".4", "361")]
    public void InvalidImportedSettingsFailWithoutReturningAnOutput(
        string text, string size, string opacity, string rotation)
    {
        var step = new PdfMacroStep(PdfMacroOperation.Watermark, new Dictionary<string, string>
        {
            ["text"] = text, ["fontSize"] = size, ["opacity"] = opacity, ["rotation"] = rotation
        });
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 100).Build();
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro)]);

        var result = Assert.Single(dispatcher.RunReport(new("Invalid", [step]), [source]).Results);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Empty(new PdfPageContentReader(PdfDocument.Open(source)).Read(0).TextRuns);
    }

    [Theory]
    [InlineData("REVIEW")]
    [InlineData("ПРОВЕРКА")]
    public void ReusableWatermarkPreservesOriginalTextPathsAndEditableFields(string watermark)
    {
        byte[] original = new PdfDocumentBuilder().AddPage(300, 400,
            new PdfContentStreamBuilder().Rectangle(10, 20, 30, 40).Stroke()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 350).ShowLatin1Text("Original text").EndText())
            .AddBlankPage(400, 300).Build();
        byte[] source = new PdfIncrementalPageEditor(PdfDocument.Open(original))
            .AddTextField(0, "customer", 10, 100, 100, 20).Build();
        byte[] unchanged = source.ToArray();
        var macro = PdfMacro.FromJson(new PdfMacro("Review", [
            PdfEngineBurn.WatermarkStep(new(watermark, 32, .4, 30))]).ToJson());
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro)]);

        PdfMacroFileResult result = Assert.Single(dispatcher.RunReport(macro, [source]).Results);

        Assert.True(result.Succeeded, result.Error);
        PdfDocument output = PdfDocument.Open(result.Data!.Value);
        var reader = new PdfPageContentReader(output);
        Assert.Equal(2, reader.PageCount);
        Assert.Contains("Original text", string.Join("", reader.Read(0).TextRuns.Select(run => run.Text)));
        Assert.Contains(watermark, string.Join("", reader.Read(0).TextRuns.Select(run => run.Text)));
        Assert.Contains(watermark, string.Join("", reader.Read(1).TextRuns.Select(run => run.Text)));
        Assert.Single(reader.Read(0).Paths);
        Assert.Empty(reader.Read(0).Images);
        Assert.Equal("customer", Assert.Single(PdfFormWidgetReader.ReadPage(output, 0)).FieldName);
        Assert.Equal(400, reader.Read(1).Width);
        Assert.Equal(300, reader.Read(1).Height);
        Assert.Equal(unchanged, source);
    }

    [Fact]
    public void UnknownImportedSettingsAreRejectedInsteadOfIgnored()
    {
        var step = new PdfMacroStep(PdfMacroOperation.Watermark, new Dictionary<string, string>
        {
            ["text"] = "REVIEW", ["imagePath"] = "unavailable.png"
        });
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 100).Build();
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro)]);

        var result = Assert.Single(dispatcher.RunReport(new("Invalid", [step]), [source]).Results);

        Assert.False(result.Succeeded);
        Assert.Null(result.Data);
    }

    [Fact]
    public void CanceledWatermarkDoesNotReturnAnOutput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 100).Build();
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro)]);

        Assert.Throws<OperationCanceledException>(() => dispatcher.Execute(
            PdfEngineBurn.WatermarkStep(new("REVIEW")), source,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void WatermarkWorkingFileComesFromTheSessionTempSeamAndIsDeleted()
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-watermark-seam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var tags = new List<string>();
        string? used = null;
        var previous = PdfEngineBurn.MacroTempFile;
        PdfEngineBurn.MacroTempFile = tag =>
        {
            tags.Add(tag);
            return used = Path.Combine(folder, $"killerpdf_{tag}_seam.pdf");
        };
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage(200, 200).Build();
            var dispatcher = new PdfMacroDispatcher([
                new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro)]);

            var result = Assert.Single(dispatcher.RunReport(new("Seam", [PdfEngineBurn.WatermarkStep(new("REVIEW"))]), [source]).Results);

            Assert.True(result.Succeeded);
            Assert.Equal(["watermark"], tags);
            Assert.False(File.Exists(used));
        }
        finally
        {
            PdfEngineBurn.MacroTempFile = previous;
            Directory.Delete(folder, recursive: true);
        }
    }
}
