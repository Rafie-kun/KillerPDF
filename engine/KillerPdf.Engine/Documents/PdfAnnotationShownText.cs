using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>
/// Text an annotation shows that its value and contents do not hold: a button's caption and whatever
/// its appearance draws, such as the name on a signature or the wording of a stamp.
/// </summary>
internal sealed record PdfAnnotationShownText(int AnnotationIndex, bool IsWidget, PdfContentBounds? Rectangle, string Text);

internal static class PdfAnnotationShownTextReader
{
    /// <summary>
    /// Reads every annotation on one page that shows text. An annotation that cannot be read is skipped,
    /// since the value and contents searches report the page when its data is the part that is damaged.
    /// </summary>
    public static IReadOnlyList<PdfAnnotationShownText> ReadPage(PdfDocument document, PdfPageContentReader reader,
        PdfPageTree tree, int pageIndex, CancellationToken cancellationToken)
    {
        var result = new List<PdfAnnotationShownText>();
        PdfDictionary page = tree.Pages[pageIndex].Dictionary;
        if (!page.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)
            || Resolve(document, annotationsValue) is not PdfArray annotations) return result;
        for (int index = 0; index < annotations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Resolve(document, annotations[index]) is not PdfDictionary annotation) continue;
                var texts = new List<string>();
                if (annotation.TryGetValue(Name("MK"), out PdfObject? captionsValue)
                    && Resolve(document, captionsValue) is PdfDictionary captions)
                {
                    foreach (string key in new[] { "CA", "RC", "AC" })
                        if (captions.TryGetValue(Name(key), out PdfObject? caption)
                            && Resolve(document, caption) is PdfString text)
                            texts.Add(PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "A widget caption"));
                }
                if (Appearance(document, annotation) is { } appearance)
                    texts.Add(string.Join(' ', reader.ReadAppearance(pageIndex, appearance, cancellationToken).Words.Select(word => word.Text)));
                texts.RemoveAll(string.IsNullOrWhiteSpace);
                if (texts.Count == 0) continue;
                bool widget = annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
                    && Resolve(document, subtypeValue) is PdfName subtype && subtype.ValueAsLatin1() == "Widget";
                result.Add(new PdfAnnotationShownText(index, widget,
                    PdfCommentReader.Bounds(document, annotation, pageIndex, index), string.Join('\n', texts)));
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException or NotSupportedException
                or ArgumentException or OverflowException or KeyNotFoundException or PdfFilterException)
            {
            }
        }
        return result;
    }

    // The normal appearance, which is one stream, or one for each state with /AS naming the state shown.
    private static PdfStream? Appearance(PdfDocument document, PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(Name("AP"), out PdfObject? appearancesValue)
            || Resolve(document, appearancesValue) is not PdfDictionary appearances
            || !appearances.TryGetValue(Name("N"), out PdfObject? normalValue)) return null;
        PdfObject normal = Resolve(document, normalValue);
        if (normal is PdfStream stream) return stream;
        return normal is PdfDictionary states
            && annotation.TryGetValue(Name("AS"), out PdfObject? stateValue)
            && Resolve(document, stateValue) is PdfName state
            && states.TryGetValue(state, out PdfObject? shownValue)
            ? Resolve(document, shownValue) as PdfStream : null;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
