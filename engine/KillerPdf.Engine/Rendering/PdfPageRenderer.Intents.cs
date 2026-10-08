using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private static int ReadRenderingIntent(PdfName name) => name.ValueAsLatin1() switch
    {
        "Perceptual" => 0,
        "Saturation" => 2,
        "AbsoluteColorimetric" => 3,
        _ => 1
    };

    private int ReadRenderingIntent(PdfDictionary image, int inherited) =>
        image.TryGetValue(Name("Intent"), out PdfObject? value) && Resolve(value) is PdfName name
            ? ReadRenderingIntent(name) : inherited;

    private GraphicsState ApplyRenderingIntent(GraphicsState state, int intent,
        RasterSurface destination, HashSet<string> diagnostics, bool? blackPointCompensation = null)
    {
        bool compensate = blackPointCompensation ?? state.BlackPointCompensation;
        bool policyChanged = compensate != state.BlackPointCompensation;
        if (state.RenderingIntent == intent && !policyChanged) return state;
        using var inputProfile = destination.PrepareInput(intent);
        state = state with { RenderingIntent = intent, BlackPointCompensation = compensate };
        if (state.FillColorSpace is { } fill && (fill.HasIccSource || policyChanged || compensate))
        {
            ImageColorSpace space = Rebind(fill).ForDestination(destination, compensate, diagnostics);
            Color color = state.FillOperands is { } operands
                ? ReadDevicePaint(space, operands, out double[]? components)
                : space.InitialPaint(out components);
            state = state with { FillColorSpace = space, Fill = color, FillComponents = components };
        }
        if (state.StrokeColorSpace is { } stroke && (stroke.HasIccSource || policyChanged || compensate))
        {
            ImageColorSpace space = Rebind(stroke).ForDestination(destination, compensate, diagnostics);
            Color color = state.StrokeOperands is { } operands
                ? ReadDevicePaint(space, operands, out double[]? components)
                : space.InitialPaint(out components);
            state = state with { StrokeColorSpace = space, Stroke = color, StrokeComponents = components };
        }
        if (state.FillPatternBase is { } fillBase && (fillBase.HasIccSource || policyChanged || compensate))
        {
            ImageColorSpace space = Rebind(fillBase).ForDestination(destination, compensate, diagnostics);
            PatternPaint? paint = state.FillPattern;
            if (paint?.BaseColor is not null && state.FillOperands is { } operands)
                paint = paint with { BaseColor = ReadDevicePaint(space, operands, out _) };
            state = state with { FillPatternBase = space, FillPattern = paint };
        }
        if (state.StrokePatternBase is { } strokeBase && (strokeBase.HasIccSource || policyChanged || compensate))
        {
            ImageColorSpace space = Rebind(strokeBase).ForDestination(destination, compensate, diagnostics);
            PatternPaint? paint = state.StrokePattern;
            if (paint?.BaseColor is not null && state.StrokeOperands is { } operands)
                paint = paint with { BaseColor = ReadDevicePaint(space, operands, out _) };
            state = state with { StrokePatternBase = space, StrokePattern = paint };
        }
        return state;

        ImageColorSpace Rebind(ImageColorSpace space) => space.HasIccSource && space.Definition is { } definition
            && space.SourceResources is { } resources
                ? ReadColorSpace(definition, resources, 0, diagnostics: diagnostics, intent: intent)
                : (space.BlackPointSource ?? space) with { Intent = intent };
    }
}
