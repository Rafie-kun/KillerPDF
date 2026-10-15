using System.Globalization;
using KillerPdf.Engine.Authoring;

namespace KillerPDF.Services;

/// <summary>
/// Reads the parts of a text field that a preset keeps (#340) from the field's own PDF values:
/// the font size and text color in its default appearance string, and its border style.
/// </summary>
internal static class TextFieldPresetCapture
{
    /// <summary>The font size (0 when the field sizes its text automatically) and text color of a
    /// default appearance string such as "/Helv 12 Tf 0 0 1 rg".</summary>
    internal static (double FontSize, PdfRgbColor TextColor) ParseDefaultAppearance(string? appearance)
    {
        double fontSize = 0;
        var color = new PdfRgbColor(0, 0, 0);
        if (string.IsNullOrWhiteSpace(appearance)) return (fontSize, color);
        string[] tokens = appearance.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        double Operand(int index, int back) =>
            index - back >= 0 && double.TryParse(tokens[index - back], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : double.NaN;
        static double Unit(double value) => Math.Clamp(value, 0, 1);
        for (int i = 0; i < tokens.Length; i++)
            switch (tokens[i])
            {
                case "Tf":
                    double size = Operand(i, 1);
                    if (!double.IsNaN(size) && size >= 0) fontSize = size;
                    break;
                case "g":
                    double gray = Operand(i, 1);
                    if (!double.IsNaN(gray)) color = new PdfRgbColor(Unit(gray), Unit(gray), Unit(gray));
                    break;
                case "rg":
                    double r = Operand(i, 3), g = Operand(i, 2), b = Operand(i, 1);
                    if (!double.IsNaN(r) && !double.IsNaN(g) && !double.IsNaN(b))
                        color = new PdfRgbColor(Unit(r), Unit(g), Unit(b));
                    break;
                case "k":
                    double c = Operand(i, 4), m = Operand(i, 3), y = Operand(i, 2), k = Operand(i, 1);
                    if (!double.IsNaN(c) && !double.IsNaN(m) && !double.IsNaN(y) && !double.IsNaN(k))
                        color = new PdfRgbColor(
                            Unit((1 - c) * (1 - k)), Unit((1 - m) * (1 - k)), Unit((1 - y) * (1 - k)));
                    break;
            }
        return (fontSize, color);
    }

    /// <summary>The border style a widget's /BS /S name stands for.</summary>
    internal static PdfFormFieldBorderStyle BorderStyleFromName(string? name) => name switch
    {
        "D" => PdfFormFieldBorderStyle.Dashed,
        "B" => PdfFormFieldBorderStyle.Beveled,
        "I" => PdfFormFieldBorderStyle.Inset,
        "U" => PdfFormFieldBorderStyle.Underline,
        _ => PdfFormFieldBorderStyle.Solid
    };

    /// <summary>A preset name not yet used: "Preset 1", "Preset 2" and so on, from the given
    /// pattern.</summary>
    internal static string NextName(string pattern, IEnumerable<string> taken)
    {
        if (!pattern.Contains("{0}", StringComparison.Ordinal)) pattern += " {0}";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        for (int number = 1; ; number++)
        {
            string name = string.Format(CultureInfo.CurrentCulture, pattern, number);
            if (!used.Contains(name)) return name;
        }
    }
}
