using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

public static partial class PdfColorConversionMacro
{
    private static PdfStream ConvertImage(PdfDocument document, PdfStream image, PdfDictionary dictionary,
        PdfMacroColorOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (dictionary.TryGetValue(Name("ImageMask"), out PdfObject? stencil)
            && Resolve(stencil) is PdfBoolean { Value: true })
            return ReferenceEquals(dictionary, image.Dictionary) ? image : PdfStream.FromOwnedData(dictionary, image.EncodedData);
        if (options.Mode == PdfMacroColorMode.Grayscale
            && dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? space)
            && Resolve(space) is PdfName colorSpace && colorSpace.ValueAsLatin1() == "DeviceGray")
            return ReferenceEquals(dictionary, image.Dictionary) ? image : PdfStream.FromOwnedData(dictionary, image.EncodedData);
        if (dictionary.TryGetValue(Name("SMask"), out PdfObject? mask)
            && Resolve(mask) is PdfStream alpha && alpha.Dictionary.ContainsKey(Name("Matte")))
            throw new NotSupportedException("Color conversion cannot yet preserve image matte colors.");

        PdfImageRedaction.Samples samples = PdfImageRedaction.Decode(document, image)
            ?? throw new NotSupportedException("Color conversion cannot preserve this image's color space, masks, or sample encoding.");
        if (samples.Components is not (1 or 3 or 4))
            throw new NotSupportedException("The image color space is not yet supported for color conversion.");
        long count = (long)samples.Width * samples.Height;
        if (count > PdfStreamDecoder.DefaultMaximumDecodedBytes)
            throw new NotSupportedException("The image is too large for bounded color conversion.");
        var pixels = new byte[(int)count];
        for (int y = 0; y < samples.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < samples.Width; x++)
            {
                int index = y * samples.Width + x;
                byte gray;
                if (samples.Bits == 1)
                {
                    int bit = (samples.Data[y * samples.RowBytes + (x >> 3)] >> (7 - (x & 7))) & 1;
                    gray = (samples.Inverted ? bit == 0 : bit != 0) ? (byte)255 : (byte)0;
                }
                else if (samples.Components == 1) gray = samples.Data[index];
                else if (samples.Components == 3) gray = Gray(samples.Data[index * 3], samples.Data[index * 3 + 1], samples.Data[index * 3 + 2]);
                else gray = CmykGray(samples.Data[index * 4], samples.Data[index * 4 + 1],
                    samples.Data[index * 4 + 2], samples.Data[index * 4 + 3]);
                pixels[index] = options.Mode == PdfMacroColorMode.BlackAndWhite
                    ? gray >= options.Threshold ? (byte)255 : (byte)0 : gray;
            }
        }
        PdfImage converted = options.Mode == PdfMacroColorMode.BlackAndWhite
            ? PdfImage.FromBitonal(samples.Width, samples.Height, pixels)
            : PdfImage.FromGray(samples.Width, samples.Height, pixels);
        token.ThrowIfCancellationRequested();
        PdfStream encoded = PdfImageXObjectFactory.Create(converted, null);
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (string key in new[] { "Filter", "DecodeParms", "Length", "Decode", "DL", "DecodedLength", "BitsPerComponent", "ColorSpace" })
            entries.Remove(Name(key));
        foreach ((PdfName key, PdfObject value) in encoded.Dictionary) entries[key] = value;
        return new PdfStream(new PdfDictionary(entries), encoded.EncodedData.Span);

        PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 64) throw new InvalidOperationException("An image reference chain is too deeply nested.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }
}
