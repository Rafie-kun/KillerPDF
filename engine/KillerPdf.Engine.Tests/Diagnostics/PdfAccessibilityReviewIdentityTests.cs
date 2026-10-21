using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfAccessibilityReviewIdentityTests
{
    [Fact]
    public void FindingPreservesOriginalConstructorAndFiveValueDeconstruction()
    {
        Type[] constructorParameters = [typeof(PdfAccessibilityFindingCode), typeof(PdfDiagnosticSeverity),
            typeof(string), typeof(int?), typeof(int?)];
        var constructor = typeof(PdfAccessibilityFinding).GetConstructor(constructorParameters);
        Assert.NotNull(constructor);
        var finding = Assert.IsType<PdfAccessibilityFinding>(constructor.Invoke([
            PdfAccessibilityFindingCode.MissingLinkDescription, PdfDiagnosticSeverity.Error, "Missing description", 2, 7]));
        var deconstruct = typeof(PdfAccessibilityFinding).GetMethod("Deconstruct",
            constructorParameters.Select(type => type.MakeByRefType()).ToArray());
        Assert.NotNull(deconstruct);
        object?[] values = new object?[5];
        deconstruct.Invoke(finding, values);
        Assert.Equal(new object?[] { PdfAccessibilityFindingCode.MissingLinkDescription,
            PdfDiagnosticSeverity.Error, "Missing description", 2, 7 }, values);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkFindingsIdentifyTheAnnotationAndUnsupportedDirectTargets(bool direct)
    {
        string[] links = [
            "<< /Type /Annot /Subtype /Link /Rect [10 10 50 20] /A << /S /URI /URI (https://first.test) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 40 50 50] /A << /S /URI /URI (https://second.test) >> >>"];
        PdfDocument document = OpenFixture(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots ["
            + (direct ? string.Join(' ', links) : "4 0 R 5 0 R") + "] >>", direct ? null : links);
        using JsonDocument json = JsonDocument.Parse(PdfAccessibilityInspector.Inspect(document).ToJson());
        JsonElement[] findings = json.RootElement.GetProperty("findings").EnumerateArray()
            .Where(item => item.GetProperty("code").GetString() == "missingLinkDescription").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.Equal(0, findings[0].GetProperty("annotationIndex").GetInt32());
        Assert.Equal(1, findings[1].GetProperty("annotationIndex").GetInt32());
        var preview = PdfAccessibilityRepair.PreviewLinkDescription(document, 0,
            findings[1].GetProperty("annotationIndex").GetInt32(), "Second destination");
        if (direct)
        {
            Assert.False(preview.WillChange);
            Assert.Throws<InvalidOperationException>(() => PdfAccessibilityRepair.ApplyLinkDescription(document, preview));
            Assert.All(PdfLinkReader.ReadPage(document, 0), link => Assert.Null(link.Description));
            return;
        }
        PdfDocument reopened = PdfDocument.Open(PdfAccessibilityRepair.ApplyLinkDescription(document, preview).Document);
        var repairedLinks = PdfLinkReader.ReadPage(reopened, 0);
        Assert.Null(repairedLinks[0].Description);
        Assert.Equal("Second destination", repairedLinks[1].Description);
    }

    [Fact]
    public void DirectWidgetFindingsIdentifyTheirParentFields()
    {
        PdfDocument document = OpenFixture(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots ["
            + "<< /Type /Annot /Subtype /Widget /Rect [10 10 50 20] /Parent 4 0 R >> "
            + "<< /Type /Annot /Subtype /Widget /Rect [10 40 50 50] /Parent 5 0 R >>] >>",
            ["<< /FT /Tx /T (first) >>", "<< /FT /Tx /T (second) >>"],
            catalogEntries: "/AcroForm << /Fields [4 0 R 5 0 R] >>");
        PdfAccessibilityFinding[] findings = PdfAccessibilityInspector.Inspect(document).Findings
            .Where(item => item.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription).ToArray();
        Assert.Equal(new[] { "first", "second" }, findings.Select(item => item.FieldName));
        Assert.Equal(new int?[] { 0, 1 }, findings.Select(item => item.AnnotationIndex));
        var preview = PdfAccessibilityRepair.PreviewFormFieldDescription(document, findings[1].FieldName!, "Second field");
        PdfDocument reopened = PdfDocument.Open(PdfAccessibilityRepair.ApplyFormFieldDescription(document, preview).Document);
        var widgets = PdfFormWidgetReader.ReadPage(reopened, 0);
        Assert.Empty(widgets[0].Tooltip);
        Assert.Equal("Second field", widgets[1].Tooltip);
    }

    [Fact]
    public void FigureGenerationIdentityRejectsUnsupportedAndStaleRepairs()
    {
        PdfDocument original = FigureDocument(0);
        var preview = PdfAccessibilityRepair.PreviewFigureAlternateDescription(original, 4, "A square");
        Assert.True(preview.WillChange);
        PdfDocument changed = FigureDocument(2);
        PdfAccessibilityFinding finding = Assert.Single(PdfAccessibilityInspector.Inspect(changed).Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription);
        Assert.Equal(2, finding.ObjectGeneration);
        Assert.False(PdfAccessibilityRepair.PreviewFigureAlternateDescription(changed, 4, "A square").WillChange);
        Assert.Throws<InvalidOperationException>(() => PdfAccessibilityRepair.ApplyFigureAlternateDescription(changed, preview));
        Assert.Contains(PdfAccessibilityInspector.Inspect(changed).Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription);

        static PdfDocument FigureDocument(int generation) => OpenFixture(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            ["<< /Type /StructElem /S /Figure /Pg 3 0 R /K 0 >>"], generation,
            $"/MarkInfo << /Marked true >> /StructTreeRoot << /Type /StructTreeRoot /K 4 {generation} R >>");
    }

    [Fact]
    public void TrulyDirectFormFieldIsIdentifiedButApplyFailsWithoutChangingItsDescription()
    {
        const string field = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (direct) /Rect [10 10 50 20] >>";
        PdfDocument document = OpenFixture(
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [{field}] >>",
            catalogEntries: $"/AcroForm << /Fields [{field}] >>");
        PdfAccessibilityFinding finding = Assert.Single(PdfAccessibilityInspector.Inspect(document).Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription);
        Assert.Equal("direct", finding.FieldName);
        Assert.Equal(0, finding.AnnotationIndex);
        var preview = PdfAccessibilityRepair.PreviewFormFieldDescription(document, finding.FieldName!, "Direct field");
        Assert.Throws<NotSupportedException>(() => PdfAccessibilityRepair.ApplyFormFieldDescription(document, preview));
        Assert.Empty(Assert.Single(PdfFormWidgetReader.ReadPage(document, 0)).Tooltip);
    }

    private static PdfDocument OpenFixture(string page, string[]? extraObjects = null, int generation = 0,
        string catalogEntries = "")
    {
        string[] objects = [$"<< /Type /Catalog /Pages 2 0 R /Lang (en-US) {catalogEntries} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", page,
            .. extraObjects ?? []];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{index + 1} {(index == 3 ? generation : 0)} obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int index = 0; index < offsets.Count; index++)
            text.Append($"{offsets[index]:D10} {(index == 3 ? generation : 0):D5} n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.ASCII.GetBytes(text.ToString()));
    }
}
