using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfOptionalCheckBoxAppearanceTests
{
    [Theory]
    [InlineData(false, "/Off")]
    [InlineData(true, "/Yes")]
    public void ImportAcceptsCheckBoxWithoutOptionalOffAppearance(bool selected, string expected)
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddCheckBox(0, "approved", 20, 20, 20, 20).Build());
        PdfFormWidgetInfo widget = Assert.Single(PdfFormWidgetReader.ReadPage(original, 0));
        var reference = new PdfIndirectReference(widget.ObjectNumber, widget.Generation);
        PdfDictionary field = Assert.IsType<PdfDictionary>(original.Resolve(reference));
        PdfDictionary appearance = Assert.IsType<PdfDictionary>(Resolve(field[N("AP")]));
        PdfDictionary states = Assert.IsType<PdfDictionary>(Resolve(appearance[N("N")]));
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(widget.ObjectNumber, new PdfDictionary(field.Select(entry =>
            new KeyValuePair<PdfName, PdfObject>(entry.Key, entry.Key.Equals(N("AP"))
                ? new PdfDictionary(appearance.Select(item =>
                    new KeyValuePair<PdfName, PdfObject>(item.Key, item.Key.Equals(N("N"))
                        ? new PdfDictionary(states.Where(state => !state.Key.Equals(N("Off"))))
                        : item.Value))) : entry.Value))));
        PdfDocument source = PdfDocument.Open(update.Build());

        byte[] output = PdfFormDataImporter.Apply(source, new PdfFormDataSet
        {
            Fields = [new PdfFormDataField { Name = "approved", Values = [selected ? "Yes" : "Off"] }]
        });

        Assert.Equal(expected, Assert.Single(PdfFormDataExporter.Export(PdfDocument.Open(output)).Fields).Values[0]);
        Assert.Equal(source.Source.ToArray(), output[..source.Source.Length]);
        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(PdfDocument.Open(output)));
        Assert.Empty(PdfFormWidgetReader.ReadPage(flattened, 0));

        PdfObject Resolve(PdfObject value) => value is PdfIndirectReference indirect ? original.Resolve(indirect) : value;
    }

    private static PdfName N(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
