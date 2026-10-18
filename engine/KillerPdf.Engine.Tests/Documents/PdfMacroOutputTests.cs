using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMacroOutputTests
{
    [Fact]
    public void DataMergeKeepsEachFormEditableAndAppliesFollowingRename()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(612, 792,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 700, 612, 92).Fill())
            .AddTextField(0, "name", 20, 20, 140, 24).Build();
        var profile = new PdfDataMergeProfile("Customers", [new("Name", "name")], "{{Name}}.pdf");
        IReadOnlyDictionary<string, string?>[] records = [
            new Dictionary<string, string?> { ["Name"] = "Ada" },
            new Dictionary<string, string?> { ["Name"] = "Grace" }];
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.DataMerge, (step, data, _, token) => PdfDataMergeMacro.Execute(step, data, records, token)),
            new(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
            new(PdfMacroOperation.ConvertColor, PdfColorConversionMacro.Execute),
            new(PdfMacroOperation.Rename, PdfRenameMacro.Execute)]);

        PdfMacroRunReport report = dispatcher.RunReport(new("Customers", [profile.ToMacroStep(), PdfResizeMacro.ScaleStep(50),
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            PdfRenameMacro.RenameStep("{name}-ready-{index}")]), [source], PdfRenameMacro.CreateContext("template.pdf", 1));

        Assert.True(Assert.Single(report.Results).Succeeded);
        Assert.All(report.Results[0].Outputs!, output =>
        {
            PdfDocument document = PdfDocument.Open(output.Data!.Value);
            Assert.Equal(306, Assert.Single(PdfPageInformation.Read(document)).Width);
            var bitmap = new KillerPdf.Engine.Rendering.PdfPageRenderer(document).Render(0,
                new KillerPdf.Engine.Rendering.PdfRenderOptions(306, 396));
            Assert.Equal(new byte[] { 77, 77, 77, 255 }, bitmap.Pixels.Slice((10 * 306 + 10) * 4, 4).ToArray());
        });
        Assert.Collection(report.Results[0].Outputs!,
            output => { Assert.Equal("Ada-ready-1.pdf", output.OutputFileName);
                Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(output.Data!.Value), 0)).Value); },
            output => { Assert.Equal("Grace-ready-1.pdf", output.OutputFileName);
                Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(output.Data!.Value), 0)).Value); });
        Assert.Equal("", Assert.Single(PdfFormWidgetReader.ReadPage(PdfDocument.Open(source), 0)).Value);
    }

    [Fact]
    public void RecordFailuresRemainVisibleAndDoNotStopOtherOutputs()
    {
        var macro = new PdfMacro("Records", [new(PdfMacroOperation.DataMerge), new(PdfMacroOperation.Validate)]);
        PdfMacroRunReport report = PdfMacroRunner.RunContextualReport(macro, [new byte[] { 9 }], null,
            (step, data, _, _) => step.Operation == PdfMacroOperation.DataMerge
                ? new(data) { Outputs = [new(new byte[] { 1 }, "ada.pdf", RecordIndex: 0),
                    new(null, "bad.pdf", "Missing customer", 1), new(new byte[] { 3 }, "grace.pdf", RecordIndex: 2)] }
                : data.Span[0] == 1 ? throw new InvalidOperationException("Invalid Ada") : new(data));

        Assert.Equal(1, report.FailedCount);
        Assert.Collection(Assert.Single(report.Results).Outputs!,
            output => { Assert.Equal("Invalid Ada", output.Error); Assert.Equal(1, output.FailedStepIndex); },
            output => { Assert.Equal("Missing customer", output.Error); Assert.Equal(0, output.FailedStepIndex); },
            output => Assert.True(output.Succeeded));
        Assert.Contains("grace.pdf", report.ToText(), StringComparison.Ordinal);
        Assert.Contains("Invalid Ada", report.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("AQ==", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void LaterStepsProcessEachGeneratedRecordWithItsOwnContext()
    {
        var macro = new PdfMacro("Records", [new(PdfMacroOperation.DataMerge),
            new(PdfMacroOperation.Save, new Dictionary<string, string> { ["suffix"] = "${record}" })]);
        byte[] source = [9];

        PdfMacroFileResult result = Assert.Single(PdfMacroRunner.RunContextual(macro, [source], null,
            (step, data, values, _) => step.Operation == PdfMacroOperation.DataMerge
                ? new(data) { Outputs = [
                    new(new byte[] { 1 }, "ada.pdf", Values: new Dictionary<string, string> { ["record"] = "Ada" }),
                    new(new byte[] { 2 }, "grace.pdf", Values: new Dictionary<string, string> { ["record"] = "Grace" })] }
                : new(data.ToArray().Append((byte)step.Settings!["suffix"].Length).ToArray())));

        Assert.True(result.Succeeded);
        Assert.Null(result.Data);
        Assert.Collection(result.Outputs!,
            output => { Assert.Equal("ada.pdf", output.OutputFileName); Assert.Equal(new byte[] { 1, 3 }, output.Data!.Value.ToArray()); },
            output => { Assert.Equal("grace.pdf", output.OutputFileName); Assert.Equal(new byte[] { 2, 5 }, output.Data!.Value.ToArray()); });
        Assert.Equal(new byte[] { 9 }, source);
    }
}
