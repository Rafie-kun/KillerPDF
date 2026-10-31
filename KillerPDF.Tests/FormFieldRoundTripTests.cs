using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FormFieldRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FontSizeOnlyEditPreservesExistingValueAndChangesAppearance(bool editSibling)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-field-size-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Fixture("Original"));
            byte[] before = Render(PdfDocument.Open(File.ReadAllBytes(path)));
            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                editSibling ? new Dictionary<string, string> { ["sibling"] = "Changed" } : new(),
                new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, bool>(), new Dictionary<string, string>(),
                new Dictionary<string, double> { ["customer"] = 20 }));

            PdfDocument saved = PdfDocument.Open(File.ReadAllBytes(path));
            var fields = PdfFormWidgetReader.ReadPage(saved, 0).ToDictionary(field => field.FieldName);
            Assert.Equal("Original", fields["customer"].Value);
            Assert.Contains("20 Tf", fields["customer"].DefaultAppearance);
            Assert.Equal(editSibling ? "Changed" : "Keep", fields["sibling"].Value);
            Assert.False(before.AsSpan().SequenceEqual(Render(saved)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ClearingTextPersistsEmptyValueAndAppearanceWithoutChangingSibling()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-clear-field-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Fixture("Original"));
            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string> { ["customer"] = "" },
                new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, bool>(), new Dictionary<string, string>(),
                new Dictionary<string, double>()));

            PdfDocument saved = PdfDocument.Open(File.ReadAllBytes(path));
            var fields = PdfFormWidgetReader.ReadPage(saved, 0).ToDictionary(field => field.FieldName);
            Assert.Equal("", fields["customer"].Value);
            Assert.Equal("Keep", fields["sibling"].Value);
            byte[] expectedPixels = Render(PdfDocument.Open(Fixture("")));
            Assert.Equal(expectedPixels, Render(saved));
            Assert.False(expectedPixels.AsSpan().SequenceEqual(Render(PdfDocument.Open(Fixture("Original")))));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] Fixture(string value) => new PdfDocumentBuilder().AddBlankPage()
        .AddTextField(0, "customer", 20, 20, 140, 24, value, defaultValue: "Default")
        .AddTextField(0, "sibling", 20, 70, 140, 24, "Keep")
        .Build();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingFieldPreservesSiblingWhenFormCatalogIsMissing(bool missingFormCatalog)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-delete-field-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = Fixture("Original");
            if (missingFormCatalog)
                source = WithoutFormCatalog(source);
            File.WriteAllBytes(path, source);
            Assert.Equal(2, PdfFormWidgetReader.ReadPage(PdfDocument.Open(source), 0).Count);

            PdfEngineIntegration.RemoveFormField(path, "customer");

            PdfDocument saved = PdfDocument.Open(File.ReadAllBytes(path));
            PdfFormWidgetInfo sibling = Assert.Single(PdfFormWidgetReader.ReadPage(saved, 0));
            Assert.Equal("sibling", sibling.FieldName);
            Assert.Equal("Keep", sibling.Value);
            byte[] expected = new PdfDocumentBuilder().AddBlankPage()
                .AddTextField(0, "sibling", 20, 70, 140, 24, "Keep").Build();
            Assert.Equal(Render(PdfDocument.Open(expected)), Render(saved));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void UnsupportedUnlistedHierarchyLeavesSourceUnchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-unsupported-field-{Guid.NewGuid():N}.pdf");
        byte[] source = WithoutFormCatalog(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 20, 20, 140, 24, "Original").Build());
        try
        {
            File.WriteAllBytes(path, source);
            Assert.Throws<NotSupportedException>(() => PdfEngineIntegration.RemoveFormField(path, "customer.name"));
            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] WithoutFormCatalog(byte[] source)
    {
        PdfDocument original = PdfDocument.Open(source);
        var root = (PdfIndirectReference)original.Trailer[new PdfName("Root"u8)];
        var catalog = (PdfDictionary)original.Resolve(root);
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(
            catalog.Where(entry => !entry.Key.Equals(new PdfName("AcroForm"u8)))));
        return update.Build();
    }

    private static byte[] Render(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(306, 396)).Pixels.ToArray();
}
