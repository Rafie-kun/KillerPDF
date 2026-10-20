using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    internal PdfRenderedPage? RenderExportImage(PdfExtractedImage image, CancellationToken cancellationToken)
    {
        if (image.Source is null || image.Placement is not { } placement || image.Resources is null)
            return null;
        if (image.Source.Dictionary.TryGetValue(Name("ImageMask"), out var mask)
            && Resolve(mask) is KillerPdf.Engine.Objects.PdfBoolean { Value: true }) return null;
        PdfContentBounds bounds = image.BoundingBox;
        double scale = Math.Min(2, 2048 / Math.Max(bounds.Width, bounds.Height));
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var pixels = new byte[checked(width * height * 4)];
        var surface = new RasterSurface(pixels, 0, 0, width, height, _pdf20BlendEndpoints);
        var transform = new Matrix(placement.A, placement.B, placement.C, placement.D,
            placement.E - bounds.Left, placement.F - bounds.Bottom);
        var diagnostics = new HashSet<string>();
        if (!TryRenderImage(image.Source, image.Resources, transform, [], Color.Black, 1,
            RendererBlendMode.Normal, null, null, surface, width, height, width / bounds.Width,
            height / bounds.Height, out string? diagnostic, diagnostics: diagnostics,
            cancellationToken: cancellationToken) || diagnostic is not null || diagnostics.Count > 0)
            return null;
        return new PdfRenderedPage(width, height, pixels);
    }
}
