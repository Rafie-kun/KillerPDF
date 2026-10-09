using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfBlendExtensionTests
{
    public static IEnumerable<object?[]> Cases()
    {
        var declarations = new (bool Header17, string? Version, string Vendor,
            string BaseVersion, int Level, bool IntegerLevel, bool Modern)[]
        {
            (true, null, "ADBE", "1.7", 4, true, false),
            (true, null, "ADBE", "1.7", 5, true, true),
            (true, null, "ADBE", "1.7", 8, true, true),
            (true, "1.6", "ADBE", "1.7", 5, true, true),
            (false, "1.7", "ADBE", "1.7", 5, true, true),
            (false, null, "ADBE", "1.7", 5, true, false),
            (true, null, "OTHER", "1.7", 5, true, false),
            (true, null, "ADBE", "1.6", 5, true, false),
            (true, null, "ADBE", "1.7", 5, false, false),
            (true, "2.0", "OTHER", "1.6", 1, true, true),
        };
        foreach (var declaration in declarations)
        foreach (PdfBlendMode mode in new[] { PdfBlendMode.ColorDodge, PdfBlendMode.ColorBurn })
        foreach (bool cmyk in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
            yield return [declaration.Header17, declaration.Version, declaration.Vendor,
                declaration.BaseVersion, declaration.Level, declaration.IntegerLevel,
                declaration.Modern, mode, cmyk, indirect];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DeclaredExtensionSelectsCorrectBlendEndpoints(bool header17,
        string? version, string vendor, string baseVersion, int level, bool integerLevel,
        bool modern, PdfBlendMode mode, bool cmyk, bool indirect)
    {
        double backdrop = mode == PdfBlendMode.ColorBurn ? 1 : 0;
        var content = new PdfContentStreamBuilder();
        void Fill(double value)
        {
            if (cmyk) content.SetFillCmyk(1 - value, 1 - value, 1 - value, 0);
            else content.SetFillRgb(value, value, value);
            content.Rectangle(0, 0, 1, 1).Fill();
        }
        Fill(backdrop);
        content.SetBlendMode(mode);
        Fill(1 - backdrop);
        if (cmyk)
            content = new PdfContentStreamBuilder().DrawForm(new PdfFormXObject(1, 1, content,
                isolatedTransparencyGroup: true,
                transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Cmyk), 0, 0);
        var original = PdfDocument.Open(new PdfDocumentBuilder(
            header17 ? PdfVersion.Pdf17 : new PdfVersion(1, 6)).AddPage(1, 1, content).Build());
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        var root = (PdfIndirectReference)original.Trailer[Name("Root")];
        var catalog = (PdfDictionary)original.Resolve(root);
        var update = new PdfIncrementalUpdateBuilder(original);
        PdfObject Indirect(PdfObject value) => indirect ? update.AddObject(value) : value;
        var extension = new PdfDictionary([
            Entry("BaseVersion", Indirect(Name(baseVersion))),
            Entry("ExtensionLevel", Indirect(integerLevel ? new PdfInteger(level) : new PdfReal(level)))]);
        var extensions = new PdfDictionary([Entry(vendor, Indirect(extension))]);
        var entries = catalog.Append(Entry("Extensions", Indirect(extensions)));
        if (version is not null) entries = entries.Append(Entry("Version", Indirect(Name(version))));
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(entries));
        var page = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1));
        byte expected = (byte)((modern ? backdrop : 1 - backdrop) * 255);
        uint rgb = cmyk ? PdfDeviceCmyk.ToRgb(expected == 0 ? 0x00FFFFFFu : 0u)
            : (uint)(expected | expected << 8 | expected << 16);
        Assert.Equal(new byte[] { (byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16), 255 },
            page.Pixels.ToArray());
        Assert.Empty(page.Diagnostics);
    }
}
