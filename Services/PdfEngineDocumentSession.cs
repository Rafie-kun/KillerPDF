using KillerPdf.Engine.Documents;
using System.IO;

namespace KillerPDF.Services;

/// <summary>Immutable engine view of the active serialized working document.</summary>
internal sealed class PdfEngineDocumentSession
{
    private readonly Lazy<PdfLinkReader> _linkReader;

    private PdfEngineDocumentSession(string path, PdfDocument document,
        IReadOnlyList<PdfPageInformation> pages)
    {
        Path = path;
        Document = document;
        Pages = pages;
        _linkReader = new Lazy<PdfLinkReader>(() => new PdfLinkReader(document));
    }

    internal string Path { get; }
    internal PdfDocument Document { get; }
    internal IReadOnlyList<PdfPageInformation> Pages { get; }
    internal PdfLinkReader LinkReader => _linkReader.Value;
    internal int PageCount => Pages.Count;

    internal static PdfEngineDocumentSession Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Same recovery as the working document, so a file that opened is never rejected here.
        PdfDocument document = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(path));
        return new PdfEngineDocumentSession(path, document,
            PdfPageInformation.Read(document));
    }

    /// <summary>Captures effective visual rotations without consulting the mutable legacy model.</summary>
    internal void CaptureRotations(Dictionary<int, int> rotations)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        bool completeApplicationState = rotations.Count == Pages.Count
            && Enumerable.Range(0, Pages.Count).All(rotations.ContainsKey);
        if (completeApplicationState) return;
        rotations.Clear();
        for (int index = 0; index < Pages.Count; index++)
            rotations[index] = Pages[index].Rotation;
    }

    private readonly Dictionary<int, (int Width, int Height)?> _pageImagePixels = [];

    /// <summary>The pixels of the one image that fills a page, turned as the page is shown, or null
    /// for any other page. Reading a page costs more than a footer update should, so each is read once.</summary>
    internal (int Width, int Height)? PageImagePixels(int pageIndex, IReadOnlyDictionary<int, int>? rotations = null)
    {
        if (!_pageImagePixels.TryGetValue(pageIndex, out (int Width, int Height)? pixels))
        {
            try { pixels = PdfPageRasterInformation.ReadPageImageSize(Document, pageIndex); }
            catch (Exception error) when (error is not OutOfMemoryException) { pixels = null; }
            _pageImagePixels[pageIndex] = pixels;
        }
        if (pixels is not { } size) return null;
        var (width, height) = VisualPageSize(pageIndex, rotations);
        return OutputPixelDimensions.Oriented(size, width, height);
    }

    internal (double Width, double Height) VisualPageSize(
        int pageIndex, IReadOnlyDictionary<int, int>? rotations = null)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageInformation page = Pages[pageIndex];
        int rotation = rotations?.TryGetValue(pageIndex, out int stored) == true
            ? ((stored % 360) + 360) % 360 : page.Rotation;
        return rotation is 90 or 270
            ? (page.Height, page.Width) : (page.Width, page.Height);
    }
}
