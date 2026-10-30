using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>
/// Finds content that paints with the resources of whatever draws it. Such content names entries its
/// drawer's own stream never mentions, so a resource dictionary that holds any of it must stay whole.
/// </summary>
internal static class PdfInheritedResources
{
    private static readonly PdfName ResourcesName = Name("Resources");

    /// <summary>
    /// True when a Type 3 font, a tiling pattern or a soft-mask group listed in the resources has no
    /// resources of its own, or when an appearance of one of the page's annotations has none. Those
    /// draw with these resources. A dictionary that cannot be read counts, since it cannot be shown
    /// to hold none of them.
    /// </summary>
    /// <param name="document">The document that holds the resources.</param>
    /// <param name="resources">The resource dictionary of a page or form.</param>
    /// <param name="page">The page dictionary when the resources are a page's, for its annotations.</param>
    internal static bool ConsumesPageResources(PdfDocument document, PdfDictionary resources, PdfDictionary? page = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resources);
        try
        {
            return Category(document, resources, "Font").Any(font => IsOpenType3(document, font))
                || Category(document, resources, "ExtGState").Any(state => StateConsumes(document, state))
                || Category(document, resources, "Pattern").Any(pattern => pattern is PdfStream stream && !HasOwnResources(document, stream.Dictionary))
                || page is not null && AppearanceConsumes(document, page);
        }
        catch (Exception error) when (PdfReaderDamage.Is(error))
        {
            return true;
        }
    }

    private static IEnumerable<PdfObject> Category(PdfDocument document, PdfDictionary resources, string category) =>
        resources.TryGetValue(Name(category), out PdfObject? value) && Resolve(document, value) is PdfDictionary items
            ? items.Values.Select(item => Resolve(document, item)) : [];

    private static bool IsOpenType3(PdfDocument document, PdfObject value) =>
        value is PdfDictionary font && font.TryGetValue(Name("Subtype"), out PdfObject? subtype)
        && Resolve(document, subtype) is PdfName name && name.ValueAsLatin1() == "Type3" && !HasOwnResources(document, font);

    private static bool StateConsumes(PdfDocument document, PdfObject value)
    {
        if (value is not PdfDictionary state) return false;
        if (state.TryGetValue(Name("Font"), out PdfObject? fontValue) && Resolve(document, fontValue) is PdfArray { Count: > 0 } font
            && IsOpenType3(document, Resolve(document, font[0]))) return true;
        return state.TryGetValue(Name("SMask"), out PdfObject? maskValue) && Resolve(document, maskValue) is PdfDictionary mask
            && mask.TryGetValue(Name("G"), out PdfObject? groupValue) && Resolve(document, groupValue) is PdfStream group
            && !HasOwnResources(document, group.Dictionary);
    }

    // An annotation can hold one appearance stream for each kind, or a dictionary of them by state.
    private static bool AppearanceConsumes(PdfDocument document, PdfDictionary page)
    {
        if (!page.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)
            || Resolve(document, annotationsValue) is not PdfArray annotations) return false;
        foreach (PdfObject item in annotations)
        {
            if (Resolve(document, item) is not PdfDictionary annotation
                || !annotation.TryGetValue(Name("AP"), out PdfObject? appearancesValue)
                || Resolve(document, appearancesValue) is not PdfDictionary appearances) continue;
            foreach (PdfObject appearance in appearances.Values.Select(value => Resolve(document, value)))
            {
                if (appearance is PdfStream stream && !HasOwnResources(document, stream.Dictionary)) return true;
                if (appearance is PdfDictionary states && states.Values.Any(value =>
                    Resolve(document, value) is PdfStream state && !HasOwnResources(document, state.Dictionary))) return true;
            }
        }
        return false;
    }

    private static bool HasOwnResources(PdfDocument document, PdfDictionary dictionary) =>
        dictionary.TryGetValue(ResourcesName, out PdfObject? value) && Resolve(document, value) is PdfDictionary;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
