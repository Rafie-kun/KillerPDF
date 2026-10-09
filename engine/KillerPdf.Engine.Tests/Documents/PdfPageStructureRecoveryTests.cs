using System.Text;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed partial class PdfPageStructureRecoveryTests
{
    [Theory]
    [InlineData("missing-catalog")]
    [InlineData("wrong-catalog-number")]
    [InlineData("missing-pages-link")]
    [InlineData("wrong-pages-link")]
    [InlineData("missing-pages-root")]
    [InlineData("root-not-reference")]
    [InlineData("root-wrong-object")]
    [InlineData("recursive-kid")]
    [InlineData("nonexistent-kid")]
    public void IntactPageSurvivesDamagedLinksAndBothWriterPaths(string damage)
    {
        byte[] original = new PdfDocumentBuilder().AddPage(32, 32,
            "1 0 0 rg 4 4 24 24 re f"u8.ToArray()).Build();
        PdfDocument clean = PdfDocument.Open(original);
        PdfPageTree tree = PdfPageTree.Read(clean);
        int catalog = tree.CatalogReference.ObjectNumber, root = tree.RootReference.ObjectNumber;
        int page = tree.Pages[0].Reference.ObjectNumber;
        string source = Encoding.Latin1.GetString(original);
        source = damage switch
        {
            "missing-catalog" => DeleteObject(catalog),
            "wrong-catalog-number" => source.Replace($"{catalog} 0 obj", "99 0 obj"),
            "missing-pages-link" => ChangeObject(catalog, value => PagesLinkRegex().Replace(value, "")),
            "wrong-pages-link" => ChangeObject(catalog, value => PagesLinkRegex().Replace(value, "/Pages 999 0 R")),
            "missing-pages-root" => DeleteObject(root),
            "root-not-reference" => RootLinkRegex().Replace(source, "/Root 1"),
            "root-wrong-object" => RootLinkRegex().Replace(source, $"/Root {page} 0 R"),
            "recursive-kid" => ChangeObject(root, value => KidsRegex().Replace(value, $"/Kids [{root} 0 R]")),
            "nonexistent-kid" => ChangeObject(root, value => KidsRegex().Replace(value, "/Kids [999 0 R]")),
            _ => throw new ArgumentException(damage)
        };
        byte[] damaged = Encoding.Latin1.GetBytes(source);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(damaged);
        byte[] expected = Pixels(clean);
        Assert.Equal(expected, Pixels(recovered));
        Assert.NotEmpty(recovered.RecoveryDiagnostics);
        PdfPageTree repaired = PdfPageTree.Read(recovered);
        Assert.Equal(page, repaired.Pages[0].Reference.ObjectNumber);
        Assert.IsType<PdfDictionary>(recovered.Resolve(repaired.CatalogReference));
        Assert.IsType<PdfDictionary>(recovered.Resolve(repaired.RootReference));
        Assert.Equal(expected, Pixels(PdfDocument.Open(PdfDocumentWriter.Write(recovered))));
        var update = new PdfIncrementalUpdateBuilder(recovered);
        var added = update.AddObject(new PdfInteger(42));
        byte[] saved = update.Build();
        Assert.Equal(damaged, saved.AsSpan(0, damaged.Length).ToArray());
        PdfDocument reopened = PdfDocument.OpenWithCompatibilityRecovery(saved);
        Assert.Equal(42, Assert.IsType<PdfInteger>(reopened.Resolve(added)).Value);
        Assert.Equal(expected, Pixels(reopened));
        Assert.Empty(reopened.RecoveryDiagnostics);
        var editor = new PdfIncrementalPageEditor(recovered);
        editor.RotateClockwise(0).RotateClockwise(0).RotateClockwise(0).RotateClockwise(0);
        PdfDocument edited = PdfDocument.OpenWithCompatibilityRecovery(editor.Build());
        Assert.Equal(expected, Pixels(edited));
        Assert.Empty(edited.RecoveryDiagnostics);

        string DeleteObject(int number) => ChangeObject(number, value => new string(' ', value.Length));
        string ChangeObject(int number, Func<string, string> change) => Regex.Replace(source,
            $@"(?m)^{number} 0 obj\b[\s\S]*?endobj", match => change(match.Value));
    }

    [Fact]
    public void EmptyTreeDoesNotResurrectAnOrphanPage()
    {
        var original = PdfDocument.Open(new PdfDocumentBuilder().AddPage(32, 32, Array.Empty<byte>()).Build());
        PdfPageTree tree = PdfPageTree.Read(original);
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(tree.RootReference.ObjectNumber, Dictionary(
            ("Type", Name("Pages")), ("Count", new PdfInteger(0)), ("Kids", new PdfArray([]))));
        var document = PdfDocument.OpenWithCompatibilityRecovery(update.Build());
        Assert.Empty(PdfPageTree.Read(document).Pages);
        Assert.Empty(document.RecoveryDiagnostics);
    }

    [Fact]
    public void MissingPageObjectDoesNotInventContentOrGeometry()
    {
        var original = PdfDocument.Open(new PdfDocumentBuilder().AddPage(32, 32, "0 0 16 16 re f"u8.ToArray()).Build());
        PdfPageTree tree = PdfPageTree.Read(original);
        var update = new PdfIncrementalUpdateBuilder(original);
        update.FreeObject(tree.Pages[0].Reference.ObjectNumber);
        var document = PdfDocument.OpenWithCompatibilityRecovery(update.Build());
        Assert.Empty(PdfPageTree.Read(document, allowCycleRecovery: true).Pages);
        Assert.Empty(document.RecoveryDiagnostics);
    }

    [Fact]
    public void MisnumberedObjectZeroSentinelDoesNotHideIntactReferencedContent()
    {
        // PDF-HUL-140 starts its only xref subsection at one while its first entry is
        // the object-zero sentinel. This is not evidence that object one was deleted.
        var source = new StringBuilder("%PDF-2.0\n1 0 obj (intact) endobj\n");
        int offset = source.Length;
        source.Append("xref\n1 2\n0000000000 65535 f\n0000000009 00000 n\n");
        source.Append($"trailer << /Size 3 /Root 2 0 R >>\nstartxref\n{offset}\n%%EOF\n");
        var document = PdfDocument.OpenWithCompatibilityRecovery(Encoding.ASCII.GetBytes(source.ToString()));
        Assert.Equal("intact"u8.ToArray(), Assert.IsType<PdfString>(
            document.Resolve(new PdfIndirectReference(1, 0))).Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritingBeforeRenderingRecoversStructureBeforeAllocatingNewObjectNumbers(bool incremental)
    {
        byte[] source = new PdfDocumentBuilder().AddPage(32, 32, "1 0 0 rg 4 4 24 24 re f"u8.ToArray()).Build();
        PdfDocument clean = PdfDocument.Open(source);
        int root = PdfPageTree.Read(clean).RootReference.ObjectNumber;
        string damaged = Regex.Replace(Encoding.Latin1.GetString(source),
            $@"(?m)^{root} 0 obj\b[\s\S]*?endobj", match => new string(' ', match.Length));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(Encoding.Latin1.GetBytes(damaged));
        byte[] saved;
        if (incremental)
        {
            var update = new PdfIncrementalUpdateBuilder(document);
            var added = update.AddObject(new PdfInteger(42));
            // Rendering after reserving an object must not create a colliding virtual root.
            Assert.Equal(Pixels(clean), Pixels(document));
            saved = update.Build();
            Assert.Equal(42, Assert.IsType<PdfInteger>(
                PdfDocument.OpenWithCompatibilityRecovery(saved).Resolve(added)).Value);
        }
        else saved = PdfDocumentWriter.Write(document);
        PdfDocument reopened = incremental ? PdfDocument.OpenWithCompatibilityRecovery(saved) : PdfDocument.Open(saved);
        Assert.Equal(Pixels(clean), Pixels(reopened));
        Assert.Empty(reopened.RecoveryDiagnostics);
    }

    private static byte[] Pixels(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(32, 32) { CacheResult = false }).Pixels.ToArray();
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] pairs) =>
        new(pairs.Select(pair => new KeyValuePair<PdfName, PdfObject>(Name(pair.Key), pair.Value)));

    [GeneratedRegex(@"/Pages\s+\d+\s+0\s+R")]
    private static partial Regex PagesLinkRegex();

    [GeneratedRegex(@"/Root\s+\d+\s+0\s+R")]
    private static partial Regex RootLinkRegex();

    [GeneratedRegex(@"/Kids\s*\[[^\]]*\]")]
    private static partial Regex KidsRegex();
}
