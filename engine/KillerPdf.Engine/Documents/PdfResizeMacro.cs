using System.Globalization;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Resizes page geometry while keeping PDF text and forms editable.</summary>
public static class PdfResizeMacro
{
    /// <summary>Creates a proportional page resize expressed as a percentage.</summary>
    public static PdfMacroStep ScaleStep(double percent)
    {
        Validate(percent);
        return new(PdfMacroOperation.Resize, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["percent"] = percent.ToString("R", CultureInfo.InvariantCulture) }));
    }

    /// <summary>Reads the proportional resize percentage from a typed step.</summary>
    public static double ReadPercent(PdfMacroStep step)
    {
        if (step.Operation != PdfMacroOperation.Resize || step.Settings is not { Count: 1 }
            || !step.Settings.TryGetValue("percent", out string? text)
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            throw new ArgumentException("Resize requires only a scale percentage.", nameof(step));
        Validate(percent);
        return percent;
    }

    /// <summary>Executes a proportional resize without rasterizing the document.</summary>
    public static PdfMacroOperationResult Execute(PdfMacroStep step, ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double percent = ReadPercent(step);
        byte[] output = PdfPageDimensionNormalizer.ScalePages(PdfDocument.Open(source), percent / 100);
        cancellationToken.ThrowIfCancellationRequested();
        return new(output);
    }

    private static void Validate(double percent)
    {
        if (!double.IsFinite(percent) || percent < 1 || percent > 1000)
            throw new ArgumentOutOfRangeException(nameof(percent), "Resize must be between 1% and 1000%.");
    }
}
