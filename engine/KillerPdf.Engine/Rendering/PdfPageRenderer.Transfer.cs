using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // ISO 32000-2 10.5: transfer functions adjust each device colorant after color conversion.
    // Pages are rendered in RGB, so the red, green and blue functions apply; the fourth entry of a
    // function array is the gray one. The result is held as three 256-entry tables.
    private byte[]? ReadTransfer(PdfDictionary settings, byte[]? current, RasterSurface target,
        HashSet<string> diagnostics)
    {
        if (!settings.TryGetValue(Name("TR2"), out PdfObject? value)
            && !settings.TryGetValue(Name("TR"), out value)) return current;
        PdfObject resolved = Resolve(value);
        if (resolved is PdfName name)
            return name.ValueAsLatin1() is "Identity" or "Default" ? null : current;
        if (target.Ink is not null)
        {
            diagnostics.Add("Transfer functions are not applied to process-color output.");
            return current;
        }
        if (resolved is PdfArray { Count: not 4 })
        {
            diagnostics.Add("A transfer function array was invalid and was ignored.");
            return current;
        }
        try
        {
            var table = new byte[768];
            for (int channel = 0; channel < 3; channel++)
            {
                PdfObject function = resolved is PdfArray functions ? functions[channel] : value;
                if (Resolve(function) is PdfName identity && identity.ValueAsLatin1() == "Identity")
                {
                    for (int index = 0; index < 256; index++) table[channel * 256 + index] = (byte)index;
                    continue;
                }
                Func<double, Color> map = ReadColorFunction(function, new ImageColorSpace(1, null),
                    "transfer function");
                for (int index = 0; index < 256; index++)
                    table[channel * 256 + index] = map(index / 255d).Red;
            }
            return table;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException
            or InvalidDataException or PdfFilterException or InvalidCastException)
        {
            diagnostics.Add("A transfer function could not be read and was ignored.");
            return current;
        }
    }

    private static Color ApplyTransfer(Color color, byte[]? table)
    {
        if (table is null || color.DoesNotPaint) return color;
        return color with
        {
            Red = table[color.Red], Green = table[256 + color.Green], Blue = table[512 + color.Blue],
            Ink = null, ProcessInk = null
        };
    }

    /// <summary>Wraps an image color space so every converted sample passes through the transfer tables.</summary>
    private static ImageColorSpace WithTransfer(ImageColorSpace space, byte[] table)
    {
        if (space.DoesNotPaint) return space;
        if (space.Palette is { } palette)
            return space with { Palette = [.. palette.Select(color => ApplyTransfer(color, table))] };
        if (space.MultiConverter is { } multi)
            return space with { MultiConverter = values => ApplyTransfer(multi(values), table) };
        ImageColorSpace original = space;
        return space with
        {
            Converter = (first, second, third, fourth) =>
                ApplyTransfer(original.Convert(first, second, third, fourth), table),
            Profile = null, IsIccBased = false, IsDeviceCmyk = false
        };
    }
}
