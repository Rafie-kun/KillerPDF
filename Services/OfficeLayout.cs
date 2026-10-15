using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPDF.Services;

// Document to PDF (#74): the page model the Word and Excel converters fill, the installed fonts
// they measure with, and the writer that turns finished pages into a PDF. Coordinates here are
// points from the top left of the page; the writer flips them for PDF.

internal readonly record struct OfficeColor(byte R, byte G, byte B)
{
    internal static readonly OfficeColor Black = new(0, 0, 0);

    /// <summary>Reads RRGGBB or AARRGGBB hex; null for "auto" and anything unreadable.</summary>
    internal static OfficeColor? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim();
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            return null;
        return new OfficeColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

internal enum OfficeVerticalPosition { Baseline, Superscript, Subscript }

internal sealed record OfficeRunStyle(
    string Family, double Size, bool Bold = false, bool Italic = false, bool Underline = false,
    bool Strike = false, OfficeColor Color = default, OfficeColor? Highlight = null,
    OfficeVerticalPosition Position = OfficeVerticalPosition.Baseline)
{
    /// <summary>The size glyphs are drawn at; raised and lowered text is smaller.</summary>
    internal double DrawSize => Position == OfficeVerticalPosition.Baseline ? Size : Size * 0.65;

    /// <summary>How far the baseline moves up for raised text, or down for lowered text.</summary>
    internal double Rise => Position switch
    {
        OfficeVerticalPosition.Superscript => Size * 0.33,
        OfficeVerticalPosition.Subscript => -Size * 0.14,
        _ => 0
    };
}

internal abstract record OfficeDrawOp;
internal sealed record OfficeTextOp(double X, double Baseline, string Text, TrueTypeFont Font, double Size, OfficeColor Color) : OfficeDrawOp;
internal sealed record OfficeRectOp(double X, double Y, double Width, double Height, OfficeColor Color) : OfficeDrawOp;
internal sealed record OfficeLineOp(double X1, double Y1, double X2, double Y2, double Thickness, OfficeColor Color) : OfficeDrawOp;
internal sealed record OfficeImageOp(PdfImage Image, double X, double Y, double Width, double Height) : OfficeDrawOp;
internal sealed record OfficeClipOp(double X, double Y, double Width, double Height, IReadOnlyList<OfficeDrawOp> Ops) : OfficeDrawOp;

internal sealed class OfficePage(double width, double height)
{
    internal double Width { get; } = width;
    internal double Height { get; } = height;
    internal List<OfficeDrawOp> Ops { get; } = [];
}

internal static class OfficeDraw
{
    internal static OfficeDrawOp Move(OfficeDrawOp op, double dx, double dy) => op switch
    {
        OfficeTextOp text => text with { X = text.X + dx, Baseline = text.Baseline + dy },
        OfficeRectOp rect => rect with { X = rect.X + dx, Y = rect.Y + dy },
        OfficeLineOp line => line with { X1 = line.X1 + dx, Y1 = line.Y1 + dy, X2 = line.X2 + dx, Y2 = line.Y2 + dy },
        OfficeImageOp image => image with { X = image.X + dx, Y = image.Y + dy },
        OfficeClipOp clip => clip with
        {
            X = clip.X + dx, Y = clip.Y + dy,
            Ops = [.. clip.Ops.Select(inner => Move(inner, dx, dy))]
        },
        _ => op
    };

    internal static void AddMoved(List<OfficeDrawOp> target, IEnumerable<OfficeDrawOp> ops, double dx, double dy)
    {
        foreach (OfficeDrawOp op in ops) target.Add(Move(op, dx, dy));
    }

    /// <summary>Writes the pages as a PDF; a document with no pages gets one blank Letter page.</summary>
    internal static byte[] Write(IReadOnlyList<OfficePage> pages)
    {
        var builder = new PdfDocumentBuilder();
        if (pages.Count == 0)
        {
            builder.AddPage(612, 792, ReadOnlyMemory<byte>.Empty);
            return builder.Build();
        }
        foreach (OfficePage page in pages)
        {
            var content = new PdfContentStreamBuilder();
            foreach (OfficeDrawOp op in page.Ops) Emit(content, op, page.Height);
            builder.AddPage(page.Width, page.Height, content);
        }
        return builder.Build();
    }

    private static void Emit(PdfContentStreamBuilder content, OfficeDrawOp op, double pageHeight)
    {
        switch (op)
        {
            case OfficeTextOp text when text.Text.Length > 0:
                content.SetFillRgb(text.Color.R / 255d, text.Color.G / 255d, text.Color.B / 255d)
                    .BeginText()
                    .SetFont(text.Font, text.Size)
                    .SetTextMatrix(1, 0, 0, 1, text.X, pageHeight - text.Baseline)
                    .ShowUnicodeText(text.Text)
                    .EndText();
                break;
            case OfficeRectOp rect when rect.Width > 0 && rect.Height > 0:
                content.SetFillRgb(rect.Color.R / 255d, rect.Color.G / 255d, rect.Color.B / 255d)
                    .Rectangle(rect.X, pageHeight - rect.Y - rect.Height, rect.Width, rect.Height)
                    .Fill();
                break;
            case OfficeLineOp line when line.Thickness > 0:
                content.SetStrokeRgb(line.Color.R / 255d, line.Color.G / 255d, line.Color.B / 255d)
                    .SetLineWidth(line.Thickness)
                    .MoveTo(line.X1, pageHeight - line.Y1)
                    .LineTo(line.X2, pageHeight - line.Y2)
                    .Stroke();
                break;
            case OfficeImageOp image when image.Width > 0 && image.Height > 0:
                content.DrawImage(image.Image, image.X, pageHeight - image.Y - image.Height, image.Width, image.Height);
                break;
            case OfficeClipOp clip when clip.Width > 0 && clip.Height > 0 && clip.Ops.Count > 0:
                content.SaveState()
                    .Rectangle(clip.X, pageHeight - clip.Y - clip.Height, clip.Width, clip.Height)
                    .Clip();
                foreach (OfficeDrawOp inner in clip.Ops) Emit(content, inner, pageHeight);
                content.RestoreState();
                break;
        }
    }
}

/// <summary>Vertical font metrics for a one point size, the way Word spaces single lines.</summary>
internal readonly record struct OfficeFontMetrics(double Ascent, double Descent, double Gap)
{
    internal double LineHeight => Ascent + Descent + Gap;
}

/// <summary>
/// Installed fonts for one conversion: the face a run asks for, a covering face for characters
/// it lacks, widths for measuring, and line metrics. Characters no installed face can draw are
/// dropped, because an unmapped glyph cannot be written.
/// </summary>
internal sealed class OfficeFonts
{
    private static readonly string[] Fallbacks = ["Calibri", "Arial", "Segoe UI", "Times New Roman", "Tahoma"];
    private readonly Dictionary<(string Family, bool Bold, bool Italic), TrueTypeFont?> _faces = [];
    private readonly Dictionary<(int Scalar, string Family, bool Bold, bool Italic), TrueTypeFont?> _covering = [];
    private readonly Dictionary<TrueTypeFont, OfficeFontMetrics> _metrics = [];

    internal TrueTypeFont Primary(OfficeRunStyle style)
    {
        if (Face(style.Family, style.Bold, style.Italic) is TrueTypeFont wanted) return wanted;
        foreach (string family in Fallbacks)
            if (Face(family, style.Bold, style.Italic) is TrueTypeFont fallback) return fallback;
        throw new InvalidOperationException("No installed font can be embedded in the PDF.");
    }

    private TrueTypeFont? Face(string family, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        var key = (family, bold, italic);
        if (_faces.TryGetValue(key, out TrueTypeFont? cached)) return cached;
        TrueTypeFont? font = null;
        byte[]? bytes = InstalledFontCatalog.FaceBytes(family, bold, italic);
        if (bytes is not null)
        {
            try
            {
                TrueTypeFont loaded = TrueTypeFont.Load(bytes);
                if (loaded.EmbeddingAllowed) font = loaded;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException
                or IndexOutOfRangeException or OverflowException)
            {
                font = null;
            }
        }
        _faces[key] = font;
        return font;
    }

    /// <summary>Splits text into pieces that one face can draw each.</summary>
    internal List<(string Text, TrueTypeFont Font)> Segments(string text, OfficeRunStyle style)
    {
        var result = new List<(string, TrueTypeFont)>();
        if (string.IsNullOrEmpty(text)) return result;
        TrueTypeFont primary = Primary(style);
        var current = new StringBuilder();
        TrueTypeFont? currentFont = null;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            TrueTypeFont? font = primary.GetGlyphId(rune.Value) != 0 ? primary : Covering(rune, style);
            if (font is null) continue;
            if (currentFont is not null && !ReferenceEquals(font, currentFont))
            {
                result.Add((current.ToString(), currentFont));
                current.Clear();
            }
            currentFont = font;
            current.Append(rune.ToString());
        }
        if (currentFont is not null && current.Length > 0) result.Add((current.ToString(), currentFont));
        return result;
    }

    private TrueTypeFont? Covering(Rune rune, OfficeRunStyle style)
    {
        var key = (rune.Value, style.Family, style.Bold, style.Italic);
        if (_covering.TryGetValue(key, out TrueTypeFont? cached)) return cached;
        TrueTypeFont? found = null;
        string family = FontCoverage.PickFamily(style.Family, rune.ToString());
        if (Face(family, style.Bold, style.Italic) is TrueTypeFont picked && picked.GetGlyphId(rune.Value) != 0)
            found = picked;
        if (found is null)
        {
            foreach (string fallback in Fallbacks)
            {
                if (Face(fallback, style.Bold, style.Italic) is TrueTypeFont face && face.GetGlyphId(rune.Value) != 0)
                {
                    found = face;
                    break;
                }
            }
        }
        _covering[key] = found;
        return found;
    }

    internal static double Width(string text, TrueTypeFont font, double size)
    {
        double units = 0;
        foreach (Rune rune in text.EnumerateRunes())
            units += font.GetPdfAdvanceWidth(font.GetGlyphId(rune.Value));
        return units * size / 1000d;
    }

    /// <summary>Measures text drawn in a style, across every face it falls back to.</summary>
    internal double Measure(string text, OfficeRunStyle style)
    {
        double width = 0;
        foreach ((string piece, TrueTypeFont font) in Segments(text, style)) width += Width(piece, font, style.DrawSize);
        return width;
    }

    internal OfficeFontMetrics Metrics(TrueTypeFont font)
    {
        if (_metrics.TryGetValue(font, out OfficeFontMetrics cached)) return cached;
        double unitsPerEm = font.UnitsPerEm > 0 ? font.UnitsPerEm : 1000;
        double ascent = font.Ascender, descent = -font.Descender, gap = 0;
        if (TryReadHorizontalHeader(font.FontData.Span, out short hheaAscent, out short hheaDescent, out short hheaGap)
            && hheaAscent > 0)
        {
            ascent = hheaAscent;
            descent = Math.Abs((int)hheaDescent);
            gap = Math.Max(0, (int)hheaGap);
        }
        if (ascent <= 0) ascent = unitsPerEm * 0.8;
        if (descent <= 0) descent = unitsPerEm * 0.2;
        var metrics = new OfficeFontMetrics(ascent / unitsPerEm, descent / unitsPerEm, gap / unitsPerEm);
        _metrics[font] = metrics;
        return metrics;
    }

    // hhea holds the ascender, descender and line gap Word adds up for single spacing.
    private static bool TryReadHorizontalHeader(ReadOnlySpan<byte> data, out short ascent, out short descent, out short gap)
    {
        ascent = descent = gap = 0;
        if (data.Length < 12) return false;
        int tables = (data[4] << 8) | data[5];
        for (int index = 0; index < tables; index++)
        {
            int record = 12 + index * 16;
            if (record + 16 > data.Length) return false;
            if (data[record] != 'h' || data[record + 1] != 'h' || data[record + 2] != 'e' || data[record + 3] != 'a')
                continue;
            long offset = ((long)data[record + 8] << 24) | ((long)data[record + 9] << 16)
                | ((long)data[record + 10] << 8) | data[record + 11];
            if (offset < 0 || offset + 10 > data.Length) return false;
            int start = (int)offset;
            ascent = (short)((data[start + 4] << 8) | data[start + 5]);
            descent = (short)((data[start + 6] << 8) | data[start + 7]);
            gap = (short)((data[start + 8] << 8) | data[start + 9]);
            return true;
        }
        return false;
    }
}

/// <summary>Theme fonts and colors shared by Word and Excel packages.</summary>
internal sealed class OfficeTheme
{
    internal string MajorFont { get; private set; } = "Calibri Light";
    internal string MinorFont { get; private set; } = "Calibri";

    /// <summary>dk1, lt1, dk2, lt2, accent1 to accent6, hlink, folHlink, in theme file order.</summary>
    internal OfficeColor[] Colors { get; } =
    [
        new(0, 0, 0), new(255, 255, 255), new(0x44, 0x54, 0x6A), new(0xE7, 0xE6, 0xE6),
        new(0x44, 0x72, 0xC4), new(0xED, 0x7D, 0x31), new(0xA5, 0xA5, 0xA5), new(0xFF, 0xC0, 0x00),
        new(0x5B, 0x9B, 0xD5), new(0x70, 0xAD, 0x47), new(0x05, 0x63, 0xC1), new(0x95, 0x4F, 0x72)
    ];

    internal static OfficeTheme Read(System.Xml.Linq.XDocument? document)
    {
        var theme = new OfficeTheme();
        if (document?.Root is null) return theme;
        foreach (System.Xml.Linq.XElement element in document.Root.Descendants())
        {
            string name = element.Name.LocalName;
            if (name is "majorFont" or "minorFont")
            {
                string? typeface = element.Elements().FirstOrDefault(child => child.Name.LocalName == "latin")
                    ?.Attribute("typeface")?.Value;
                if (!string.IsNullOrWhiteSpace(typeface))
                {
                    if (name == "majorFont") theme.MajorFont = typeface;
                    else theme.MinorFont = typeface;
                }
            }
            else if (name == "clrScheme")
            {
                string[] order = ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4",
                    "accent5", "accent6", "hlink", "folHlink"];
                for (int index = 0; index < order.Length; index++)
                {
                    System.Xml.Linq.XElement? slot = element.Elements().FirstOrDefault(child => child.Name.LocalName == order[index]);
                    System.Xml.Linq.XElement? value = slot?.Elements().FirstOrDefault();
                    string? hex = value?.Name.LocalName switch
                    {
                        "srgbClr" => value.Attribute("val")?.Value,
                        "sysClr" => value.Attribute("lastClr")?.Value,
                        _ => null
                    };
                    if (OfficeColor.Parse(hex) is OfficeColor color) theme.Colors[index] = color;
                }
            }
        }
        return theme;
    }

    /// <summary>Applies an Office tint, darker below zero and lighter above.</summary>
    internal static OfficeColor Tint(OfficeColor color, double tint)
    {
        if (tint == 0 || !double.IsFinite(tint)) return color;
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2, hue = 0, saturation = 0;
        if (max != min)
        {
            double delta = max - min;
            saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
            if (max == r) hue = (g - b) / delta + (g < b ? 6 : 0);
            else if (max == g) hue = (b - r) / delta + 2;
            else hue = (r - g) / delta + 4;
            hue /= 6;
        }
        lightness = tint < 0 ? lightness * (1 + tint) : lightness * (1 - tint) + tint;
        lightness = Math.Clamp(lightness, 0, 1);
        double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1d / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2d / 3) return p + (q - p) * (2d / 3 - t) * 6;
            return p;
        }
        if (saturation == 0)
        {
            byte gray = (byte)Math.Round(lightness * 255);
            return new OfficeColor(gray, gray, gray);
        }
        double q2 = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        double p2 = 2 * lightness - q2;
        return new OfficeColor(
            (byte)Math.Round(Channel(p2, q2, hue + 1d / 3) * 255),
            (byte)Math.Round(Channel(p2, q2, hue) * 255),
            (byte)Math.Round(Channel(p2, q2, hue - 1d / 3) * 255));
    }
}
