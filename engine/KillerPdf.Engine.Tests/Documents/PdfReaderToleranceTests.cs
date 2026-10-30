using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// The readers behind Document Info, the outline, the links, the attachments, the signature panel and
// Initial View read only. Damage in one value must cost that value and never the rest of the read.
public sealed class PdfReaderToleranceTests
{
    // ---- Document Info ----

    [Theory]
    [InlineData("info-garbage")]
    [InlineData("title-garbage")]
    [InlineData("date-garbage")]
    [InlineData("xmp-object-garbage")]
    [InlineData("prefs-garbage")]
    [InlineData("openaction-garbage")]
    [InlineData("lang-garbage")]
    public void DocumentInfo_ReadsWithAGarbledObjectAsBlank(string fixture)
    {
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(InfoFixture(fixture)));

        Assert.Equal(1, info.PageCount);
        switch (fixture)
        {
            case "title-garbage":
                Assert.Null(info.Title);
                Assert.Equal(["Title"], info.UnreadableKeys);
                break;
            case "date-garbage":
                Assert.Null(info.CreationDate);
                Assert.Equal(["CreationDate"], info.UnreadableKeys);
                break;
            case "lang-garbage":
                Assert.Null(info.Language);
                Assert.Equal(["Lang"], info.UnreadableKeys);
                break;
        }
    }

    [Fact]
    public void DocumentInfo_AGarbledValueKeepsItsNeighbours()
    {
        var objects = Base();
        objects.Add("<< /Title 5 0 R /Author (Ann) /CreationDate (D:20240101120000Z) >>");
        objects.Add("(x)");
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(
            Garble(Pdf(objects, "/Info 4 0 R"), 5)));

        Assert.Null(info.Title);
        Assert.Equal("Ann", info.Author);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), info.CreationDate);
        Assert.Equal(["Title"], info.UnreadableKeys);
    }

    [Theory]
    [InlineData("/7")]
    [InlineData("/1")]
    [InlineData("/0")]
    [InlineData("/true")]
    [InlineData("/Bogus")]
    public void DocumentInfo_ReadsOnlyTheThreeTrappedNames(string value)
    {
        var objects = Base();
        objects.Add($"<< /Title (x) /Trapped {value} >>");

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(Pdf(objects, "/Info 4 0 R")));

        Assert.Null(info.Trapped);
        Assert.Equal(["Trapped"], info.UnreadableKeys);
    }

    [Theory]
    [InlineData("/True", PdfTrappedStatus.True)]
    [InlineData("/False", PdfTrappedStatus.False)]
    [InlineData("/Unknown", PdfTrappedStatus.Unknown)]
    public void DocumentInfo_ReadsTheTrappedNames(string value, PdfTrappedStatus expected)
    {
        var objects = Base();
        objects.Add($"<< /Trapped {value} >>");

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(Pdf(objects, "/Info 4 0 R")));

        Assert.Equal(expected, info.Trapped);
        Assert.Empty(info.UnreadableKeys);
    }

    // Offset minutes, or null when the date is unreadable. The same table applies to every reader.
    public static TheoryData<string, int?> DateSuffixes => new()
    {
        { "", 0 },
        { "Z", 0 },
        { "Z'", 0 },
        { "Z00'00'", 0 },
        { "Z0000", 0 },
        { "+01", 60 },
        { "+01'", 60 },
        { "+0100", 60 },
        { "+01'00", 60 },
        { "+01'00'", 60 },
        { "-05'30'", -330 },
        { "-0530", -330 },
        { "+1", null },
        { "+", null },
        { "+'", null },
        { "+01'0", null },
        { "+0x", null },
        { "Z0", null },
        { "X", null },
        { "garbage", null },
    };

    [Theory]
    [MemberData(nameof(DateSuffixes))]
    public void DocumentInfo_ParsesTheDateOffsetForms(string suffix, int? offsetMinutes)
    {
        var objects = Base();
        objects.Add($"<< /CreationDate (D:20240101120000{suffix}) >>");

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(Pdf(objects, "/Info 4 0 R")));

        AssertDate(offsetMinutes, info.CreationDate);
        Assert.Equal(offsetMinutes is null ? ["CreationDate"] : [], info.UnreadableKeys);
    }

    [Theory]
    [MemberData(nameof(DateSuffixes))]
    public void AttachmentReader_ParsesTheDateOffsetFormsInParams(string suffix, int? offsetMinutes)
    {
        byte[] source = AttachmentDocument($"/Params << /ModDate (D:20240101120000{suffix}) >>");

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(source)));

        AssertDate(offsetMinutes, attachment.ModificationDate);
        Assert.Equal("ONE", Encoding.ASCII.GetString(attachment.Data.Span));
    }

    [Theory]
    [MemberData(nameof(DateSuffixes))]
    public void SignatureReader_ParsesTheDateOffsetFormsInM(string suffix, int? offsetMinutes)
    {
        byte[] source = SignatureDocument($"/M (D:20240101120000{suffix})");

        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(PdfDocument.Open(source)));

        Assert.True(signature.IsSigned);
        AssertDate(offsetMinutes, signature.SigningTime);
    }

    [Theory]
    [InlineData("/ModDate 5")]
    [InlineData("/ModDate <FEFFD800>")]
    [InlineData("/CreationDate (garbage) /ModDate (D:20240101120000Z)")]
    public void AttachmentReader_AnUnreadableDateCostsThatDateAndNotTheList(string parameters)
    {
        byte[] source = AttachmentDocument($"/Params << {parameters} >>", second: true);

        IReadOnlyList<PdfAttachmentInfo> attachments = PdfAttachmentReader.Read(PdfDocument.Open(source));

        Assert.Equal(["a.txt", "b.txt"], attachments.Select(item => item.FileName));
        Assert.Null(attachments[0].CreationDate);
        Assert.Equal("ONE", Encoding.ASCII.GetString(attachments[0].Data.Span));
    }

    // ---- Outline and links ----

    public static TheoryData<string> Fixtures => new()
    {
        "control",
        "dest-garbage-object",
        "action-garbage-object",
        "names-selfref",
        "names-garbage",
        "dests-tree-garbage-node",
        "legacy-dests-selfref",
        "legacy-dests-garbage",
        "named-value-selfref",
        "named-value-garbage",
        "outline-bad-count",
        "outline-bad-flag",
        "outline-bad-color",
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Bookmarks_ReadTheItemWhateverIsWrongWithOneValue(string fixture)
    {
        PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(PdfDocument.Open(BookFixture(fixture))));

        Assert.Equal("One", bookmark.Title);
        if (fixture is "control" or "outline-bad-count" or "outline-bad-flag" or "outline-bad-color")
            Assert.Equal(0, bookmark.DestinationPageIndex);
        else
            Assert.Null(bookmark.DestinationPageIndex);
        if (fixture.StartsWith("outline-bad", StringComparison.Ordinal))
        {
            Assert.Equal(PdfBookmarkStyle.Regular, bookmark.Style);
            Assert.Null(bookmark.Color);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Links_ReadThePageWhateverIsWrongWithTheNameTables(string fixture)
    {
        PdfLinkInfo link = Assert.Single(PdfLinkReader.ReadPage(PdfDocument.Open(BookFixture(fixture)), 0));

        Assert.Equal(0, link.AnnotationIndex);
    }

    [Fact]
    public void Bookmarks_AnItemWithAnUnreadableColorKeepsItsOtherFields()
    {
        PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(PdfDocument.Open(
            BookFixture("outline-bad-color"))));

        Assert.Equal(0, bookmark.DestinationPageIndex);
        Assert.Equal("One", bookmark.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("99 0 R")]
    public void Bookmarks_ANullOrDanglingOutlinesEntryMeansNoOutline(string outlines)
    {
        var objects = Base(outlines.Length == 0 ? "/Outlines null" : $"/Outlines {outlines}");

        Assert.Empty(PdfBookmarkReader.Read(PdfDocument.Open(Pdf(objects))));
    }

    [Fact]
    public void Bookmarks_AnOutlinesEntryThatIsNotADictionaryStaysAnError()
    {
        var objects = Base("/Outlines [1 2]");

        Assert.Throws<InvalidOperationException>(() => PdfBookmarkReader.Read(PdfDocument.Open(Pdf(objects))));
    }

    [Theory]
    [InlineData("link-dest-garbage-object")]
    [InlineData("link-action-garbage")]
    [InlineData("link-dest-selfref")]
    [InlineData("link-dest-badutf16")]
    [InlineData("link-action-d-badutf16")]
    [InlineData("link-action-uri-badutf16")]
    [InlineData("link-action-uri-number")]
    [InlineData("link-action-s-number")]
    [InlineData("link-rect-bad")]
    public void Links_OneBadAnnotationCostsOnlyThatLink(string fixture)
    {
        PdfLinkInfo link = Assert.Single(PdfLinkReader.ReadPage(PdfDocument.Open(LinkFixture(fixture)), 0));

        Assert.Equal(1, link.AnnotationIndex);
        Assert.Equal("https://example.com/", link.Uri);
    }

    [Theory]
    [InlineData("link-dest-garbage-object")]
    [InlineData("link-action-uri-badutf16")]
    [InlineData("link-rect-bad")]
    public void Links_TheAnnotationIndexesIncludeABadLinkAndSkipTheRest(string fixture)
    {
        // Annotation 0 is the bad link, 1 the good one and 2 is not a link at all.
        PdfDocument document = PdfDocument.Open(LinkFixture(fixture));

        Assert.Equal([0, 1], PdfLinkReader.ReadLinkAnnotationIndexes(document, 0));
        Assert.Equal([0, 1], new PdfLinkReader(document).ReadLinkAnnotationIndexes(0));
    }

    [Fact]
    public void Links_TheAnnotationIndexesRejectAPageOutOfRange()
    {
        PdfDocument document = PdfDocument.Open(LinkFixture("link-rect-bad"));

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfLinkReader.ReadLinkAnnotationIndexes(document, 5));
    }

    // ---- Name trees ----

    [Fact]
    public void Destinations_AnOutOfOrderTreeStillResolvesEveryName()
    {
        // Lookup needs neither the ordering nor /Limits, so a tree that breaks both still works.
        var objects = Doc("<< /Kids [9 0 R] /Limits [(z) (a)] >>",
            "<< /Limits [(z) (a)] /Names [(b) [3 0 R /Fit] (a) [3 0 R /FitH 5]] >>");
        PdfDocument document = PdfDocument.Open(Pdf(objects));

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal([0, 0], bookmarks.Select(item => item.DestinationPageIndex));
        Assert.Equal(PdfDestinationKind.FitH, bookmarks[0].Destination!.Kind);
    }

    [Fact]
    public void Destinations_AnUnreadableNodeLosesThatNodeAndKeepsTheOthers()
    {
        var objects = Doc("<< /Kids [9 0 R 10 0 R 11 0 R] >>",
            "(x)",
            "<< /Names [(a) [3 0 R /Fit] 5 [3 0 R /Fit] (c)] >>",
            "<< /Names [(b) [3 0 R /Fit]] >>");
        PdfDocument document = PdfDocument.Open(Garble(Pdf(objects), 9));

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal([0, 0], bookmarks.Select(item => item.DestinationPageIndex));
    }

    [Fact]
    public void Destinations_ACycleStopsAndKeepsWhatWasReadBeforeIt()
    {
        var objects = Doc("<< /Kids [9 0 R 8 0 R] >>",
            "<< /Names [(a) [3 0 R /Fit]] >>");
        PdfDocument document = PdfDocument.Open(Pdf(objects));

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
        Assert.Null(bookmarks[1].DestinationPageIndex);
    }

    [Fact]
    public void Destinations_ATreeNestedPastTheLimitStopsWithoutFailing()
    {
        // A chain of 300 nodes exceeds the depth limit; the names found before it stay usable.
        var objects = Doc("<< /Names [(a) [3 0 R /Fit]] /Kids [9 0 R] >>");
        for (int node = 9; node < 308; node++) objects.Add($"<< /Kids [{node + 1} 0 R] >>");
        objects.Add("<< /Names [(b) [3 0 R /Fit]] >>");
        PdfDocument document = PdfDocument.Open(Pdf(objects));

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
        Assert.Null(bookmarks[1].DestinationPageIndex);
    }

    [Fact]
    public void Attachments_AnEmptyEmbeddedFilesDictionaryMeansNoAttachments()
    {
        var objects = Base("/Names << /EmbeddedFiles 4 0 R >>");
        objects.Add("<< >>");
        byte[] source = Pdf(objects);

        Assert.Empty(PdfAttachmentReader.Read(PdfDocument.Open(source)));

        byte[] added = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .AddAttachment("x.txt", new byte[] { 65 }, "text/plain").Build();
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(added)));
        Assert.Equal("x.txt", attachment.FileName);
    }

    [Fact]
    public void Attachments_ANodeInsideTheTreeWithNeitherKeyStaysAnError()
    {
        var objects = Base("/Names << /EmbeddedFiles << /Kids [4 0 R] >> >>");
        objects.Add("<< /Limits [(a) (b)] >>");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfAttachmentReader.Read(PdfDocument.Open(Pdf(objects))));

        Assert.Contains("exactly one of /Names or /Kids", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_ARootWithOnlyALimitsEntryStaysAnError()
    {
        var objects = Base("/Names << /EmbeddedFiles << /Limits [(a) (b)] >> >>");

        Assert.Throws<InvalidOperationException>(() =>
            PdfAttachmentReader.Read(PdfDocument.Open(Pdf(objects))));
    }

    // ---- Initial View ----

    [Fact]
    public void InitialView_ANamedOpenActionWithNoDestinationReadsAsNoDestination()
    {
        var objects = Base("/OpenAction (nowhere)");

        PdfInitialView view = PdfDocumentInformation.Read(PdfDocument.Open(Pdf(objects))).InitialView;

        Assert.Null(view.NamedDestination);
        Assert.Null(view.PageIndex);
        // The dialog passes the view back untouched, so it must apply without a destination.
        byte[] saved = view.Apply(PdfDocument.Open(Pdf(objects)));
        Assert.Null(PdfDocumentInformation.Read(PdfDocument.Open(saved)).InitialView.NamedDestination);
    }

    [Theory]
    [InlineData("/Names << /Dests << /Names [(here) [3 0 R /Fit]] >> >>")]
    [InlineData("/Dests << /here [3 0 R /Fit] >>")]
    public void InitialView_ANamedOpenActionWithADestinationKeepsItsName(string destinations)
    {
        var objects = Base($"/OpenAction (here) {destinations}");

        PdfInitialView view = PdfDocumentInformation.Read(PdfDocument.Open(Pdf(objects))).InitialView;

        Assert.Equal("here", view.NamedDestination);
    }

    // ---- Signatures ----

    [Fact]
    public void Signatures_ADictionaryWithNoTypeReadsAsSigned()
    {
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(PdfDocument.Open(
            SignatureDocument("", type: ""))));

        Assert.True(signature.IsSigned);
        Assert.Equal("Sig1", signature.FieldName);
        Assert.Equal("adbe.pkcs7.detached", signature.SubFilter);
    }

    [Theory]
    [InlineData("/Type /Foo")]
    [InlineData("/Type (Sig)")]
    public void Signatures_ATypeThatIsNotSigStaysAnError(string type)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfSignatureReader.Read(PdfDocument.Open(SignatureDocument("", type))));

        Assert.Contains("does not declare /Type /Sig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Signatures_ADocumentTimestampTypeStillReads()
    {
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(PdfDocument.Open(
            SignatureDocument("", "/Type /DocTimeStamp"))));

        Assert.True(signature.IsDocumentTimestamp);
    }

    [Fact]
    public void Signatures_ACertificationSignatureWithNoTypeStillReads()
    {
        const string transform = "/Reference [<< /Type /SigRef /TransformMethod /DocMDP "
            + "/TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>]";
        PdfDocument document = PdfDocument.Open(SignatureDocument(transform, "", "/Perms << /DocMDP 5 0 R >>"));

        Assert.Equal(PdfSignatureCertificationPermission.FormFillingAndSignatures,
            PdfSignatureReader.ReadCertificationPermission(document));
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));
        Assert.True(signature.IsCertificationSignature);
    }

    [Fact]
    public void Signatures_ACertificationSignatureOfAnotherTypeStaysAnError()
    {
        const string transform = "/Reference [<< /Type /SigRef /TransformMethod /DocMDP "
            + "/TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>]";
        PdfDocument document = PdfDocument.Open(SignatureDocument(
            transform, "/Type /Foo", "/Perms << /DocMDP 5 0 R >>"));

        Assert.Throws<InvalidOperationException>(() => PdfSignatureReader.ReadCertificationPermission(document));
    }

    [Theory]
    [InlineData("/Name (a\\177b)")]
    [InlineData("/Name <FEFFD800>")]
    [InlineData("/Name 5")]
    [InlineData("/Name /Signer")]
    public void Signatures_AnUnreadableNameCostsOnlyTheName(string name)
    {
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(PdfDocument.Open(
            SignatureDocument($"{name} /Reason (Approved) /Location (Paris) /ContactInfo (me)"))));

        Assert.True(signature.IsSigned);
        Assert.Null(signature.SignerName);
        Assert.Equal("Approved", signature.Reason);
        Assert.Equal("Paris", signature.Location);
        Assert.Equal("me", signature.ContactInformation);
    }

    [Theory]
    [InlineData("/Reason <FEFFD800>")]
    [InlineData("/Location (a\\177b)")]
    [InlineData("/ContactInfo 5")]
    [InlineData("/M 5")]
    [InlineData("/M (nonsense)")]
    public void Signatures_AnyOneUnreadableTextValueStillReadsTheSignature(string damaged)
    {
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(PdfDocument.Open(
            SignatureDocument($"/Name (Alice) {damaged}"))));

        Assert.True(signature.IsSigned);
        Assert.Equal("Alice", signature.SignerName);
    }

    // ---- Fixtures ----

    private static void AssertDate(int? offsetMinutes, DateTimeOffset? actual)
    {
        if (offsetMinutes is null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes.Value)), actual);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes.Value), actual!.Value.Offset);
    }

    private static List<string> Base(string catalog = "") =>
    [
        $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
    ];

    private static byte[] InfoFixture(string fixture)
    {
        switch (fixture)
        {
            case "info-garbage":
            {
                var objects = Base();
                objects.Add("(x)");
                return Garble(Pdf(objects, "/Info 4 0 R"), 4);
            }
            case "title-garbage":
            {
                var objects = Base();
                objects.Add("<< /Title 5 0 R >>");
                objects.Add("(x)");
                return Garble(Pdf(objects, "/Info 4 0 R"), 5);
            }
            case "date-garbage":
            {
                var objects = Base();
                objects.Add("<< /CreationDate 5 0 R >>");
                objects.Add("(x)");
                return Garble(Pdf(objects, "/Info 4 0 R"), 5);
            }
            case "xmp-object-garbage":
            {
                var objects = Base("/Metadata 4 0 R");
                objects.Add(Stream("<x/>", "/Type /Metadata /Subtype /XML"));
                return Garble(Pdf(objects), 4);
            }
            case "prefs-garbage":
            {
                var objects = Base("/ViewerPreferences 4 0 R");
                objects.Add("<< >>");
                return Garble(Pdf(objects), 4);
            }
            case "openaction-garbage":
            {
                var objects = Base("/OpenAction 4 0 R");
                objects.Add("[3 0 R /Fit]");
                return Garble(Pdf(objects), 4);
            }
            case "lang-garbage":
            {
                var objects = Base("/Lang 4 0 R");
                objects.Add("(en)");
                return Garble(Pdf(objects), 4);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(fixture));
        }
    }

    // Two bookmarks naming "a" and "b" in the catalog's /Dests tree, whose objects (8 onward) are supplied.
    private static List<string> Doc(params string[] tree)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /Names << /Dests 8 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Last 7 0 R /Count 2 >>",
            "<< /Title (A) /Parent 4 0 R /Next 7 0 R /Dest (a) >>",
            "null",
            "<< /Title (B) /Parent 4 0 R /Prev 5 0 R /Dest (b) >>",
        };
        objects.AddRange(tree);
        return objects;
    }

    private static byte[] BookFixture(string fixture)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R CATALOG >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (One) /Parent 4 0 R BOOKMARK >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest NAMED >>",
        };
        string catalog = "", bookmark = "/Dest [3 0 R /Fit]", named = "[3 0 R /Fit]";
        string[] extra = [];
        int garble = 0;
        switch (fixture)
        {
            case "control": break;
            case "dest-garbage-object": bookmark = "/Dest 7 0 R"; extra = ["[3 0 R /Fit]"]; garble = 7; break;
            case "action-garbage-object":
                bookmark = "/A 7 0 R"; extra = ["<< /S /GoTo /D [3 0 R /Fit] >>"]; garble = 7; break;
            case "names-selfref": catalog = "/Names 7 0 R"; bookmark = "/Dest (x)"; named = "(x)"; extra = ["7 0 R"]; break;
            case "names-garbage":
                catalog = "/Names 7 0 R"; bookmark = "/Dest (x)"; named = "(x)";
                extra = ["<< /Dests << /Names [(x) [3 0 R /Fit]] >> >>"]; garble = 7; break;
            case "dests-tree-garbage-node":
                catalog = "/Names << /Dests 7 0 R >>"; bookmark = "/Dest (x)"; named = "(x)";
                extra = ["<< /Names [(x) [3 0 R /Fit]] >>"]; garble = 7; break;
            case "legacy-dests-selfref": catalog = "/Dests 7 0 R"; bookmark = "/Dest /x"; named = "/x"; extra = ["7 0 R"]; break;
            case "legacy-dests-garbage":
                catalog = "/Dests 7 0 R"; bookmark = "/Dest /x"; named = "/x"; extra = ["<< /x [3 0 R /Fit] >>"]; garble = 7; break;
            case "named-value-selfref":
                catalog = "/Dests << /x 7 0 R >>"; bookmark = "/Dest /x"; named = "/x"; extra = ["7 0 R"]; break;
            case "named-value-garbage":
                catalog = "/Dests << /x 7 0 R >>"; bookmark = "/Dest /x"; named = "/x"; extra = ["[3 0 R /Fit]"]; garble = 7; break;
            case "outline-bad-count": bookmark = "/Count (x) /Dest [3 0 R /Fit]"; break;
            case "outline-bad-flag": bookmark = "/F 8 /Dest [3 0 R /Fit]"; break;
            case "outline-bad-color": bookmark = "/C [1 2] /Dest [3 0 R /Fit]"; break;
            default: throw new ArgumentOutOfRangeException(nameof(fixture));
        }
        objects[0] = objects[0].Replace("CATALOG", catalog, StringComparison.Ordinal);
        objects[4] = objects[4].Replace("BOOKMARK", bookmark, StringComparison.Ordinal);
        objects[5] = objects[5].Replace("NAMED", named, StringComparison.Ordinal);
        objects.AddRange(extra);
        byte[] pdf = Pdf(objects);
        return garble == 0 ? pdf : Garble(pdf, garble);
    }

    // Annotation 0 carries the damage, 1 is a good URI link and 2 is a text annotation.
    private static byte[] LinkFixture(string fixture)
    {
        string bad = fixture switch
        {
            "link-dest-garbage-object" => "/Dest 8 0 R",
            "link-action-garbage" => "/A 8 0 R",
            "link-dest-selfref" => "/Dest 8 0 R",
            "link-dest-badutf16" => "/Dest <FEFFD800>",
            "link-action-d-badutf16" => "/A << /S /GoTo /D <FEFFD800> >>",
            "link-action-uri-badutf16" => "/A << /S /URI /URI <FEFFD800> >>",
            "link-action-uri-number" => "/A << /S /URI /URI 5 >>",
            "link-action-s-number" => "/A << /S 5 >>",
            "link-rect-bad" => "/Dest [3 0 R /Fit] /Rect [1 2]",
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R 6 0 R] >>",
            $"<< /Type /Annot /Subtype /Link {(fixture == "link-rect-bad" ? string.Empty : "/Rect [0 0 10 10]")} {bad} >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://example.com/) >> >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (note) >>",
            "null",
            fixture == "link-dest-selfref" ? "8 0 R" : "[3 0 R /Fit]",
        };
        byte[] pdf = Pdf(objects);
        return fixture is "link-dest-garbage-object" or "link-action-garbage" ? Garble(pdf, 8) : pdf;
    }

    private static byte[] AttachmentDocument(string streamExtra, bool second = false)
    {
        var entries = new StringBuilder("(a.txt) 4 0 R");
        var objects = new List<string>
        {
            "PLACEHOLDER",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /EF << /F 5 0 R >> >>",
            Stream("ONE", $"/Type /EmbeddedFile {streamExtra}"),
        };
        if (second)
        {
            entries.Append(" (b.txt) 6 0 R");
            objects.Add("<< /Type /Filespec /F (b.txt) /UF (b.txt) /EF << /F 7 0 R >> >>");
            objects.Add(Stream("TWO", "/Type /EmbeddedFile"));
        }
        objects[0] = $"<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [{entries}] >> >> >>";
        return Pdf(objects);
    }

    private static byte[] SignatureDocument(string signatureExtra, string type = "/Type /Sig",
        string catalog = "")
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /FT /Sig /T (Sig1) /V 5 0 R >>",
            $"<< {type} /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <0000> {signatureExtra} >>",
        };
        return Pdf(objects);
    }

    private static string Stream(string content, string dictionary) =>
        $"<< {dictionary} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] Pdf(List<string> objects, string trailerExtra = "")
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    // Overwrites an object's body with closing parentheses so it cannot parse but every offset stays valid.
    private static byte[] Garble(byte[] source, int objectNumber)
    {
        string text = Encoding.Latin1.GetString(source);
        string header = $"{objectNumber} 0 obj\n";
        int start = text.IndexOf(header, StringComparison.Ordinal);
        int end = text.IndexOf("\nendobj", start, StringComparison.Ordinal);
        var builder = new StringBuilder(text);
        for (int index = start + header.Length; index < end; index++) builder[index] = ')';
        return Encoding.Latin1.GetBytes(builder.ToString());
    }
}
