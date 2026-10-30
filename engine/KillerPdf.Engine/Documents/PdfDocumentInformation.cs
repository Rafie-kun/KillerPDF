using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
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
        // hiding the rest. An /Info that is not a dictionary carries no values at all.
        PdfDictionary? info = document.Trailer.TryGetValue(new PdfName("Info"u8), out PdfObject? value)
            ? Resolve(document, value) as PdfDictionary : null;

        PdfPageTree pageTree = PdfPageTree.Read(document);
        // PDF 2.0 deprecates /Info, so a field missing there is read from the XMP packet.
        PdfXmpDocumentMetadata? xmp;
        try
        {
            xmp = PdfXmpDocumentMetadata.Read(document);
        }
        catch (Exception error) when (error is InvalidOperationException or PdfFilterException)
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
            if (!readable && source is not null
                && source.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out PdfObject? raw)
                && Resolve(document, raw) is not PdfNull)
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
            ? DecodedText(Resolve(document, value)) : null;

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

    private static DateTimeOffset? Date(PdfDocument document, PdfDictionary? info, string key)
    {
        string? value = Text(document, info, key);
        if (value is null) return null;
        try
        {
            if (!value.StartsWith("D:", StringComparison.Ordinal) || value.Length < 6)
                throw new FormatException();
            string digits = new([.. value.Skip(2).TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length is not (4 or 6 or 8 or 10 or 12 or 14)) throw new FormatException();
            int Part(int offset, int length, int fallback) => digits.Length >= offset + length
                ? int.Parse(digits.AsSpan(offset, length), CultureInfo.InvariantCulture) : fallback;
            int year = Part(0, 4, 1), month = Part(4, 2, 1), day = Part(6, 2, 1);
            int hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
            string suffix = value[(2 + digits.Length)..];
            TimeSpan offset = TimeSpan.Zero;
            if (suffix.Length > 0 && suffix != "Z")
            {
                char sign = suffix[0];
                // ISO 32000-1 7.9.4 lets Z carry an HH'mm offset too (Adobe signers write Z00'00');
                // Z means universal time either way.
                if (sign is not ('+' or '-' or 'Z')) throw new FormatException();
                string compact = suffix[1..].Replace("'", string.Empty, StringComparison.Ordinal);
                // The minutes are optional in ISO 32000, so +01 is an hour-only offset.
                if (compact.Length is not (2 or 4) || !int.TryParse(compact[..2], out int oh)) throw new FormatException();
                int om = 0;
                if (compact.Length == 4 && !int.TryParse(compact[2..], out om)) throw new FormatException();
                offset = sign == 'Z' ? TimeSpan.Zero : new TimeSpan(oh, om, 0) * (sign == '-' ? -1 : 1);
            }
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static PdfTrappedStatus? ReadTrapped(PdfDocument document, PdfDictionary? info)
    {
        if (info is null || !info.TryGetValue(new PdfName("Trapped"u8), out PdfObject? value)) return null;
        // A null entry carries no value and means the same as an absent entry, as does an undefined one.
        return Resolve(document, value) is PdfName name && Enum.TryParse(name.ValueAsLatin1(), out PdfTrappedStatus result)
            ? result : null;
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
            ? DecodedText(Resolve(document, value)) : null;
}
