using System.Globalization;

namespace KillerPDF.Services;

/// <summary>A unit the Measure tool can show, and how many inches one of it is.</summary>
internal sealed record MeasureUnit(string Id, string Symbol, double Inches, bool Metric);

/// <summary>
/// Units, scales and value text for the Measure tool. Plain numbers in and text out, free of WPF
/// so KillerPDF.Tests can link this file. A scale is kept the way the PDF stores it: drawing units
/// per PDF point (1/72 inch on paper).
/// </summary>
internal static class MeasurementUnits
{
    internal const string FeetAndInches = "ft-in";

    internal static readonly MeasureUnit[] All =
    [
        new("in", "in", 1, false),
        new("ft", "ft", 12, false),
        new(FeetAndInches, "ft-in", 12, false),
        new("yd", "yd", 36, false),
        new("mi", "mi", 63360, false),
        new("mm", "mm", 1 / 25.4, true),
        new("cm", "cm", 1 / 2.54, true),
        new("m", "m", 1 / 0.0254, true),
        new("km", "km", 1 / 0.0000254, true),
        new("pt", "pt", 1 / 72.0, false),
    ];

    /// <summary>The units offered in the Measure bar. Points are only read from other files.</summary>
    internal static IEnumerable<MeasureUnit> Offered => All.Where(unit => unit.Id != "pt");

    internal static bool TryFind(string? id, out MeasureUnit unit)
    {
        unit = All.FirstOrDefault(candidate => candidate.Id == id)!;
        return unit is not null;
    }

    /// <summary>The unit with this id, or inches for an id the tool does not know.</summary>
    internal static MeasureUnit Find(string? id) => TryFind(id, out MeasureUnit unit) ? unit : All[0];

    /// <summary>Millimeters where the region measures in metric, inches elsewhere.</summary>
    internal static string DefaultUnitId(bool metric) => metric ? "mm" : "in";

    internal static bool RegionIsMetric()
    {
        try { return RegionInfo.CurrentRegion.IsMetric; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Decimal places that read naturally for a unit; feet and inches show whole inches.</summary>
    internal static int DefaultPrecision(string id) => id switch
    {
        "mm" => 1,
        FeetAndInches => 0,
        "mi" or "km" => 3,
        _ => 2,
    };

    /// <summary>Drawing units per PDF point when the page is drawn at actual size.</summary>
    internal static double ActualSizeUnitsPerPoint(string id) => 1.0 / 72.0 / Find(id).Inches;

    /// <summary>
    /// The scale for "paper length in paper unit equals real length in real unit", in real units
    /// per PDF point.
    /// </summary>
    internal static double UnitsPerPoint(double paperLength, string paperUnit, double realLength, string realUnit)
    {
        if (!(paperLength > 0) || !(realLength > 0) || !double.IsFinite(paperLength) || !double.IsFinite(realLength))
            throw new ArgumentOutOfRangeException(nameof(paperLength), "Scale lengths must be positive.");
        return realLength / (paperLength * Find(paperUnit).Inches * 72.0);
    }

    /// <summary>The same scale expressed in another unit.</summary>
    internal static double ConvertScale(double unitsPerPoint, string fromUnit, string toUnit) =>
        unitsPerPoint * Find(fromUnit).Inches / Find(toUnit).Inches;

    /// <summary>True when a scale in this unit can be shown in the Measure bar's units.</summary>
    internal static bool CanConvert(string unit) => TryFind(unit, out _);

    /// <summary>
    /// The scale ratio text saved into the PDF, which other viewers show as is, so it uses plain
    /// invariant numbers: "1 in = 10 ft". Feet and inches name feet.
    /// </summary>
    internal static string RatioText(double paperLength, string paperUnit, double realLength, string realUnit) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{paperLength:0.######} {RatioSymbol(paperUnit)} = {realLength:0.######} {RatioSymbol(realUnit)}");

    /// <summary>The ratio text for a page drawn at actual size, in one unit.</summary>
    internal static string ActualSizeText(string unit) => RatioText(1, unit, 1, unit);

    private static string RatioSymbol(string unit) => unit == FeetAndInches ? "ft" : Symbol(unit);

    /// <summary>The symbol shown after a value. A unit from another file that the tool does not
    /// know keeps the text that file gave it.</summary>
    internal static string Symbol(string unit) => TryFind(unit, out MeasureUnit known) ? known.Symbol : unit;

    /// <summary>A length in a unit at a number of decimal places, in the reader's number format.</summary>
    internal static string FormatLength(double value, string unit, int precision, CultureInfo culture)
    {
        precision = Math.Clamp(precision, 0, 6);
        if (unit == FeetAndInches) return FeetAndInchesText(value, precision, culture);
        return Clean(value.ToString("N" + precision, culture)) + " " + Symbol(unit);
    }

    /// <summary>An area, in the square of the length unit (square feet for feet and inches).</summary>
    internal static string FormatArea(double value, string unit, int precision, CultureInfo culture)
    {
        precision = Math.Clamp(precision, 0, 6);
        string symbol = unit == FeetAndInches ? "ft" : Symbol(unit);
        return Clean(value.ToString("N" + precision, culture)) + " " + symbol + "²";
    }

    /// <summary>An angle in degrees to one decimal place.</summary>
    internal static string FormatAngle(double degrees, CultureInfo culture) =>
        Clean(degrees.ToString("N1", culture)) + "°";

    // Feet with the inches left over, the way a tape reads: 3' 4". The precision applies to the
    // inches, and inches that round up to twelve carry into the next foot.
    private static string FeetAndInchesText(double feet, int precision, CultureInfo culture)
    {
        double totalInches = Math.Round(Math.Abs(feet) * 12, precision, MidpointRounding.AwayFromZero);
        double wholeFeet = Math.Floor(totalInches / 12);
        double inches = Math.Round(totalInches - wholeFeet * 12, precision, MidpointRounding.AwayFromZero);
        if (inches >= 12) { wholeFeet++; inches -= 12; }
        string sign = feet < 0 && totalInches > 0 ? "-" : "";
        return sign + wholeFeet.ToString("N0", culture) + "' "
            + Clean(inches.ToString("N" + precision, culture)) + "\"";
    }

    // Labels are also drawn into the PDF with a standard font that has no narrow no-break space,
    // which some number formats use between digit groups; a plain no-break space looks the same.
    private static string Clean(string text) => text.Replace(' ', ' ');

    /// <summary>Reads a length typed in the reader's number format, or the invariant one.</summary>
    internal static bool TryParseLength(string? text, CultureInfo culture, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        return (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, culture, out value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value) && value > 0;
    }
}
