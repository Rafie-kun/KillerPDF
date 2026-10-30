using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads native link annotations and resolves their local targets.</summary>
public sealed class PdfLinkReader
{
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName LinkName = Name("Link");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName ActionName = Name("A");
    private static readonly PdfName ActionTypeName = Name("S");
    private static readonly PdfName GoToName = Name("GoTo");
    private static readonly PdfName UriActionName = Name("URI");
    private static readonly PdfName DestinationName = Name("Dest");
    private static readonly PdfName DName = Name("D");
    private static readonly PdfName UriName = Name("URI");
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName NamesName = Name("Names");
    private static readonly PdfName DestsName = Name("Dests");
    private readonly PdfDocument _document;
    private readonly PdfPageTree _tree;
    private readonly Lazy<Dictionary<(int ObjectNumber, int Generation), int>> _pages;
    private readonly Lazy<Dictionary<string, PdfObject>> _named;

    /// <summary>Creates a reader that can reuse the document's page tree.</summary>
    public PdfLinkReader(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _tree = PdfPageTree.Read(document);
        _pages = new Lazy<Dictionary<(int ObjectNumber, int Generation), int>>(() =>
            _tree.Pages.ToDictionary(
                item => (item.Reference.ObjectNumber, item.Reference.Generation),
                item => item.Index));
        _named = new Lazy<Dictionary<string, PdfObject>>(() =>
            ReadNamedDestinations(document, _tree.Catalog));
    }

    /// <summary>Reads the native link annotations on one zero-based page.</summary>
    public static IReadOnlyList<PdfLinkInfo> ReadPage(PdfDocument document, int pageIndex)
        => new PdfLinkReader(document).ReadPage(pageIndex);

    /// <summary>Reads one page while reusing this document's parsed page tree.</summary>
    public IReadOnlyList<PdfLinkInfo> ReadPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfDocument document = _document;
        PdfPageTreeEntry page = _tree.Pages[pageIndex];
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotationsValue)) return [];
        PdfArray annotations = Resolve(document, annotationsValue, "A page /Annots value") as PdfArray
            ?? throw new InvalidOperationException("A page /Annots value is not an array.");
        Dictionary<(int ObjectNumber, int Generation), int> pages = _pages.Value;
        Dictionary<string, PdfObject> named = _named.Value;
        var result = new List<PdfLinkInfo>();
        for (int index = 0; index < annotations.Count; index++)
        {
            // One link that cannot be read costs that link and never the others on the page.
            try
            {
                PdfLinkInfo? link = ReadAnnotation(document, annotations[index], pageIndex, index, pages, named);
                if (link is not null) result.Add(link);
            }
            catch (Exception error) when (PdfReaderDamage.Is(error))
            {
            }
        }
        return result;
    }

    private static PdfLinkInfo? ReadAnnotation(
        PdfDocument document, PdfObject annotationValue, int pageIndex, int index,
        Dictionary<(int ObjectNumber, int Generation), int> pages, Dictionary<string, PdfObject> named)
    {
        PdfObject resolvedAnnotation = Resolve(document, annotationValue, "A page annotation");
        if (resolvedAnnotation is not PdfDictionary annotation
            || !annotation.TryGetValue(SubtypeName, out PdfObject? subtypeValue)
            || Resolve(document, subtypeValue, "An annotation subtype") is not PdfName subtype
            || !subtype.Equals(LinkName)) return null;
        if (!annotation.TryGetValue(RectName, out PdfObject? rectangleValue)
            || Resolve(document, rectangleValue, "A link rectangle") is not PdfArray rectangle
            || rectangle.Count != 4)
            throw new InvalidOperationException("A link annotation has no valid /Rect array.");
        double x1 = Number(document, rectangle[0]);
        double y1 = Number(document, rectangle[1]);
        double x2 = Number(document, rectangle[2]);
        double y2 = Number(document, rectangle[3]);
        int? destinationPage = null;
        string? namedDestination = null;
        string? uri = null;
        PdfObject? destination = null;
        if (annotation.TryGetValue(ActionName, out PdfObject? actionValue)
            && Resolve(document, actionValue, "A link action") is PdfDictionary action
            && action.TryGetValue(ActionTypeName, out PdfObject? actionTypeValue))
        {
            PdfName actionType = Resolve(document, actionTypeValue, "A link action type") as PdfName
                ?? throw new InvalidOperationException("A link action type is not a name.");
            if (actionType.Equals(GoToName) && action.TryGetValue(DName, out PdfObject? actionDestination))
                destination = actionDestination;
            else if (actionType.Equals(UriActionName) && action.TryGetValue(UriName, out PdfObject? uriValue))
                uri = Resolve(document, uriValue, "A link URI") is PdfString uriString
                    ? PdfUnicodeEncoding.DecodeTextString(uriString.Bytes.Span, "A link URI")
                    : throw new InvalidOperationException("A link /URI value is not a string.");
        }
        else if (annotation.TryGetValue(DestinationName, out PdfObject? directDestination))
            destination = directDestination;
        if (destination is not null)
            (destinationPage, namedDestination) = ResolveDestination(
                document, destination, pages, named);
        if (destinationPage is null && namedDestination is null && uri is null) return null;
        string? description = annotation.TryGetValue(ContentsName, out PdfObject? contentsValue)
            && Resolve(document, contentsValue, "A link description") is PdfString contents
                ? PdfUnicodeEncoding.DecodeTextString(
                    contents.Bytes.Span, "A link description")
                : null;
        return new PdfLinkInfo
        {
            PageIndex = pageIndex,
            AnnotationIndex = index,
            ObjectNumber = (annotationValue as PdfIndirectReference)?.ObjectNumber,
            Generation = (annotationValue as PdfIndirectReference)?.Generation,
            Left = Math.Min(x1, x2), Bottom = Math.Min(y1, y2),
            Right = Math.Max(x1, x2), Top = Math.Max(y1, y2),
            DestinationPageIndex = destinationPage,
            NamedDestination = namedDestination,
            Uri = uri,
            Description = description
        };
    }

    /// <summary>Lists the zero-based indexes of the link annotations on one page, readable or not.</summary>
    public static IReadOnlyList<int> ReadLinkAnnotationIndexes(PdfDocument document, int pageIndex)
        => new PdfLinkReader(document).ReadLinkAnnotationIndexes(pageIndex);

    /// <summary>
    /// Lists the zero-based indexes of the link annotations on one page. Only the subtype is checked, so a
    /// link whose rectangle, destination or action cannot be read is listed too.
    /// </summary>
    public IReadOnlyList<int> ReadLinkAnnotationIndexes(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = _tree.Pages[pageIndex];
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotationsValue)) return [];
        PdfArray annotations = Resolve(_document, annotationsValue, "A page /Annots value") as PdfArray
            ?? throw new InvalidOperationException("A page /Annots value is not an array.");
        var result = new List<int>();
        for (int index = 0; index < annotations.Count; index++)
        {
            try
            {
                if (Resolve(_document, annotations[index], "A page annotation") is PdfDictionary annotation
                    && annotation.TryGetValue(SubtypeName, out PdfObject? subtypeValue)
                    && Resolve(_document, subtypeValue, "An annotation subtype") is PdfName subtype
                    && subtype.Equals(LinkName)) result.Add(index);
            }
            catch (Exception error) when (PdfReaderDamage.Is(error))
            {
                // An annotation that cannot be resolved cannot be told apart from a link, so it is not listed.
            }
        }
        return result;
    }

    private static (int? PageIndex, string? Named) ResolveDestination(
        PdfDocument document, PdfObject value, Dictionary<(int ObjectNumber, int Generation), int> pages,
        Dictionary<string, PdfObject> namedDestinations)
    {
        PdfObject resolved = Resolve(document, value, "A link destination");
        string? named = resolved switch
        {
            PdfString text => PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "A named link destination"),
            PdfName name => name.ValueAsLatin1(),
            _ => null
        };
        if (named is not null)
        {
            if (!namedDestinations.TryGetValue(named, out PdfObject? namedValue)) return (null, named);
            try
            {
                resolved = Resolve(document, namedValue, "A named link destination value");
                if (resolved is PdfDictionary dictionary && dictionary.TryGetValue(DName, out PdfObject? d))
                    resolved = Resolve(document, d, "A named link destination /D value");
            }
            catch (Exception error) when (PdfReaderDamage.Is(error))
            {
                // A name whose value cannot be read is a link to nowhere, like a name nothing defines.
                return (null, named);
            }
        }
        if (resolved is not PdfArray array || array.Count == 0) return (null, named);
        int? pageIndex = array[0] switch
        {
            PdfIndirectReference reference when pages.TryGetValue(
                (reference.ObjectNumber, reference.Generation), out int found) => found,
            PdfInteger integer when integer.Value >= 0 && integer.Value < pages.Count => (int)integer.Value,
            _ => null
        };
        return (pageIndex, named);
    }

    private static Dictionary<string, PdfObject> ReadNamedDestinations(
        PdfDocument document, PdfDictionary catalog)
    {
        var result = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        // Named destinations are a lookup: a /Names or /Dests value that cannot be read costs the names
        // it holds and never the links.
        try
        {
            if (catalog.TryGetValue(NamesName, out PdfObject? namesValue)
                && Resolve(document, namesValue, "The catalog /Names value") is PdfDictionary names
                && names.TryGetValue(DestsName, out PdfObject? tree))
                PdfNameTree.ReadDestinations(document, tree, result);
        }
        catch (Exception error) when (PdfReaderDamage.Is(error))
        {
        }
        try
        {
            if (catalog.TryGetValue(DestsName, out PdfObject? legacyValue)
                && Resolve(document, legacyValue, "The catalog /Dests value") is PdfDictionary legacy)
                foreach ((PdfName key, PdfObject destination) in legacy)
                    result.TryAdd(key.ValueAsLatin1(), destination);
        }
        catch (Exception error) when (PdfReaderDamage.Is(error))
        {
        }
        return result;
    }

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value, "A link rectangle coordinate") switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new InvalidOperationException("A link rectangle coordinate is not numeric.")
        };

    private static PdfObject Resolve(PdfDocument document, PdfObject value, string description)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
