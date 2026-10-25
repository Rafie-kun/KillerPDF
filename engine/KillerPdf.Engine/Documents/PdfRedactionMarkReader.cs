using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>One redaction mark saved in a page's /Annots array and not yet applied.</summary>
public sealed record PdfRedactionMarkInfo
{
    /// <summary>Gets the zero-based page index.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the zero-based position in the page /Annots array.</summary>
    public required int AnnotationIndex { get; init; }
    /// <summary>Gets the annotation visibility and interaction flags.</summary>
    public long AnnotationFlags { get; init; }
    /// <summary>
    /// Gets the marked areas in default user space, one per QuadPoints quadrilateral, or the
    /// annotation rectangle when the mark has no quadrilaterals.
    /// </summary>
    public required IReadOnlyList<PdfContentBounds> Areas { get; init; }
    /// <summary>Gets the replacement text drawn into the redacted area, when one is set.</summary>
    public string? OverlayText { get; init; }
    /// <summary>Gets the annotation contents.</summary>
    public string? Contents { get; init; }
    /// <summary>Gets the fill color of the applied redaction, when the mark records one.</summary>
    public PdfRgbColor? FillColor { get; init; }
}

/// <summary>Reads the redaction marks saved in a document, the review state before applying.</summary>
public static class PdfRedactionMarkReader
{
    /// <summary>Returns every /Redact annotation in page and annotation order.</summary>
    public static IReadOnlyList<PdfRedactionMarkInfo> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading redaction marks.");
        var marks = new List<PdfRedactionMarkInfo>();
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)
                || Resolve(document, annotationsValue) is not PdfArray annotations)
                continue;
            for (int index = 0; index < annotations.Count; index++)
            {
                if (Resolve(document, annotations[index]) is not PdfDictionary annotation
                    || !annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
                    || Resolve(document, subtypeValue) is not PdfName subtype
                    || subtype.ValueAsLatin1() != "Redact")
                    continue;
                IReadOnlyList<PdfContentBounds> areas = Areas(document, annotation);
                if (areas.Count == 0) continue;
                marks.Add(new PdfRedactionMarkInfo
                {
                    PageIndex = page.Index,
                    AnnotationIndex = index,
                    AnnotationFlags = annotation.TryGetValue(Name("F"), out PdfObject? flagsValue)
                        && Resolve(document, flagsValue) is PdfInteger flags ? flags.Value : 0,
                    Areas = areas,
                    OverlayText = Text(document, annotation, "OverlayText"),
                    Contents = Text(document, annotation, "Contents"),
                    FillColor = Color(document, annotation)
                });
            }
        }
        return Array.AsReadOnly(marks.ToArray());
    }

    private static IReadOnlyList<PdfContentBounds> Areas(PdfDocument document, PdfDictionary annotation)
    {
        var areas = new List<PdfContentBounds>();
        if (annotation.TryGetValue(Name("QuadPoints"), out PdfObject? quadsValue)
            && Resolve(document, quadsValue) is PdfArray quads && quads.Count >= 8)
        {
            for (int start = 0; start + 8 <= quads.Count; start += 8)
            {
                double[] values = new double[8];
                bool valid = true;
                for (int offset = 0; offset < 8 && valid; offset++)
                    valid = TryNumber(document, quads[start + offset], out values[offset]);
                if (!valid) continue;
                double left = Math.Min(Math.Min(values[0], values[2]), Math.Min(values[4], values[6]));
                double right = Math.Max(Math.Max(values[0], values[2]), Math.Max(values[4], values[6]));
                double bottom = Math.Min(Math.Min(values[1], values[3]), Math.Min(values[5], values[7]));
                double top = Math.Max(Math.Max(values[1], values[3]), Math.Max(values[5], values[7]));
                if (right > left && top > bottom)
                    areas.Add(new PdfContentBounds(left, bottom, right, top));
            }
            if (areas.Count > 0) return Array.AsReadOnly(areas.ToArray());
        }
        if (annotation.TryGetValue(Name("Rect"), out PdfObject? rectValue)
            && Resolve(document, rectValue) is PdfArray rect && rect.Count == 4
            && TryNumber(document, rect[0], out double x1) && TryNumber(document, rect[1], out double y1)
            && TryNumber(document, rect[2], out double x2) && TryNumber(document, rect[3], out double y2))
        {
            var bounds = new PdfContentBounds(Math.Min(x1, x2), Math.Min(y1, y2),
                Math.Max(x1, x2), Math.Max(y1, y2));
            if (bounds.Right > bounds.Left && bounds.Top > bounds.Bottom) areas.Add(bounds);
        }
        return Array.AsReadOnly(areas.ToArray());
    }

    private static PdfRgbColor? Color(PdfDocument document, PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(Name("IC"), out PdfObject? value)
            || Resolve(document, value) is not PdfArray components)
            return null;
        var numbers = new double[components.Count];
        for (int index = 0; index < components.Count; index++)
            if (!TryNumber(document, components[index], out numbers[index])
                || numbers[index] is < 0 or > 1)
                return null;
        return numbers.Length switch
        {
            1 => new PdfRgbColor(numbers[0], numbers[0], numbers[0]),
            3 => new PdfRgbColor(numbers[0], numbers[1], numbers[2]),
            4 => new PdfRgbColor((1 - numbers[0]) * (1 - numbers[3]),
                (1 - numbers[1]) * (1 - numbers[3]), (1 - numbers[2]) * (1 - numbers[3])),
            _ => null
        };
    }

    private static string? Text(PdfDocument document, PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"A redaction /{key} value")
            : null;

    private static bool TryNumber(PdfDocument document, PdfObject value, out double number)
    {
        switch (Resolve(document, value))
        {
            case PdfInteger integer:
                number = integer.Value;
                return true;
            case PdfReal real when double.IsFinite(real.Value):
                number = real.Value;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A redaction mark reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
