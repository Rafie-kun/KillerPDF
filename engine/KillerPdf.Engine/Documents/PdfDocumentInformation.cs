using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Documents;

/// <summary>High-level descriptive and structural information read from a PDF document.</summary>
public sealed record PdfDocumentInformation
{
    /// <summary>Gets the document title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets the document author.</summary>
    public string? Author { get; init; }
    /// <summary>Gets the document subject.</summary>
    public string? Subject { get; init; }
    /// <summary>Gets document-search keywords.</summary>
    public string? Keywords { get; init; }
    /// <summary>Gets the application that created the original content.</summary>
    public string? Creator { get; init; }
    /// <summary>Gets the application that produced the PDF.</summary>
    public string? Producer { get; init; }
    /// <summary>Gets the document's primary natural language.</summary>
    public string? Language { get; init; }
    /// <summary>Gets the document creation date.</summary>
    public DateTimeOffset? CreationDate { get; init; }
    /// <summary>Gets the most recent document modification date.</summary>
    public DateTimeOffset? ModificationDate { get; init; }
    /// <summary>Gets the document trapping status.</summary>
    public PdfTrappedStatus? Trapped { get; init; }
    /// <summary>
    /// Gets the descriptive entries that exist in the file but could not be read, such as a bad date or
    /// undecodable text. A save that does not carry these over would delete them.
    /// </summary>
    public IReadOnlyList<string> UnreadableKeys { get; init; } = [];
    /// <summary>Gets the header version.</summary>
    public required PdfVersion Version { get; init; }
    /// <summary>Gets the number of leaf pages in the page tree.</summary>
    public required int PageCount { get; init; }
    /// <summary>Gets the presentation settings applied when the document opens.</summary>
    public required PdfInitialView InitialView { get; init; }

    /// <summary>Reads document information from an open PDF.</summary>
    public static PdfDocumentInformation Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Each descriptive value is read on its own, so one bad value shows as blank without
        // hiding the rest. An /Info that is not a dictionary, or cannot be parsed, carries no values at all.
        PdfDictionary? info = document.Trailer.TryGetValue(new PdfName("Info"u8), out PdfObject? value)
            ? ResolveOrNull(document, value) as PdfDictionary : null;

        PdfPageTree pageTree = PdfPageTree.Read(document);
        // PDF 2.0 deprecates /Info, so a field missing there is read from the XMP packet.
        PdfXmpDocumentMetadata? xmp;
        try
        {
            xmp = PdfXmpDocumentMetadata.Read(document);
        }
        catch (Exception error) when (PdfReaderDamage.Is(error))
        {
            // Optional, damaged metadata must not prevent access to the document's pages or /Info.
            xmp = null;
        }
        string? title = Text(document, info, "Title") ?? xmp?.Title;
        string? author = Text(document, info, "Author") ?? xmp?.Author;
        string? subject = Text(document, info, "Subject") ?? xmp?.Subject;
        string? keywords = Text(document, info, "Keywords") ?? xmp?.Keywords;
        string? creator = Text(document, info, "Creator") ?? xmp?.Creator;
        string? producer = Text(document, info, "Producer") ?? xmp?.Producer;
        string? language = CatalogText(document, pageTree.Catalog, "Lang");
        DateTimeOffset? creationDate = Date(document, info, "CreationDate") ?? xmp?.CreationDate;
        DateTimeOffset? modificationDate = Date(document, info, "ModDate") ?? xmp?.ModificationDate;
        PdfTrappedStatus? trapped = ReadTrapped(document, info) ?? xmp?.Trapped;
        // An entry that is present but read as nothing is unreadable, which differs from an absent one.
        var unreadable = new List<string>();
        void Check(string key, bool readable, PdfDictionary? source)
        {
            if (readable || source is null
                || !source.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out PdfObject? raw)) return;
            try
            {
                if (Resolve(document, raw) is PdfNull) return;
            }
            catch (Exception error) when (PdfReaderDamage.Is(error))
            {
                // An object that cannot be parsed is present but unreadable.
            }
            unreadable.Add(key);
        }
        Check("Title", title is not null, info);
        Check("Author", author is not null, info);
        Check("Subject", subject is not null, info);
        Check("Keywords", keywords is not null, info);
        Check("Creator", creator is not null, info);
        Check("Producer", producer is not null, info);
        Check("CreationDate", creationDate.HasValue, info);
        Check("ModDate", modificationDate.HasValue, info);
        Check("Trapped", trapped.HasValue, info);
        Check("Lang", language is not null, pageTree.Catalog);
        return new PdfDocumentInformation
        {
            Title = title,
            Author = author,
            Subject = subject,
            Keywords = keywords,
            Creator = creator,
            Producer = producer,
            Language = language,
            CreationDate = creationDate,
            ModificationDate = modificationDate,
            Trapped = trapped,
            UnreadableKeys = Array.AsReadOnly(unreadable.ToArray()),
            Version = document.Header.Version,
            PageCount = pageTree.Pages.Count,
            InitialView = PdfInitialView.Read(document, pageTree)
        };
    }

    // Null, a dangling reference and a non-string all mean the same as an absent entry.
    private static string? CatalogText(PdfDocument document, PdfDictionary catalog, string key) =>
        catalog.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out PdfObject? value)
            ? DecodedText(ResolveOrNull(document, value)) : null;

    private static string? DecodedText(PdfObject value)
    {
        if (value is not PdfString text) return null;
        try
        {
            return PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "A document text value");
        }
        catch (InvalidOperationException)
        {
            // Bad UTF-16 reads as blank rather than stopping the whole read.
            return null;
        }
    }

    private static DateTimeOffset? Date(PdfDocument document, PdfDictionary? info, string key) =>
        PdfDateText.TryParse(Text(document, info, key), out DateTimeOffset date) ? date : null;

    private static PdfTrappedStatus? ReadTrapped(PdfDocument document, PdfDictionary? info)
    {
        if (info is null || !info.TryGetValue(new PdfName("Trapped"u8), out PdfObject? value)) return null;
        // A null entry carries no value and means the same as an absent entry, as does an undefined one.
        // Only the three names count: Enum.TryParse would also take numeric text such as /7 or /1.
        return ResolveOrNull(document, value) is PdfName name
            ? name.ValueAsLatin1() switch
            {
                "True" => PdfTrappedStatus.True,
                "False" => PdfTrappedStatus.False,
                "Unknown" => PdfTrappedStatus.Unknown,
                _ => null
            }
            : null;
    }

    // An object that cannot be parsed reads as null, the same as a dangling reference.
    private static PdfObject ResolveOrNull(PdfDocument document, PdfObject value)
    {
        try
        {
            return Resolve(document, value);
        }
        catch (Exception error) when (PdfReaderDamage.Is(error))
        {
            return PdfNull.Instance;
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation))) return PdfNull.Instance;
            value = document.Resolve(reference);
        }
        return value;
    }

    private static string? Text(PdfDocument document, PdfDictionary? info, string key) =>
        info is not null && info.TryGetValue(new PdfName(System.Text.Encoding.ASCII.GetBytes(key)), out PdfObject? value)
            ? DecodedText(ResolveOrNull(document, value)) : null;
}
