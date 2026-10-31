using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Describes the effective display geometry of one PDF page.</summary>
public sealed record PdfPageInformation
{
    private static readonly PdfArray CompatibilityDefaultMediaBox = new(
        [new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792)]);

    /// <summary>Gets the left edge of the effective crop or media box.</summary>
    public double Left { get; init; }
    /// <summary>Gets the bottom edge of the effective crop or media box.</summary>
    public double Bottom { get; init; }
    /// <summary>Gets the page width in default user-space units before rotation.</summary>
    public required double Width { get; init; }
    /// <summary>Gets the page height in default user-space units before rotation.</summary>
    public required double Height { get; init; }
    /// <summary>Gets the number of 1/72-inch points in one default user-space unit.</summary>
    public double UserUnit { get; init; } = 1;
    /// <summary>Gets the physical width in 1/72-inch points before rotation.</summary>
    public double PhysicalWidth => Width * UserUnit;
    /// <summary>Gets the physical height in 1/72-inch points before rotation.</summary>
    public double PhysicalHeight => Height * UserUnit;
    /// <summary>Gets the clockwise page rotation in degrees.</summary>
    public required int Rotation { get; init; }

    /// <summary>Reads the effective crop or media geometry for every page.</summary>
    public static IReadOnlyList<PdfPageInformation> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document, allowCycleRecovery: true);
        var result = new PdfPageInformation[tree.Pages.Count];
        for (int index = 0; index < tree.Pages.Count; index++)
        {
            PdfPageTreeEntry page = tree.Pages[index];
            PdfContentBounds box = EffectiveBox(document, page);
            int rotation = 0;
            if (page.InheritedValues.TryGetValue(Name("Rotate"), out PdfObject? rotationValue))
            {
                PdfObject resolved = Resolve(document, rotationValue,
                    $"Page {index + 1} rotation");
                long rawRotation = resolved is PdfInteger integer
                    ? integer.Value
                    : throw new InvalidOperationException(
                        $"Page {index + 1} rotation is not an integer.");
                rotation = (int)(((rawRotation % 360) + 360) % 360);
                if (rotation % 90 != 0)
                {
                    if (!document.UsesCompatibilityRecovery)
                        throw new InvalidOperationException(
                            $"Page {index + 1} rotation is not a multiple of 90 degrees.");
                    rotation = (int)Math.Round(rotation / 90.0) * 90 % 360;
                }
            }
            result[index] = new PdfPageInformation
            {
                Left = box.Left,
                Bottom = box.Bottom,
                Width = box.Width,
                Height = box.Height,
                UserUnit = ReadUserUnit(document, page.Dictionary, index),
                Rotation = rotation
            };
        }
        return result;
    }

    internal static double ReadUserUnit(PdfDocument document, PdfDictionary page, int index)
    {
        if (!page.TryGetValue(Name("UserUnit"), out PdfObject? value)) return 1;
        try
        {
            double unit = Number(document, value, index);
            if (double.IsFinite(unit) && unit > 0 && unit <= 75_000) return unit;
            throw new InvalidOperationException($"Page {index + 1} UserUnit is outside the supported range.");
        }
        catch (InvalidOperationException) when (document.UsesCompatibilityRecovery) { return 1; }
    }

    /// <summary>
    /// Reads the box a page is shown in: its crop box cut to its media box, or the crop box alone
    /// when the two do not meet. The viewer, the renderer, the page content reader and the
    /// redaction rewriter all place content in this frame.
    /// </summary>
    internal static PdfContentBounds EffectiveBox(PdfDocument document, PdfPageTreeEntry page)
    {
        int index = page.Index;
        PdfObject pageBox =
            page.InheritedValues.TryGetValue(Name("CropBox"), out PdfObject? crop)
                ? crop
                : page.InheritedValues.TryGetValue(Name("MediaBox"), out PdfObject? media)
                    ? media
                    : document.UsesCompatibilityRecovery
                        ? CompatibilityDefaultMediaBox
                        : throw new InvalidOperationException(
                            $"Page {index + 1} has no effective media box.");
        double x1, y1, x2, y2, width, height;
        try
        {
            PdfArray box = ResolveArray(document, pageBox,
                $"Page {index + 1} effective page box");
            if (box.Count != 4)
                throw new InvalidOperationException(
                    $"Page {index + 1} effective page box does not contain four numbers.");
            x1 = Number(document, box[0], index);
            y1 = Number(document, box[1], index);
            x2 = Number(document, box[2], index);
            y2 = Number(document, box[3], index);
            if (crop is not null && page.InheritedValues.TryGetValue(Name("MediaBox"), out PdfObject? physicalBox))
            {
                PdfArray physical = ResolveArray(document, physicalBox, $"Page {index + 1} media box");
                if (physical.Count != 4)
                    throw new InvalidOperationException($"Page {index + 1} media box does not contain four numbers.");
                double mx1 = Number(document, physical[0], index), my1 = Number(document, physical[1], index);
                double mx2 = Number(document, physical[2], index), my2 = Number(document, physical[3], index);
                double left = Math.Max(Math.Min(x1, x2), Math.Min(mx1, mx2));
                double bottom = Math.Max(Math.Min(y1, y2), Math.Min(my1, my2));
                double right = Math.Min(Math.Max(x1, x2), Math.Max(mx1, mx2));
                double top = Math.Min(Math.Max(y1, y2), Math.Max(my1, my2));
                if (right > left && top > bottom)
                    (x1, y1, x2, y2) = (left, bottom, right, top);
            }
            width = Math.Abs(x2 - x1);
            height = Math.Abs(y2 - y1);
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                throw new InvalidOperationException(
                    $"Page {index + 1} effective page box is degenerate.");
        }
        catch (InvalidOperationException) when (document.UsesCompatibilityRecovery)
        {
            // An unusable crop box does not invalidate a usable media box.
            if (crop is null || !TryReadCompatibilityMediaBox(
                document, page, index, out x1, out y1, out x2, out y2,
                out width, out height))
            {
                // Mainstream viewers fall back to US Letter when no usable box remains.
                x1 = 0;
                y1 = 0;
                x2 = 612;
                y2 = 792;
            }
        }
        return new PdfContentBounds(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    private static bool TryReadCompatibilityMediaBox(
        PdfDocument document, PdfPageTreeEntry page, int pageIndex,
        out double x1, out double y1, out double x2, out double y2,
        out double width, out double height)
    {
        x1 = y1 = x2 = y2 = width = height = 0;
        if (!page.InheritedValues.TryGetValue(Name("MediaBox"), out PdfObject? value))
            return false;
        try
        {
            PdfArray box = ResolveArray(document, value,
                $"Page {pageIndex + 1} media box");
            if (box.Count != 4) return false;
            x1 = Number(document, box[0], pageIndex);
            y1 = Number(document, box[1], pageIndex);
            x2 = Number(document, box[2], pageIndex);
            y2 = Number(document, box[3], pageIndex);
            width = Math.Abs(x2 - x1);
            height = Math.Abs(y2 - y1);
            return double.IsFinite(width) && double.IsFinite(height)
                && width > 0 && height > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static double Number(PdfDocument document, PdfObject value, int pageIndex) =>
        Resolve(document, value, $"Page {pageIndex + 1} page-box coordinate") switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new InvalidOperationException(
                $"Page {pageIndex + 1} page box contains a nonnumeric coordinate.")
        };

    private static PdfArray ResolveArray(
        PdfDocument document, PdfObject value, string description) =>
        Resolve(document, value, description) as PdfArray
        ?? throw new InvalidOperationException($"{description} is not an array.");

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
