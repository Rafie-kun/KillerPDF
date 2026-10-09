using System.Runtime.CompilerServices;

namespace KillerPdf.Engine.Rendering;

// Compensation acts on D50 connection values between two profile transforms.
// The sampled black points belong to the profile and intent, never to an image.
internal readonly record struct PdfBlackPointCompensation(double Scale)
{
    private static readonly ConditionalWeakTable<PdfIccProfileTransform, BlackPoints> Points = [];
    internal bool IsIdentity => Scale == 1;

    internal static bool TryCreate(PdfColorTransform? source, PdfColorTransform? destination,
        int intent, out PdfBlackPointCompensation compensation)
    {
        compensation = new(1);
        if (intent is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(intent));
        if (intent == 3 || ReferenceEquals(source, destination)) return true;
        double? sourceL = Read(source, destination: false);
        double? destinationL = Read(destination, destination: true);
        if (!sourceL.HasValue || !destinationL.HasValue) return false;
        double sourceY = LightnessToY(sourceL.Value), destinationY = LightnessToY(destinationL.Value);
        compensation = new((1 - destinationY) / (1 - sourceY));
        return true;

        double? Read(PdfColorTransform? profile, bool destination)
        {
            // The renderer's implicit sRGB display and calibrated matrix spaces
            // have zero connection-space black.
            if (profile is null or PdfCalibratedColorTransform) return 0;
            if (profile is not PdfIccProfileTransform { SupportsBlackPointCompensation: true } icc) return null;
            PdfIccProfileTransform? variant = icc.ForIntent(intent);
            if (variant is null) return null;
            BlackPoints points = Points.GetValue(variant, static profile => new(profile));
            return destination ? points.Destination.Value : points.Source.Value;
        }
    }

    internal void Apply(Span<double> xyz)
    {
        if (xyz.Length != 3) throw new ArgumentException("Connection values must have three channels.");
        if (IsIdentity) return;
        double offset = 1 - Scale;
        xyz[0] = Scale * xyz[0] + offset * 0.9642;
        xyz[1] = Scale * xyz[1] + offset;
        xyz[2] = Scale * xyz[2] + offset * 0.8249;
    }

    private sealed class BlackPoints(PdfIccProfileTransform profile)
    {
        internal readonly Lazy<double?> Source = new(() => SourceLightness(profile));
        internal readonly Lazy<double?> Destination = new(() => DestinationLightness(profile));
    }

    private static double? SourceLightness(PdfIccProfileTransform profile)
    {
        Span<double> local = stackalloc double[profile.Components];
        if (!LocalBlack(profile, local)) return null;
        Span<double> xyz = stackalloc double[3];
        profile.ToXyz(local, xyz);
        return BoundedLightness(xyz[1]);
    }

    private static bool LocalBlack(PdfIccProfileTransform profile, Span<double> local)
    {
        local.Clear();
        if (profile.Components == 4 && profile.CanConvertFromXyz)
        {
            PdfIccProfileTransform? perceptual = profile.ForIntent(0);
            if (perceptual is not { CanConvertFromXyz: true }) return false;
            ReadOnlySpan<double> black = [0, 0, 0];
            perceptual.FromXyz(black, local);
            return true;
        }
        if (profile.IsLabInput) return true;
        Span<double> candidate = stackalloc double[profile.Components];
        Span<double> xyz = stackalloc double[3];
        double minimum = double.PositiveInfinity;
        int count = profile.Components == 4 ? 4 : 2;
        for (int index = 0; index < count; index++)
        {
            candidate.Fill(index == 1 || index == 3 ? 1 : 0);
            if (profile.Components == 4 && index >= 2) candidate[3] = index == 2 ? 1 : 0;
            profile.ToXyz(candidate, xyz);
            if (!double.IsFinite(xyz[1])) return false;
            if (xyz[1] < minimum)
            {
                minimum = xyz[1];
                candidate.CopyTo(local);
            }
        }
        return true;
    }

    private static double? DestinationLightness(PdfIccProfileTransform profile)
    {
        if (!profile.CanConvertFromXyz) return null;
        if (!profile.IsLutBased) return SourceLightness(profile);
        PdfIccProfileTransform? relative = profile.ForIntent(1);
        if (relative is null) return null;
        Span<double> local = stackalloc double[profile.Components];
        Span<double> xyz = stackalloc double[3];
        double initialL = 0, initialA = 0, initialB = 0;
        if (profile.RenderingIntent == 1)
        {
            if (!LocalBlack(profile, local)) return null;
            profile.ToXyz(local, xyz);
            double x = LabForward(xyz[0] / 0.9642), y = LabForward(xyz[1]), z = LabForward(xyz[2] / 0.8249);
            initialL = Math.Min(50, 116 * y - 16);
            if (profile.Components != 4)
            {
                initialA = Math.Clamp(500 * (x - y), -50, 50);
                initialB = Math.Clamp(200 * (y - z), -50, 50);
            }
        }
        Span<double> ramp = stackalloc double[256];
        for (int index = 0; index < ramp.Length; index++)
        {
            double fraction = index / 255d, y = (100 * fraction + 16) / 116;
            xyz[0] = 0.9642 * LabInverse(y + initialA * (1 - fraction) / 500);
            xyz[1] = LabInverse(y);
            xyz[2] = 0.8249 * LabInverse(y - initialB * (1 - fraction) / 200);
            profile.FromXyz(xyz, local);
            relative.ToXyz(local, xyz);
            ramp[index] = 116 * LabForward(xyz[1]) - 16;
            if (!double.IsFinite(ramp[index])) return null;
        }
        for (int index = ramp.Length - 2; index >= 0; index--)
            ramp[index] = Math.Min(ramp[index], ramp[index + 1]);
        double minimum = ramp[0], range = ramp[^1] - minimum;
        if (!(range > 0)) return 0;
        if (profile.RenderingIntent == 1)
        {
            bool straight = true;
            for (int index = 0; index < ramp.Length; index++)
            {
                double input = index * 100d / 255;
                if (input > minimum + 0.2 * range && Math.Abs(input - ramp[index]) >= 4)
                {
                    straight = false;
                    break;
                }
            }
            if (straight) return initialL;
        }
        return FitShadow(ramp, minimum, range, profile.RenderingIntent == 1);
    }

    private static double FitShadow(ReadOnlySpan<double> ramp, double minimum, double range, bool relative)
    {
        // Center and scale input lightness before solving the three normal
        // equations. This keeps the shadow fit away from large fourth powers.
        double lower = relative ? 0.1 : 0.03, upper = relative ? 0.5 : 0.25;
        int count = 0;
        double center = 0;
        for (int index = 0; index < ramp.Length; index++)
        {
            double y = (ramp[index] - minimum) / range;
            if (y < lower || y >= upper) continue;
            center += index / 255d;
            count++;
        }
        if (count < 3) return 0;
        center /= count;
        Span<double> equations = stackalloc double[12];
        equations.Clear();
        for (int index = 0; index < ramp.Length; index++)
        {
            double y = (ramp[index] - minimum) / range;
            if (y < lower || y >= upper) continue;
            double x = index / 255d - center;
            ReadOnlySpan<double> terms = [x * x, x, 1];
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++) equations[row * 4 + column] += terms[row] * terms[column];
                equations[row * 4 + 3] += terms[row] * y;
            }
        }
        for (int column = 0; column < 3; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 3; row++)
                if (Math.Abs(equations[row * 4 + column]) > Math.Abs(equations[pivot * 4 + column])) pivot = row;
            if (Math.Abs(equations[pivot * 4 + column]) < 1e-20) return 0;
            for (int entry = column; entry < 4; entry++)
                (equations[column * 4 + entry], equations[pivot * 4 + entry]) =
                    (equations[pivot * 4 + entry], equations[column * 4 + entry]);
            double divisor = equations[column * 4 + column];
            for (int entry = column; entry < 4; entry++) equations[column * 4 + entry] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == column) continue;
                double factor = equations[row * 4 + column];
                for (int entry = column; entry < 4; entry++) equations[row * 4 + entry] -= factor * equations[column * 4 + entry];
            }
        }
        double a = equations[3] / 10000;
        double b = (equations[7] - 2 * equations[3] * center) / 100;
        double c = equations[11] - equations[7] * center + equations[3] * center * center;
        double root;
        if (Math.Abs(a) < 1e-10) root = b == 0 ? 0 : -c / b;
        else
        {
            double discriminant = b * b - 4 * a * c;
            root = discriminant <= 0 ? 0 : (-b + Math.Sqrt(discriminant)) / (2 * a);
        }
        return double.IsFinite(root) ? Math.Clamp(root, 0, 50) : 0;
    }

    private static double? BoundedLightness(double y)
    {
        double lightness = 116 * LabForward(y) - 16;
        return double.IsFinite(lightness) && lightness >= 0 ? Math.Min(50, lightness) : null;
    }
    private static double LightnessToY(double lightness) => LabInverse((lightness + 16) / 116);
    private static double LabForward(double value) => value > 216d / 24389 ? Math.Cbrt(value) : value * 841 / 108 + 4d / 29;
    private static double LabInverse(double value) => value >= 6d / 29 ? value * value * value : (value - 4d / 29) * 108 / 841;
}
