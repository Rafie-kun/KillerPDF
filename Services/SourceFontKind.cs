using System.Text.RegularExpressions;

namespace KillerPDF.Services
{
    // An embedded font is rarely installed, so a paragraph edit needs a stand-in. A serif paper set in
    // Segoe UI runs about 8 percent wider and spills onto the next paragraph, so the stand-in comes
    // from the fonts of the same kind, in the order to try them. Names decide the kind because many
    // embedded fonts carry no usable descriptor flags, and TeX's short names are as good as any.
    internal static partial class SourceFontKind
    {
        private static readonly string[] Serif = ["Times New Roman", "Georgia", "Cambria", "Cambria Math"];
        private static readonly string[] Sans = ["Arial", "Segoe UI", "Calibri", "Tahoma", "Verdana", "Segoe UI Symbol"];
        private static readonly string[] Mono = ["Consolas", "Courier New"];

        internal static string[] Families(string pdfFontName)
        {
            string name = pdfFontName ?? "";
            int subset = name.IndexOf('+');
            if (subset >= 0) name = name[(subset + 1)..];
            if (MonoName().IsMatch(name)) return Mono;
            if (SansName().IsMatch(name)) return Sans;
            if (SerifName().IsMatch(name)) return Serif;
            return [.. Sans, .. Serif];
        }

        [GeneratedRegex("courier|mono|consol|typewriter|^cmtt|^lmtt|^sftt|menlo|inconsolata", RegexOptions.IgnoreCase)]
        private static partial Regex MonoName();

        [GeneratedRegex("sans|arial|helvet|gothic|grotesk|frutiger|myriad|univers|calibri|tahoma|verdana|segoe|roboto|lato|overpass|^cmss|^lmss|^sfss", RegexOptions.IgnoreCase)]
        private static partial Regex SansName();

        [GeneratedRegex("times|roman|serif|minion|garamond|palatino|georgia|cambria|century|bookman|^cm[rbst]|^lm[rbs]|^sf[rbs]|stix|libertine|utopia|charter|merriweather|nimbusrom|bodoni|caslon|baskerville|didot|sabon|mathtime|^tx|^pxr", RegexOptions.IgnoreCase)]
        private static partial Regex SerifName();
    }
}
