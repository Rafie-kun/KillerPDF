namespace KillerPdf.Engine.Fonts;

/// <summary>Encodes text for a standard Type 1 font that uses WinAnsiEncoding, and measures it.</summary>
internal static class PdfWinAnsiText
{
    private static readonly string?[] GlyphNames = new string?[256];
    private static readonly Dictionary<string, byte> Codes = Build();

    private static Dictionary<string, byte> Build()
    {
        var codes = new Dictionary<string, byte>(StringComparer.Ordinal);
        string[] names = PdfFontTables.EncodingNames("WinAnsiEncoding") ?? [];
        for (int code = 32; code < Math.Min(names.Length, 256); code++)
        {
            string name = names[code];
            if (name.Length == 0) continue;
            // Unassigned codes repeat the bullet; the real one is 0x95.
            if (name == "bullet" && code != 0x95) continue;
            GlyphNames[code] = name;
            if (PdfFontTables.GlyphText(name) is string text && text.Length > 0) codes.TryAdd(text, (byte)code);
        }
        codes[" "] = 0xA0;
        return codes;
    }

    /// <summary>True when every character has a WinAnsi code.</summary>
    internal static bool CanEncode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (string element in TextElements(value))
            if (!Codes.ContainsKey(element)) return false;
        return true;
    }

    /// <summary>Returns the WinAnsi bytes, with a question mark for any character the encoding lacks.</summary>
    internal static byte[] Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new List<byte>(value.Length);
        foreach (string element in TextElements(value))
            bytes.Add(Codes.TryGetValue(element, out byte code) ? code : (byte)'?');
        return [.. bytes];
    }

    /// <summary>Returns the text the encoded bytes show, so a caller can compare it with extracted text.</summary>
    internal static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = new System.Text.StringBuilder(bytes.Length);
        foreach (byte code in bytes)
            text.Append(GlyphNames[code] is string name ? PdfFontTables.GlyphText(name) ?? "?" : "?");
        return text.ToString();
    }

    /// <summary>Returns the advance width of the encoded bytes in text space units at a size of one.</summary>
    internal static double Width(string font, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(font);
        ArgumentNullException.ThrowIfNull(bytes);
        double total = 0;
        foreach (byte code in bytes)
            total += (GlyphNames[code] is string name ? PdfStandardGlyphBounds.Width(font, name) : null) ?? 556;
        return total / 1000;
    }

    private static IEnumerable<string> TextElements(string value)
    {
        for (int index = 0; index < value.Length;)
        {
            int length = char.IsSurrogatePair(value, index) ? 2 : 1;
            yield return value.Substring(index, length);
            index += length;
        }
    }
}
