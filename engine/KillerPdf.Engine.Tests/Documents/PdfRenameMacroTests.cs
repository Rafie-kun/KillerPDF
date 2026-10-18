using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfRenameMacroTests
{
    [Fact]
    public void PlansEveryRenamedOutputBeforeExecution()
    {
        var macro = new PdfMacro("Archive", [
            PdfRenameMacro.RenameStep("{name}-archived-{index}"),
            new(PdfMacroOperation.Save)]);

        Assert.Equal(["alpha-archived-1.pdf", "beta-archived-2.pdf"],
            PdfRenameMacro.PlanFileNames(macro, ["alpha.pdf", "beta.pdf"]));
    }

    [Fact]
    public void ExecutionCarriesTheRenamedFileThroughLaterStepsWithoutChangingPdfBytes()
    {
        byte[] source = new KillerPdf.Engine.Authoring.PdfDocumentBuilder().AddBlankPage().Build();
        var macro = new PdfMacro("Rename", [PdfRenameMacro.RenameStep("final-{name}-{index}"), new(PdfMacroOperation.Save)]);
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Rename, PdfRenameMacro.Execute),
            new(PdfMacroOperation.Save, (_, data, _, _) => new(data))]);

        var report = dispatcher.RunReport(macro, [new ReadOnlyMemory<byte>(source)],
            PdfRenameMacro.CreateContext("alpha.pdf", 2));
        var result = Assert.Single(report.Results);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("final-alpha-2.pdf", result.OutputFileName);
        Assert.Equal(source, result.Data!.Value.ToArray());
        Assert.Contains("final-alpha-2.pdf", report.ToText());
        Assert.Contains("\"outputFileName\":\"final-alpha-2.pdf\"", report.ToJson());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("{unknown}")]
    [InlineData("CON")]
    [InlineData("COM\u00b9")]
    [InlineData("trailing.")]
    [InlineData("invalid?")]
    public void RejectsUnsafeWindowsOutputNames(string template)
    {
        Assert.Throws<ArgumentException>(() => PdfRenameMacro.RenameStep(template));
    }

    [Fact]
    public void RejectsDuplicateTargetsBeforeExecution()
    {
        var macro = new PdfMacro("Collision", [PdfRenameMacro.RenameStep("same")]);
        Assert.Throws<ArgumentException>(() => PdfRenameMacro.PlanFileNames(macro, ["alpha.pdf", "beta.pdf"]));
    }
}
