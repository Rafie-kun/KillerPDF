using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text.Json;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfBookmarkReaderTests
{
    [Fact]
    public void Read_PreservesHierarchyPresentationDestinationsAndIdentity()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBlankPage()
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                IsOpen = false,
                Style = PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic,
                Color = new PdfRgbColor(0.1, 0.3, 0.8),
                Destination = PdfDestination.At(72, 700, 1.25)
            })
            .AddBookmark("Résumé", 1, 1, new PdfBookmarkOptions
            {
                Destination = PdfDestination.FitWidth(640)
            })
            .AddBookmark("Next", 2)
            .Build());

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal(2, bookmarks.Count);
        PdfBookmarkInfo chapter = bookmarks[0];
        Assert.True(chapter.ObjectNumber > 0);
        Assert.Equal(0, chapter.Generation);
        Assert.Equal("Chapter", chapter.Title);
        Assert.False(chapter.IsOpen);
        Assert.Equal(PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic, chapter.Style);
        Assert.Equal(new PdfRgbColor(0.1, 0.3, 0.8), chapter.Color);
        Assert.Equal(0, chapter.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, chapter.Destination!.Kind);
        Assert.Equal([72, 700, 1.25], chapter.Destination.Values);
        PdfBookmarkInfo child = Assert.Single(chapter.Children);
        Assert.Equal("Résumé", child.Title);
        Assert.Equal(1, child.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.FitH, child.Destination!.Kind);
        Assert.Equal([640], child.Destination.Values);
        Assert.Empty(bookmarks[1].Children);
        Assert.NotEqual(chapter.ObjectNumber, child.ObjectNumber);
    }

    [Fact]
    public void Read_ResolvesUnicodeNamedDestinationAndRetainsItsName()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddNamedDestination("résumé", 1, PdfDestination.FitBoundingBoxWidth(720))
            .AddNamedDestinationBookmark("Résumé", "résumé")
            .Build());

        PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(document));

        Assert.Equal("résumé", bookmark.NamedDestination);
        Assert.Equal(1, bookmark.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.FitBH, bookmark.Destination!.Kind);
        Assert.Equal([720], bookmark.Destination.Values);
    }

    [Fact]
    public void Read_TreatsExplicitZeroXyzZoomAsRetainCurrentZoom()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBookmark("Typst heading", 0, options: new PdfBookmarkOptions
            {
                Destination = PdfDestination.At(70, 781, 1)
            })
            .Build();
        source = ReplaceAscii(source, "/XYZ 70 781 1", "/XYZ 70 781 0");

        PdfBookmarkInfo bookmark = Assert.Single(
            PdfBookmarkReader.Read(PdfDocument.Open(source)));

        Assert.Equal(0, bookmark.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, bookmark.Destination!.Kind);
        Assert.Equal([70, 781, null], bookmark.Destination.Values);
    }

    [Theory]
    [InlineData("/FitR 640")]
    [InlineData("/FitH /Nm")]
    [InlineData("/Bogus 64")]
    public void Read_KeepsTheOutlineWhenOneBookmarkHasAMalformedView(string malformed)
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBookmark("Broken", 0, options: new PdfBookmarkOptions { Destination = PdfDestination.FitWidth(640) })
            .AddBookmark("Fine", 0, options: new PdfBookmarkOptions { Destination = PdfDestination.At(72, 700, 1.25) })
            .Build();
        source = ReplaceAscii(source, "/FitH 640", malformed);

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(source));

        Assert.Equal(["Broken", "Fine"], bookmarks.Select(item => item.Title));
        Assert.Null(bookmarks[0].Destination);
        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, bookmarks[1].Destination!.Kind);
    }

    [Fact]
    public void Read_KeepsTheOutlineWhenABookmarkDestinationReferencesItself()
    {
        byte[] source = TwoBookmarks();
        source = ChangeFirstBookmark(source, (update, item) =>
        {
            PdfIndirectReference loop = update.ReserveObject();
            update.SetObject(loop, loop);
            return Replace(item, "Dest", loop);
        });

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(source));

        Assert.Equal(["Broken", "Fine"], bookmarks.Select(item => item.Title));
        AssertNoDestination(bookmarks[0]);
        Assert.Equal(0, bookmarks[1].DestinationPageIndex);
    }

    [Theory]
    [InlineData(PdfStringForm.Hexadecimal)]
    [InlineData(PdfStringForm.Literal)]
    public void Read_KeepsTheOutlineWhenABookmarkDestinationHasBadUtf16(PdfStringForm form)
    {
        byte[] source = ChangeFirstBookmark(TwoBookmarks(), (update, item) =>
            Replace(item, "Dest", new PdfString(new byte[] { 0xFE, 0xFF, 0xD8, 0x00 }, form)));

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(source));

        Assert.Equal(["Broken", "Fine"], bookmarks.Select(item => item.Title));
        AssertNoDestination(bookmarks[0]);
        Assert.Equal(0, bookmarks[1].DestinationPageIndex);
    }

    [Fact]
    public void Read_KeepsTheOutlineWhenAGoToActionDestinationReferencesItself()
    {
        byte[] source = ChangeFirstBookmark(TwoBookmarks(), (update, item) =>
        {
            PdfIndirectReference loop = update.ReserveObject();
            update.SetObject(loop, loop);
            return Replace(new PdfDictionary(item.Where(entry => !entry.Key.Equals(Name("Dest")))), "A",
                new PdfDictionary([
                    new(Name("S"), Name("GoTo")),
                    new(Name("D"), loop)]));
        });

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(source));

        Assert.Equal(["Broken", "Fine"], bookmarks.Select(item => item.Title));
        AssertNoDestination(bookmarks[0]);
    }

    [Fact]
    public void Read_SkipsAnUndecodableNamedDestinationKeyAndKeepsTheRest()
    {
        byte[] source = WithNamedDestinationTree(TwoNamedBookmarks(), pageReference =>
        [
            (Literal("good"), Destination(pageReference)),
            (new PdfString(new byte[] { 0xFE, 0xFF, 0xD8, 0x00 }, PdfStringForm.Hexadecimal),
                Destination(pageReference))
        ]);

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(source));

        Assert.Equal(["Good", "Other"], bookmarks.Select(item => item.Title));
        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
        Assert.Equal("good", bookmarks[0].NamedDestination);
        Assert.Null(bookmarks[1].DestinationPageIndex);
        Assert.Equal("other", bookmarks[1].NamedDestination);
    }

    [Fact]
    public void Read_KeepsTheFirstOfTwoNamedDestinationKeysThatDecodeToTheSameName()
    {
        // The byte keys differ, so the name tree accepts both, but they decode to the same text.
        byte[] source = WithNamedDestinationTree(TwoNamedBookmarks(), pageReference =>
        [
            (Literal("good"), Destination(pageReference)),
            (new PdfString(new byte[] { 0xFE, 0xFF, 0, (byte)'g', 0, (byte)'o', 0, (byte)'o', 0, (byte)'d' },
                PdfStringForm.Hexadecimal), Destination(pageReference))
        ]);

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(source));

        Assert.Equal(["Good", "Other"], bookmarks.Select(item => item.Title));
        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
    }

    [Fact]
    public void Read_ReturnsEmptyListWhenDocumentHasNoBookmarks()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Empty(PdfBookmarkReader.Read(document));
    }

    [Fact]
    public void RenamePreservesBookmarkHierarchyAndDestination()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddBookmark("Chapter", 0)
            .AddBookmark("Old title", 1, level: 1)
            .Build());
        PdfBookmarkInfo child = Assert.Single(
            Assert.Single(PdfBookmarkReader.Read(original)).Children);

        PdfDocument changed = PdfDocument.Open(
            PdfBookmarkEditor.Rename(original, child.ObjectNumber, "Résumé"));
        PdfBookmarkInfo renamed = Assert.Single(
            Assert.Single(PdfBookmarkReader.Read(changed)).Children);

        Assert.Equal("Résumé", renamed.Title);
        Assert.Equal(1, renamed.DestinationPageIndex);
        Assert.Equal("Old title", child.Title);
    }

    [Fact]
    public void AppearanceCanBeChangedAndClearedWithoutChangingDestination()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBookmark("Chapter", 0).Build());
        PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(original));

        PdfDocument styled = PdfDocument.Open(PdfBookmarkEditor.SetAppearance(
            original, bookmark.ObjectNumber,
            PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic,
            new PdfRgbColor(0.25, 0.5, 0.75)));
        PdfBookmarkInfo changed = Assert.Single(PdfBookmarkReader.Read(styled));

        Assert.Equal(PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic, changed.Style);
        Assert.Equal(new PdfRgbColor(0.25, 0.5, 0.75), changed.Color);
        Assert.Equal(0, changed.DestinationPageIndex);
        PdfBookmarkInfo cleared = Assert.Single(PdfBookmarkReader.Read(PdfDocument.Open(
            PdfBookmarkEditor.SetAppearance(styled, bookmark.ObjectNumber))));
        Assert.Equal(PdfBookmarkStyle.Regular, cleared.Style);
        Assert.Null(cleared.Color);
    }

    [Fact]
    public void BookmarkJsonRoundTripPreservesHierarchyDestinationsAndAppearance()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                IsOpen = false,
                Style = PdfBookmarkStyle.Bold,
                Color = new PdfRgbColor(0.25, 0.5, 0.75),
                Destination = PdfDestination.At(72, 700, 1.5)
            })
            .AddBookmark("Section", 1, level: 1, options: new PdfBookmarkOptions
            {
                Style = PdfBookmarkStyle.Italic,
                Destination = PdfDestination.FitWidth(640)
            })
            .Build());

        string json = PdfBookmarkExchange.ToJson(source, indented: true);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Chapter", parsed.RootElement.GetProperty("bookmarks")[0]
            .GetProperty("title").GetString());
        PdfDocument target = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBookmark("Old", 0).Build());

        PdfDocument imported = PdfDocument.Open(
            PdfBookmarkExchange.Import(target, json, replaceExisting: true));
        PdfBookmarkInfo chapter = Assert.Single(PdfBookmarkReader.Read(imported));
        PdfBookmarkInfo section = Assert.Single(chapter.Children);

        Assert.Equal("Chapter", chapter.Title);
        Assert.False(chapter.IsOpen);
        Assert.Equal(PdfBookmarkStyle.Bold, chapter.Style);
        Assert.Equal(new PdfRgbColor(0.25, 0.5, 0.75), chapter.Color);
        Assert.Equal(0, chapter.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, chapter.Destination!.Kind);
        Assert.Equal([72, 700, 1.5], chapter.Destination.Values);
        Assert.Equal("Section", section.Title);
        Assert.Equal(1, section.DestinationPageIndex);
        Assert.Equal(PdfBookmarkStyle.Italic, section.Style);
        Assert.Equal(PdfDestinationKind.FitH, section.Destination!.Kind);
        Assert.Equal([640], section.Destination.Values);
    }

    [Fact]
    public void BookmarkImportRejectsUnresolvedAndUnsupportedData()
    {
        PdfDocument target = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        InvalidOperationException unresolved = Assert.Throws<InvalidOperationException>(() =>
            PdfBookmarkExchange.Import(target,
                """{"schemaVersion":1,"bookmarks":[{"title":"Missing","isOpen":true,"style":"regular","children":[]}]}"""));
        Assert.Contains("no resolved page destination", unresolved.Message);
        Assert.Throws<NotSupportedException>(() => PdfBookmarkExchange.Import(target,
            """{"schemaVersion":2,"bookmarks":[]}"""));
    }

    [Fact]
    public void AMalformedNamedDestinationTreeCostsItsNamesNotTheOutline()
    {
        PdfDocument document = PdfDocument.Open(WithNamedDestinationTree(TwoNamedBookmarks(),
            page => [(Literal("good"), Destination(page)), (Literal("good"), Destination(page))]));

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal(["Good", "Other"], bookmarks.Select(bookmark => bookmark.Title));
        Assert.All(bookmarks, AssertNoDestination);
    }

    private static byte[] TwoBookmarks() => new PdfDocumentBuilder()
        .AddBlankPage()
        .AddBookmark("Broken", 0)
        .AddBookmark("Fine", 0)
        .Build();

    private static byte[] TwoNamedBookmarks() => new PdfDocumentBuilder()
        .AddBlankPage()
        .AddNamedDestination("good", 0)
        .AddNamedDestination("other", 0)
        .AddNamedDestinationBookmark("Good", "good")
        .AddNamedDestinationBookmark("Other", "other")
        .Build();

    private static void AssertNoDestination(PdfBookmarkInfo bookmark)
    {
        Assert.Null(bookmark.DestinationPageIndex);
        Assert.Null(bookmark.Destination);
    }

    private static byte[] ChangeFirstBookmark(byte[] source,
        Func<PdfIncrementalUpdateBuilder, PdfDictionary, PdfDictionary> change)
    {
        PdfDocument document = PdfDocument.Open(source);
        PdfBookmarkInfo first = PdfBookmarkReader.Read(document)[0];
        var update = new PdfIncrementalUpdateBuilder(document);
        var item = Assert.IsType<PdfDictionary>(
            document.Resolve(new PdfIndirectReference(first.ObjectNumber, first.Generation)));
        update.ReplaceObject(first.ObjectNumber, change(update, item));
        return update.Build();
    }

    // Replaces the catalog's /Dests name tree with one flat node holding the given keys, in order.
    private static byte[] WithNamedDestinationTree(byte[] source,
        Func<PdfIndirectReference, (PdfString Key, PdfObject Value)[]> entries)
    {
        PdfDocument document = PdfDocument.Open(source);
        PdfIndirectReference page = PdfPageTree.Read(document).Pages[0].Reference;
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(root));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference tree = update.AddObject(new PdfDictionary([
            new(Name("Names"), new PdfArray(entries(page).SelectMany(
                entry => new[] { (PdfObject)entry.Key, entry.Value })))]));
        PdfIndirectReference names = update.AddObject(new PdfDictionary([new(Name("Dests"), tree)]));
        update.ReplaceObject(root.ObjectNumber, Replace(catalog, "Names", names));
        return update.Build();
    }

    private static PdfArray Destination(PdfIndirectReference page) => new([page, Name("Fit")]);

    private static PdfString Literal(string value) =>
        new(Encoding.ASCII.GetBytes(value), PdfStringForm.Literal);

    private static PdfDictionary Replace(PdfDictionary source, string key, PdfObject value) =>
        new(source.Where(entry => !entry.Key.Equals(Name(key))).Append(
            new KeyValuePair<PdfName, PdfObject>(Name(key), value)));

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] ReplaceAscii(byte[] source, string oldValue, string newValue)
    {
        Assert.Equal(oldValue.Length, newValue.Length);
        string text = Encoding.Latin1.GetString(source);
        Assert.Equal(1, text.Split(oldValue).Length - 1);
        return Encoding.Latin1.GetBytes(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }
}
