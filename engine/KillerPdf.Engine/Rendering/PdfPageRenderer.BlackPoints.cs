using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private static bool ReadBlackPointPolicy(PdfObject value, HashSet<string> diagnostics)
    {
        if (value is PdfName name)
        {
            switch (name.ValueAsLatin1())
            {
                case "ON": return true;
                case "OFF":
                case "Default": return false;
            }
        }
        diagnostics.Add("The black-point compensation policy is invalid; the default policy was used.");
        return false;
    }

    private static ImageColorSpace BindBlackPointCompensation(ImageColorSpace source,
        ImageColorSpace bound, RasterSurface destination, HashSet<string>? diagnostics)
    {
        PdfColorTransform? target = destination.InputProfile;
        ImageColorSpace result = bound with
        {
            BlackPointControl = new(source, target, null)
        };
        if (bound.DoesNotPaint || bound.NativeProcessMask.HasValue) return result;
        if (bound.PaletteBase is { } paletteBase)
        {
            ImageColorSpace mapped = paletteBase.ForDestination(destination, true, diagnostics);
            return result with
            {
                PaletteBase = mapped,
                Palette = ImageColorSpace.BuildPalette(mapped, bound.PaletteSamples!, bound.Palette!.Length)
            };
        }
        // Native same-profile CMYK binding intentionally removes Profile.
        if (bound.Profile is null && bound.SourceSpace is { Profile: not null } native
            && SameProfile(native.Profile, destination.InkProfile)) return result;
        ImageColorSpace connection = bound;
        while (connection.BlackPointAlternate is { } alternate) connection = alternate;
        bool unprofiledBlack = connection.IsLab || connection.RegistrationColor
            || connection.Converter is null && connection.MultiConverter is null && connection.Components is 1 or 3;
        if (connection.Profile is null && !unprofiledBlack
            || !PdfBlackPointCompensation.TryCreate(connection.Profile, target, bound.Intent, out var mapping))
        {
            diagnostics?.Add("Black-point compensation could not be applied to this color conversion.");
            return result;
        }
        if (mapping.IsIdentity) return result;
        return result with
        {
            BlackPointControl = new(source, target, mapping),
            Converter = bound.MultiConverter is null
                ? (first, second, third, fourth) => CompensateColor(bound.Convert(first, second, third, fourth), mapping)
                : null,
            MultiConverter = bound.MultiConverter is not null
                ? values => CompensateColor(bound.Convert(values), mapping) : null
        };
    }

    private static Color CompensateColor(Color color, PdfBlackPointCompensation mapping)
    {
        if (color.DoesNotPaint || mapping.IsIdentity) return color;
        Span<double> xyz = stackalloc double[3];
        if (color.Connection is { } connection)
        {
            xyz[0] = connection.X;
            xyz[1] = connection.Y;
            xyz[2] = connection.Z;
        }
        else DisplayToXyz(color, xyz);
        mapping.Apply(xyz);
        Color display = IccXyzToDisplay(xyz[0], xyz[1], xyz[2]);
        return color with
        {
            Red = display.Red, Green = display.Green, Blue = display.Blue,
            Connection = (xyz[0], xyz[1], xyz[2])
        };
    }
}
