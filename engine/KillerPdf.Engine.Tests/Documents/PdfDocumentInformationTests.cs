using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentInformationTests
{
    [Theory]
    // ISO 32000 defines a zero zoom as null: keep the viewer's current zoom.
    [InlineData("/XYZ null null 0", PdfDestinationKind.Xyz)]
    [InlineData("/XYZ 10 20 -1", PdfDestinationKind.Xyz)]
    [InlineData("/FitR 200 200 100 100", null)]
    public void AnOpenActionWithAMalformedViewDoesNotStopTheDocumentBeingRead(string view, PdfDestinationKind? kind)
    {
        string[] objects = [$"<< /Type /Catalog /Pages 2 0 R /OpenAction [3 0 R {view}] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>"];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        PdfInitialView view2 = PdfDocumentInformation.Read(
            PdfDocument.Open(System.Text.Encoding.ASCII.GetBytes(pdf.ToString()))).InitialView;

        Assert.Equal(0, view2.PageIndex);
        Assert.Equal(kind, view2.Destination?.Kind);
        if (kind is not null) Assert.Null(view2.Destination!.Values[2]);
    }

    [Theory]
    [InlineData("FlateDecode")]
    [InlineData("UnsupportedFilter")]
    public void Read_PreservesInfoAndPagesWhenOptionalXmpCannotBeDecoded(string filter)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Valid title", Author = "Valid author" })
            .AddBlankPage().Build());
        var root = (PdfIndirectReference)source.Trailer[new PdfName("Root"u8)];
        var catalog = (PdfDictionary)source.Resolve(root);
        var update = new PdfIncrementalUpdateBuilder(source);
        var metadataName = new PdfName("Metadata"u8);
        PdfIndirectReference metadata = update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Type"u8), new PdfName("Metadata"u8)),
            new(new PdfName("Subtype"u8), new PdfName("XML"u8)),
            new(new PdfName("Filter"u8), new PdfName(System.Text.Encoding.ASCII.GetBytes(filter)))
        ]), "broken"u8));
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(
            catalog.Where(entry => !entry.Key.Equals(metadataName))
                .Append(new(metadataName, metadata))));

        PdfDocumentInformation information = PdfDocumentInformation.Read(
            PdfDocument.Open(update.Build()));

        Assert.Equal("Valid title", information.Title);
        Assert.Equal("Valid author", information.Author);
        Assert.Equal(1, information.PageCount);
        Assert.NotNull(information.InitialView);
    }

    [Fact]
    public void ReadTreatsANullTrappedEntryAsAbsent()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>",
            "<< /Title (Report) /Trapped null >>"
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(System.Text.Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = System.Text.Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R /Info 4 0 R >>\n"
            + $"startxref\n{xref}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(
            System.Text.Encoding.Latin1.GetBytes(pdf.ToString()));

        PdfDocumentInformation information = PdfDocumentInformation.Read(document);

        Assert.Null(information.Trapped);
        Assert.Equal("Report", information.Title);
    }

    [Theory]
    [InlineData("D:20140724223756Z00'00'")]
    [InlineData("D:20140724223756Z00'00")]
    [InlineData("D:20140724223756Z")]
    public void Read_TreatsZWithAnOffsetAsUniversalTime(string date)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(new PdfName("ModDate"u8.ToArray()),
                new PdfString(System.Text.Encoding.Latin1.GetBytes(date), PdfStringForm.Literal))
        ]));

        PdfDocumentInformation info = PdfDocumentInformation.Read(
            PdfDocument.Open(update.SetDocumentInformation(information).Build()));

        Assert.Equal(new DateTimeOffset(2014, 7, 24, 22, 37, 56, TimeSpan.Zero), info.ModificationDate);
    }

    [Fact]
    public void Read_ReturnsMetadataVersionAndPageCount()
    {
        byte[] bytes = new PdfDocumentBuilder(PdfVersion.Pdf20)
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Technical overview",
                Author = "Steve",
                Subject = "The KillerPDF.Engine",
                Keywords = "PDF 2.0, PDF/A",
                Creator = "Tests",
                Producer = "The KillerPDF.Engine",
                Language = "en-US",
                CreationDate = new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)),
                ModificationDate = new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero),
                Trapped = PdfTrappedStatus.False
            })
            .SetPageLayout(PdfPageLayout.TwoColumnRight)
            .SetPageMode(PdfPageMode.UseOutlines)
            .SetViewerPreferences(new PdfViewerPreferences
            {
                HideToolbar = true,
                HideMenuBar = true,
                FitWindow = true,
                DisplayDocumentTitle = true
            })
            .AddBlankPage()
            .AddBlankPage()
            .SetOpenAction(1, PdfDestination.FitWidth(720))
            .Build();

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(bytes));

        Assert.Equal("Technical overview", info.Title);
        Assert.Equal("Steve", info.Author);
        Assert.Equal("The KillerPDF.Engine", info.Subject);
        Assert.Equal("PDF 2.0, PDF/A", info.Keywords);
        Assert.Equal("Tests", info.Creator);
        Assert.Equal("The KillerPDF.Engine", info.Producer);
        Assert.Equal("en-US", info.Language);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)), info.CreationDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero), info.ModificationDate);
        Assert.Equal(PdfTrappedStatus.False, info.Trapped);
        Assert.Equal(PdfVersion.Pdf20, info.Version);
        Assert.Equal(2, info.PageCount);
        Assert.Equal(PdfPageLayout.TwoColumnRight, info.InitialView.PageLayout);
        Assert.Equal(PdfPageMode.UseOutlines, info.InitialView.PageMode);
        Assert.True(info.InitialView.ViewerPreferences.HideToolbar);
        Assert.True(info.InitialView.ViewerPreferences.HideMenuBar);
        Assert.True(info.InitialView.ViewerPreferences.FitWindow);
        Assert.True(info.InitialView.ViewerPreferences.DisplayDocumentTitle);
        Assert.Equal(1, info.InitialView.PageIndex);
        Assert.Equal(PdfDestinationKind.FitH, info.InitialView.Destination?.Kind);
        Assert.Equal(720, info.InitialView.Destination?.Values.Single());
    }

    [Fact]
    public void Read_AllowsMissingInformationDictionary()
    {
        byte[] bytes = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(bytes));

        Assert.Null(info.Title);
        Assert.Null(info.Author);
        Assert.Equal(1, info.PageCount);
        Assert.NotNull(info.InitialView);
        Assert.Null(info.InitialView.PageLayout);
        Assert.Null(info.InitialView.PageMode);
        Assert.Null(info.InitialView.PageIndex);
    }

    [Fact]
    public void InitialView_AppliesCompleteSavedSelection()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetPageLayout(PdfPageLayout.OneColumn)
            .SetPageMode(PdfPageMode.UseNone)
            .SetViewerPreferences(new PdfViewerPreferences())
            .AddBlankPage().AddBlankPage()
            .SetOpenAction(0, PdfDestination.FitPage())
            .Build());
        var selection = new PdfInitialView
        {
            PageLayout = PdfPageLayout.TwoPageRight,
            PageMode = PdfPageMode.UseThumbs,
            ViewerPreferences = new PdfViewerPreferences
            {
                HideToolbar = true,
                CenterWindow = true,
                DisplayDocumentTitle = true
            },
            PageIndex = 1,
            Destination = PdfDestination.At(12, 34, 1.5)
        };

        PdfInitialView saved = PdfDocumentInformation.Read(
            PdfDocument.Open(selection.Apply(source))).InitialView;

        Assert.Equal(selection.PageLayout, saved.PageLayout);
        Assert.Equal(selection.PageMode, saved.PageMode);
        Assert.True(saved.ViewerPreferences.HideToolbar);
        Assert.True(saved.ViewerPreferences.CenterWindow);
        Assert.True(saved.ViewerPreferences.DisplayDocumentTitle);
        Assert.Equal(1, saved.PageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, saved.Destination?.Kind);
        Assert.Equal([12d, 34d, 1.5d], saved.Destination?.Values);
    }

    [Theory]
    [InlineData("/OpenAction [3 0 R /XYZ 4 0 R 0 0]")]
    [InlineData("/OpenAction [3 0 R 4 0 R]")]
    [InlineData("/OpenAction <FEFFD800>")]
    [InlineData("/ViewerPreferences null")]
    [InlineData("/ViewerPreferences 99 0 R")]
    [InlineData("/ViewerPreferences 4 0 R")]
    [InlineData("/ViewerPreferences 5")]
    [InlineData("/ViewerPreferences << /HideToolbar 4 0 R /Direction 4 0 R >>")]
    [InlineData("/Lang null")]
    [InlineData("/Lang 99 0 R")]
    [InlineData("/Lang 4 0 R")]
    [InlineData("/Lang 5")]
    [InlineData("/Lang <FEFFD800>")]
    public void AMalformedOpeningViewOrLanguageDoesNotStopTheDocumentBeingRead(string entry)
    {
        // Object 4 refers to itself, so every reference to it is a cycle.
        PdfDocument document = Build($"/Pages 2 0 R {entry}", null, ["4 0 R"]);

        PdfDocumentInformation information = PdfDocumentInformation.Read(document);

        Assert.Equal(1, information.PageCount);
        Assert.Null(information.Language);
        Assert.NotNull(information.InitialView);
        Assert.False(information.InitialView.ViewerPreferences.HideToolbar);
    }

    [Theory]
    [InlineData("/CreationDate (not a date)")]
    [InlineData("/CreationDate (D:20231345)")]
    [InlineData("/CreationDate 5")]
    [InlineData("/CreationDate 4 0 R")]
    [InlineData("/Title 5")]
    [InlineData("/Title <FEFFD800>")]
    [InlineData("/Title 4 0 R")]
    [InlineData("/Trapped /Maybe")]
    public void ABadInformationValueReadsAsBlankAndKeepsTheOtherFields(string entry)
    {
        PdfDocument document = Build("/Pages 2 0 R", $"<< /Author (Ada) /Subject (Notes) {entry} >>", ["4 0 R"]);

        PdfDocumentInformation information = PdfDocumentInformation.Read(document);

        Assert.Equal("Ada", information.Author);
        Assert.Equal("Notes", information.Subject);
        Assert.Null(information.CreationDate);
        Assert.Null(information.Trapped);
        Assert.Equal(1, information.PageCount);
        Assert.Null(information.Title);
    }

    [Fact]
    public void AnInformationEntryThatIsNotADictionaryReadsAsAbsent()
    {
        PdfDocument document = Build("/Pages 2 0 R", "(not a dictionary)", []);

        PdfDocumentInformation information = PdfDocumentInformation.Read(document);

        Assert.Null(information.Title);
        Assert.Equal(1, information.PageCount);
    }

    // Builds a one-page file whose catalog and /Info carry the given raw entries. Objects 1 to 3 are
    // the catalog, page tree and page; the extras follow from 4 and /Info is the object after them.
    private static PdfDocument Build(string catalogEntries, string? info, string[] extras)
    {
        List<string> objects =
        [
            $"<< /Type /Catalog {catalogEntries} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>",
            .. extras
        ];
        if (info is not null) objects.Add(info);
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(System.Text.Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = System.Text.Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        string infoEntry = info is null ? "" : $" /Info {objects.Count} 0 R";
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R{infoEntry} >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(System.Text.Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    [Fact]
    public void InitialView_RejectsAmbiguousOpeningDestinations()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Throws<InvalidOperationException>(() => new PdfInitialView
        {
            NamedDestination = "start",
            PageIndex = 0,
            Destination = PdfDestination.FitPage()
        }.Apply(source));
        Assert.Throws<InvalidOperationException>(() => new PdfInitialView
        {
            PageIndex = 0
        }.Apply(source));
    }
}
