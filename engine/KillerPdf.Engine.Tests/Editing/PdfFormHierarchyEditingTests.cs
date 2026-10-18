using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfFormHierarchyEditingTests
{
    [Theory]
    [InlineData("customer", "client")]
    [InlineData("customer.last", "first")]
    public void AmbiguousParentAndDuplicateFieldRenamesStillFail(string fieldName, string replacement)
    {
        PdfDocument source = PdfDocument.Open(new PdfIncrementalPageEditor(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddTextField(0, "customer.first", 10, 10, 100, 20, "Ada")
            .AddTextField(0, "customer.last", 10, 40, 100, 20, "Lovelace").Build());

        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(source)
            .RenameFormField(fieldName, replacement).Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamingOneParentOntoAnotherStillFailsInEitherFieldOrder(bool reverse)
    {
        var builder = new PdfDocumentBuilder().AddBlankPage();
        foreach (string name in reverse ? new[] { "customer.last", "client.first" } : new[] { "client.first", "customer.last" })
            builder.AddTextField(0, name, 10, 10, 100, 20);
        PdfDocument source = PdfDocument.Open(builder.Build());

        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(source)
            .RenameFormField("client", "customer").Build());
    }

    [Fact]
    public void CalculationOrderStillRejectsAnAmbiguousParentName()
    {
        PdfDocument source = PdfDocument.Open(new PdfIncrementalPageEditor(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddTextField(0, "customer.first", 10, 10, 100, 20)
            .AddTextField(0, "customer.last", 10, 40, 100, 20).Build());

        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(source)
            .SetFormCalculationOrder(["customer"]).Build());
    }

    [Fact]
    public void CalculationOrderCanAddressUniqueChildrenInSeparateParentGroups()
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfDocument source = PdfDocument.Open(new PdfIncrementalPageEditor(blank)
            .AddTextField(0, "customer.first", 10, 10, 100, 20, "Ada")
            .AddTextField(0, "customer.last", 10, 40, 100, 20, "Lovelace").Build());

        PdfDocument edited = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetFormCalculationOrder(["customer.last", "customer.first"]).Build());

        var catalog = (PdfDictionary)edited.Resolve((PdfIndirectReference)edited.Trailer[new PdfName("Root"u8)]);
        PdfObject formValue = catalog[new PdfName("AcroForm"u8)];
        var form = (PdfDictionary)(formValue is PdfIndirectReference reference ? edited.Resolve(reference) : formValue);
        var order = (PdfArray)form[new PdfName("CO"u8)];
        var widgets = PdfFormWidgetReader.ReadPage(edited, 0).ToDictionary(field => field.ObjectNumber, field => field.FieldName);
        Assert.Equal(["customer.last", "customer.first"], order.Cast<PdfIndirectReference>().Select(item => widgets[item.ObjectNumber]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QualifiedFieldsAddedSeparatelyRemainIndependentlyEditable(bool rename)
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfDocument source = PdfDocument.Open(new PdfIncrementalPageEditor(blank)
            .AddTextField(0, "customer.first", 10, 10, 100, 20, "Ada")
            .AddTextField(0, "customer.last", 10, 40, 100, 20, "Lovelace").Build());

        string fieldName = rename ? "customer.surname" : "customer.last";
        if (rename) source = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .RenameFormField("customer.last", "surname").Build());
        PdfDocument edited = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetTextFieldValue(fieldName, "Byron").Build());

        var fields = PdfFormWidgetReader.ReadPage(edited, 0).ToDictionary(field => field.FieldName);
        Assert.Equal("Ada", fields["customer.first"].Value);
        Assert.Equal("Byron", fields[fieldName].Value);
        Assert.Equal("Lovelace", PdfFormWidgetReader.ReadPage(source, 0).Single(field => field.FieldName == fieldName).Value);
    }
}
