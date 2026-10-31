using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Security;

/// <summary>Recognizes security names in damaged source bytes without relying on their spelling.</summary>
/// <remarks>A marker is evidence for further inspection, not proof that readable content is encrypted.</remarks>
public static class PdfSecurityIndicators
{
    /// <summary>Finds a Filter name followed by the Standard or Adobe.PubSec security handler.</summary>
    public static bool NamesSecurityHandler(ReadOnlyMemory<byte> source) => Scan(source, false);

    /// <summary>Finds an Encrypt name or a supported security handler marker.</summary>
    public static bool NamesEncryption(ReadOnlyMemory<byte> source) => Scan(source, true);

    private static bool Scan(ReadOnlyMemory<byte> source, bool includeEncrypt)
    {
        ReadOnlySpan<byte> bytes = source.Span;
        for (int position = 0; position < bytes.Length;)
        {
            int next = bytes[position..].IndexOf((byte)'/');
            if (next < 0) return false;
            position += next;
            string? name = ReadName(source, ref position);
            if (includeEncrypt && name == "Encrypt") return true;
            if (name != "Filter") continue;
            int value = position;
            while (value < bytes.Length)
            {
                if (Whitespace(bytes[value])) { value++; continue; }
                if (bytes[value] != '%') break;
                while (value < bytes.Length && bytes[value] is not (10 or 13)) value++;
            }
            if (value < bytes.Length && bytes[value] == '/'
                && ReadName(source, ref value) is "Standard" or "Adobe.PubSec") return true;
        }
        return false;
    }

    private static string? ReadName(ReadOnlyMemory<byte> source, ref int position)
    {
        int start = position++;
        ReadOnlySpan<byte> bytes = source.Span;
        while (position < bytes.Length && !Whitespace(bytes[position])
            && bytes[position] is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
                or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%')) position++;
        // The longest supported name needs at most 36 bytes with every character escaped.
        if (position - start > 37) return null;
        try { return new PdfTokenizer(source.Slice(start, position - start)).Read().ValueAsLatin1(); }
        catch (FormatException) { return null; }
    }

    private static bool Whitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
}
