using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Editing;

/// <summary>
/// Where a measurement annotation draws its value label and at what size. The label shows the
/// annotation contents, centered on <see cref="Center"/> in default user space.
/// </summary>
public sealed record PdfMeasurementCaption
{
    /// <summary>Creates a caption centered on a page point.</summary>
    public PdfMeasurementCaption(PdfPoint center, double fontSize = 10)
    {
        if (!double.IsFinite(fontSize) || fontSize is < 1 or > 144)
            throw new ArgumentOutOfRangeException(nameof(fontSize),
                "A caption font size must be between 1 and 144 points.");
        Center = center;
        FontSize = fontSize;
    }

    /// <summary>Gets the label's center point.</summary>
    public PdfPoint Center { get; }
    /// <summary>Gets the label's font size in points.</summary>
    public double FontSize { get; }
}
