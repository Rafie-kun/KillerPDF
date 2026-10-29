using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Matrix = KillerPdf.Engine.Parsing.PdfTextContentReader.Matrix;

namespace KillerPdf.Engine.Documents;

/// <summary>How a redacted page is rewritten.</summary>
public enum PdfRedactionPageMethod
{
    /// <summary>Content under the regions is removed and the rest of the page stays editable and searchable.</summary>
    Content,
    /// <summary>The page cannot be edited safely and must be replaced by a sanitized image.</summary>
    Raster
}

/// <summary>The redaction analysis for one page that has at least one region.</summary>
public sealed record PdfRedactionPageAnalysis(
    int PageIndex,
    PdfRedactionPageMethod Method,
    IReadOnlyList<string> RasterReasons,
    int RemovedGlyphs,
    int RemovedImages,
    int RemovedPaths,
    int RemovedAnnotations)
{
    /// <summary>Gets the number of partly covered images whose covered pixels are painted over.</summary>
    public int RedactedImages { get; init; }
}

/// <summary>The per-page redaction analysis for a set of regions.</summary>
public sealed record PdfContentRedactionPlan(IReadOnlyList<PdfRedactionPageAnalysis> Pages)
{
    /// <summary>
    /// Gets whether the document carries an XFA form packet, which the redaction removes because its
    /// datasets repeat every field value. The result keeps working as a plain AcroForm.
    /// </summary>
    public bool RemovesXfaForm { get; init; }
    /// <summary>Gets the zero-based pages that can only be redacted by replacing them with an image.</summary>
    public IReadOnlyList<int> RasterPages => Array.AsReadOnly(Pages
        .Where(page => page.Method == PdfRedactionPageMethod.Raster)
        .Select(page => page.PageIndex).ToArray());
}

/// <summary>Options for content-level redaction.</summary>
public sealed record PdfContentRedactionOptions
{
    /// <summary>
    /// Gets the pages the caller approved for image replacement. Every page the analysis marks as
    /// raster must be listed; listing a content page replaces it with an image as well.
    /// </summary>
    public IReadOnlyCollection<int> ApprovedRasterPages { get; init; } = [];
    /// <summary>Gets the resolution of replacement page images.</summary>
    public double RasterDpi { get; init; } = 200;
    /// <summary>Gets whether document information and XMP metadata are removed.</summary>
    public bool ClearDocumentMetadata { get; init; } = true;
    /// <summary>Gets whether signed signature fields may be invalidated by the rewrite.</summary>
    public bool AllowSignatureInvalidation { get; init; }
    /// <summary>Gets whether password encryption is removed from an authenticated source.</summary>
    public bool RemoveEncryption { get; init; }
    /// <summary>Gets text that must not remain anywhere in the output.</summary>
    public IReadOnlyList<string> ProhibitedText { get; init; } = [];
    /// <summary>Gets the font resolver used when rendering replacement page images.</summary>
    public IPdfFontResolver? FontResolver { get; init; }
}

/// <summary>A verified content-level redaction.</summary>
public sealed record PdfContentRedactionResult(
    ReadOnlyMemory<byte> Document,
    PdfContentRedactionPlan Plan,
    PdfRedactionVerificationReport Verification);

/// <summary>Raised when a rewritten document still exposes redacted content.</summary>
/// <param name="report">The failed verification report the exception is created from.</param>
public sealed class PdfRedactionVerificationException(PdfRedactionVerificationReport report)
    : InvalidOperationException("The redacted document failed verification." + Environment.NewLine + report.ToText())
{
    /// <summary>Gets the failed verification report.</summary>
    public PdfRedactionVerificationReport Report { get; } = report;
}

/// <summary>
/// Removes text, images, and drawings under redaction regions at the content level, replaces
/// only approved unsafe pages with sanitized images, and verifies the rewritten file.
/// </summary>
/// <remarks>Regions use unrotated, crop-relative PDF points, the coordinates of <see cref="PdfPageContentReader"/>.</remarks>
public static partial class PdfContentRedaction
{
    internal const string RedactionImageName = "KPRedactImage";
    private const int MaximumFieldDepth = 256;
    /// <summary>The most a retained stream may decode to during prohibited-text inspection.</summary>
    internal static int InspectionDecodeLimit { get; set; } = 64 * 1024 * 1024;
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName ResourcesName = Name("Resources");
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName PopupName = Name("Popup");
    private static readonly PdfName ParentName = Name("Parent");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName XfaName = Name("XFA");
    private static readonly PdfName[] FieldValueKeys = [Name("V"), Name("DV"), Name("RV"), Name("I")];
    private static readonly PdfName StructTreeRootName = Name("StructTreeRoot");
    private static readonly PdfName MetadataName = Name("Metadata");
    private static readonly PdfName InfoName = Name("Info");
    private static readonly PdfName[] RemovedPageKeys = [Name("Thumb"), Name("PieceInfo"), Name("Metadata")];
    private static readonly PdfName[] ReplacementTextKeys = [Name("ActualText"), Name("Alt"), Name("E")];
    private static readonly PdfName[] PrunedCategories =
        [Name("Font"), Name("XObject"), Name("ExtGState"), Name("ColorSpace"), Name("Pattern"), Name("Shading"), Name("Properties")];

    /// <summary>Determines, without changing the document, how each page with regions will be redacted.</summary>
    public static PdfContentRedactionPlan Analyze(PdfDocument document,
        IEnumerable<PdfRedactionRegion> regions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        Dictionary<int, PdfContentBounds[]> byPage = GroupRegions(regions, tree.Pages.Count);
        var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        var pages = new List<PdfRedactionPageAnalysis>();
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageGeometry geometry = Geometry(document, tree.Pages[pageIndex]);
            var rewriter = new ContentRewriter(document, null, pageRegions, textOnly: false, fonts, cancellationToken);
            rewriter.RewritePage(tree.Pages[pageIndex], geometry);
            int annotations = IntersectingAnnotations(document, tree.Pages[pageIndex], geometry, pageRegions).Count;
            pages.Add(new PdfRedactionPageAnalysis(pageIndex,
                rewriter.Reasons.Count == 0 ? PdfRedactionPageMethod.Content : PdfRedactionPageMethod.Raster,
                Array.AsReadOnly(rewriter.Reasons.Order(StringComparer.Ordinal).ToArray()),
                rewriter.RemovedGlyphs, rewriter.RemovedImages, rewriter.RemovedPaths, annotations)
            {
                RedactedImages = rewriter.RedactedImages
            });
        }
        return new PdfContentRedactionPlan(Array.AsReadOnly(pages.ToArray())) { RemovesXfaForm = HasXfaForm(document, tree) };
    }

    private static bool HasXfaForm(PdfDocument document, PdfPageTree tree) =>
        tree.Catalog.TryGetValue(AcroFormName, out PdfObject? value)
        && Resolve(document, value) is PdfDictionary acroForm && acroForm.ContainsKey(XfaName);

    /// <summary>Applies and verifies the redaction, returning a single-revision document.</summary>
    /// <exception cref="InvalidOperationException">A page needs image replacement that was not approved.</exception>
    /// <exception cref="PdfRedactionVerificationException">The rewritten document still exposes redacted content.</exception>
    public static PdfContentRedactionResult Apply(PdfDocument document,
        IEnumerable<PdfRedactionRegion> regions, PdfContentRedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(regions);
        options ??= new PdfContentRedactionOptions();
        if (!double.IsFinite(options.RasterDpi) || options.RasterDpi < 36 || options.RasterDpi > 600)
            throw new ArgumentOutOfRangeException(nameof(options), "The raster resolution must be from 36 through 600 dpi.");
        if (document.IsEncrypted && !options.RemoveEncryption)
            throw new NotSupportedException("Redacting an encrypted document requires removing its encryption.");
        PdfRedactionRegion[] regionList = [.. regions];
        PdfPageTree tree = PdfPageTree.Read(document);
        Dictionary<int, PdfContentBounds[]> byPage = GroupRegions(regionList, tree.Pages.Count);
        PdfContentRedactionPlan plan = Analyze(document, regionList, cancellationToken);
        var approved = new HashSet<int>(options.ApprovedRasterPages);
        int[] unapproved = [.. plan.RasterPages.Where(page => !approved.Contains(page))];
        if (unapproved.Length != 0)
            throw new InvalidOperationException("Pages " + string.Join(", ", unapproved.Select(page => page + 1))
                + " cannot be redacted without replacing them with an image.");
        var rasterPages = new HashSet<int>(approved.Where(byPage.ContainsKey));
        Dictionary<int, PdfRedactionRegion[]> covers = GroupCovers(regionList);

        PdfDocumentWriteResult written;
        IReadOnlyList<PdfReferenceRepair> preflightRepairs = [];
        try { written = Rewrite(document, tree, byPage, covers, rasterPages, options, cancellationToken); }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or NotSupportedException)
        {
            // A damaged cross-reference table or object graph can block the incremental rewrite.
            // Normalize the source with a full rewrite and redact that copy instead.
            PdfDocument source = document.IsEncrypted ? document
                : PdfDocument.OpenWithCompatibilityRecovery(document.Source);
            PdfDocumentWriteResult normalizedWrite = PdfDocumentWriter.WriteWithReport(source, new PdfDocumentWriteOptions
            {
                PruneUnreachableObjects = true,
                AllowSignatureInvalidation = options.AllowSignatureInvalidation,
                RemoveEncryption = options.RemoveEncryption,
                ReplaceDanglingReferences = true
            });
            preflightRepairs = normalizedWrite.ReferenceRepairs;
            PdfDocument normalized = PdfDocument.Open(normalizedWrite.Data);
            written = Rewrite(normalized, PdfPageTree.Read(normalized), byPage, covers, rasterPages, options, cancellationToken);
        }
        byte[] output = written.Bytes;
        PdfRedactionVerificationReport verification = Verify(output, regionList, rasterPages,
            options.ProhibitedText, options.ClearDocumentMetadata, cancellationToken) with
        {
            ReferenceRepairs = Array.AsReadOnly(preflightRepairs.Concat(written.ReferenceRepairs).ToArray())
        };
        if (!verification.Succeeded) throw new PdfRedactionVerificationException(verification);
        return new PdfContentRedactionResult(output, plan, verification);
    }

    /// <summary>
    /// Removes the text drawn under opaque covers, such as the cover of an in-place text edit, and
    /// returns the document with an incremental update, or null when no text lies under them.
    /// </summary>
    /// <remarks>
    /// A glyph goes when its center lies inside a region. Images, drawings, annotations and every
    /// page resource stay, and text in a font that cannot be decoded is kept. Regions use unrotated
    /// PDF points relative to the corner given by <see cref="PdfPageInformation.Left"/> and
    /// <see cref="PdfPageInformation.Bottom"/>.
    /// </remarks>
    public static byte[]? RemoveCoveredText(PdfDocument document, IEnumerable<PdfRedactionRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(regions);
        PdfPageTree tree = PdfPageTree.Read(document);
        Dictionary<int, PdfContentBounds[]> byPage = GroupRegions(regions, tree.Pages.Count);
        if (byPage.Count == 0) return null;
        IReadOnlyList<PdfPageInformation> boxes = PdfPageInformation.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        bool changed = false;
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            PdfPageInformation box = boxes[pageIndex];
            var rewriter = new ContentRewriter(document, update, pageRegions, textOnly: false, fonts,
                cancellationToken, glyphsOnly: true);
            List<PdfContentInstruction> content = rewriter.RewritePage(page,
                new PageGeometry(box.Left, box.Bottom, box.Width, box.Height, box.Rotation));
            if (rewriter.RemovedGlyphs == 0) continue;
            var instructions = new List<PdfContentInstruction>(content.Count + 2) { Op("q") };
            instructions.AddRange(content);
            instructions.Add(Op("Q"));
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(PdfContentStreamWriter.Write(instructions));
            PdfIndirectReference contentReference = update.AddObject(new PdfStream(
                new PdfDictionary(new Dictionary<PdfName, PdfObject> { [Name("Filter")] = Name("FlateDecode") }),
                compressed.ToArray()));
            var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ContentsName] = contentReference;
            entries[ResourcesName] = rewriter.PageScope!.Build();
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
            changed = true;
        }
        return changed ? update.Build() : null;
    }

    private static PdfDocumentWriteResult Rewrite(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfContentBounds[]> byPage, Dictionary<int, PdfRedactionRegion[]> covers,
        HashSet<int> rasterPages, PdfContentRedactionOptions options, CancellationToken cancellationToken)
    {
        Dictionary<int, PdfImage> images = rasterPages.Count == 0 ? []
            : RenderRasterPages(document, tree, covers, rasterPages, options, cancellationToken);
        PdfIndirectReference? overlayFont = null;

        var update = new PdfIncrementalUpdateBuilder(document);
        var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        var removedAnnotations = new HashSet<int>();
        var removedWidgets = new HashSet<int>();
        var retiredStreams = new HashSet<int>();
        var retiredDescriptions = new HashSet<string>(StringComparer.Ordinal);
        var touchedContent = new Dictionary<int, (HashSet<int> Mcids, bool Whole)>();
        MaterializeInheritedResources(document, tree, update, byPage.Keys.ToHashSet(), cancellationToken);
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            PageGeometry geometry = Geometry(document, page);
            bool raster = rasterPages.Contains(pageIndex);
            var rewriter = new ContentRewriter(document, update, pageRegions, raster, fonts, cancellationToken,
                fills: [.. covers[pageIndex].Select(cover => cover.FillColor ?? DefaultFill)]);
            List<PdfContentInstruction> content = rewriter.RewritePage(page, geometry);
            if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? originalContents))
                CollectSourceStreams(document, originalContents, retiredStreams);
            retiredStreams.UnionWith(rewriter.RetiredSourceStreams);
            retiredDescriptions.UnionWith(rewriter.RetiredDescriptions);
            if (!raster && rewriter.Reasons.Count != 0)
                throw new InvalidOperationException($"Page {pageIndex + 1} changed while it was being redacted.");
            var instructions = new List<PdfContentInstruction>(content.Count + 16) { Op("q") };
            instructions.AddRange(content);
            instructions.Add(Op("Q"));
            PdfDictionary resources = rewriter.PageScope!.Build();
            if (raster)
            {
                PdfIndirectReference imageReference = update.AddObject(PdfImageXObjectFactory.Create(images[pageIndex], null));
                resources = WithEntry(resources, Name("XObject"), Name(RedactionImageName), imageReference, document);
                instructions.Add(Op("q"));
                instructions.Add(Op("cm", geometry.Width, 0, 0, geometry.Height, geometry.CropLeft, geometry.CropBottom));
                instructions.Add(new PdfContentInstruction("Do", 0, [Name(RedactionImageName)]));
                instructions.Add(Op("Q"));
            }
            // Each area is filled with its color, and its overlay text is drawn upright as real text.
            PdfRedactionRegion[] pageCovers = covers[pageIndex];
            if (pageCovers.Any(cover => Overlay(cover, geometry) is not null))
            {
                overlayFont ??= update.AddObject(new PdfDictionary(new Dictionary<PdfName, PdfObject>
                {
                    [Name("Type")] = Name("Font"),
                    [Name("Subtype")] = Name("Type1"),
                    [Name("BaseFont")] = Name(OverlayFontFamily),
                    [Name("Encoding")] = Name("WinAnsiEncoding")
                }));
                resources = WithEntry(resources, Name("Font"), Name(OverlayFontName), overlayFont, document);
            }
            foreach (PdfRedactionRegion cover in pageCovers)
                AddCover(instructions, cover, geometry);
            PdfIndirectReference contentReference = update.AddObject(
                new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(instructions)));
            var entries = page.Dictionary
                .Where(entry => !RemovedPageKeys.Contains(entry.Key) && !entry.Key.Equals(ContentsName)
                    && !entry.Key.Equals(ResourcesName) && !entry.Key.Equals(AnnotsName))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ContentsName] = contentReference;
            entries[ResourcesName] = resources;
            List<PdfObject> removed = IntersectingAnnotations(document, page, geometry, pageRegions);
            PdfArray? annotations = RemainingAnnotations(document, page, removed, removedAnnotations, removedWidgets);
            if (annotations is not null) entries[AnnotsName] = annotations;
            // A widget the field tree places on this page but the page does not list cannot be shown to be
            // clear of the areas, so it leaves with its value instead of blocking the page.
            foreach (PdfObject widget in CoveredFieldWidgets(document, tree, pageIndex, geometry, pageRegions))
                if (widget is PdfIndirectReference orphan)
                {
                    removedWidgets.Add(orphan.ObjectNumber);
                    removedAnnotations.Add(orphan.ObjectNumber);
                }
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
            touchedContent[page.Reference.ObjectNumber] = (rewriter.TouchedMcids, raster || rewriter.FormMarkedContentTouched);
        }

        PdfDictionary catalog = tree.Catalog;
        var catalogEntries = catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
        bool catalogChanged = false;
        if (catalog.TryGetValue(AcroFormName, out PdfObject? acroFormValue))
            catalogChanged |= PruneFields(document, update, acroFormValue, removedWidgets, catalogEntries);
        if (catalog.TryGetValue(StructTreeRootName, out PdfObject? structureValue))
            catalogChanged |= CleanStructure(document, update, structureValue, touchedContent, removedAnnotations, catalogEntries);
        if (catalogChanged)
            update.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalogEntries));

        PdfDocument edited = PdfDocument.Open(update.Build());
        PdfDocumentWriteResult output = PdfDocumentWriter.WriteWithReport(edited, new PdfDocumentWriteOptions
        {
            PruneUnreachableObjects = true,
            ReplaceDanglingReferences = true,
            AllowSignatureInvalidation = options.AllowSignatureInvalidation,
            RemoveEncryption = options.RemoveEncryption,
            MetadataPolicy = options.ClearDocumentMetadata
                ? PdfMetadataPolicy.RemoveDocumentInformationAndXmp : PdfMetadataPolicy.Preserve,
            PreserveDocumentIdentifiers = !options.ClearDocumentMetadata,
            CompressUnfilteredStreams = true
        });
        VerifyRetiredStreams(PdfDocument.Open(output.Data), retiredStreams, retiredDescriptions, cancellationToken);
        return output;
    }

    /// <summary>
    /// Reads a redacted output back and reports any text, vector, image, shading, or annotation that still
    /// intersects a region, any revision history, and any remaining prohibited text.
    /// </summary>
    public static PdfRedactionVerificationReport Verify(ReadOnlyMemory<byte> output,
        IEnumerable<PdfRedactionRegion> regions, IReadOnlyCollection<int> rasterPages,
        IEnumerable<string>? prohibitedText = null, bool expectMetadataCleared = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(rasterPages);
        PdfRedactionRegion[] regionList = [.. regions];
        string[] prohibited = [.. (prohibitedText ?? []).Where(value => !string.IsNullOrWhiteSpace(value))];
        var findings = new List<PdfRedactionVerificationFinding>();
        PdfDocument document = PdfDocument.Open(output);
        if (document.CrossReferences.Sections.Count != 1)
            findings.Add(new("IncrementalRevisions", "The output contains incremental revision history."));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (expectMetadataCleared && document.Trailer.ContainsKey(InfoName))
            findings.Add(new("DocumentInformation", "The output contains a document-information dictionary."));
        if (expectMetadataCleared && tree.Catalog.ContainsKey(MetadataName))
            findings.Add(new("XmpMetadata", "The output contains XMP metadata."));
        if (HasXfaForm(document, tree))
            findings.Add(new("XfaFormData", "The output still carries an XFA form packet, whose datasets repeat field values."));
        Dictionary<int, PdfContentBounds[]> byPage = GroupRegions(regionList, tree.Pages.Count);
        Dictionary<int, PdfRedactionRegion[]> covers = GroupCovers(regionList);
        var reader = new PdfPageContentReader(document);
        var vectorFonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageContent content = reader.Read(pageIndex, cancellationToken);
            PageGeometry pageGeometry = Geometry(document, tree.Pages[pageIndex]);
            var vectors = new ContentRewriter(document, null, pageRegions, textOnly: false, vectorFonts, cancellationToken);
            vectors.RewritePage(tree.Pages[pageIndex], pageGeometry, covers[pageIndex]);
            if (vectors.RemovedPaths != 0 || vectors.Reasons.Contains("Vector"))
                findings.Add(new("VectorInRegion", "Vector geometry remains inside a redacted area.", pageIndex));
            if (vectors.Reasons.Contains("RedactionCover"))
                findings.Add(new("RedactionCover", "An expected redaction cover is missing or changed.", pageIndex));
            if (TextRemains(content.Letters, pageRegions, covers[pageIndex], pageGeometry))
                findings.Add(new("TextInRegion", "Text remains inside a redacted area.", pageIndex));
            bool raster = rasterPages.Contains(pageIndex);
            foreach (PdfExtractedImage image in content.Images)
            {
                if (raster && image.ResourceName == RedactionImageName) continue;
                PdfRedactionRegion[] hits = [.. covers[pageIndex].Where(cover => Overlaps(image.BoundingBox, cover.Bounds))];
                // An image may stay over a region only when every covered pixel is painted with the fill.
                if (hits.Length != 0 && !IsPainted(document, image, hits))
                {
                    findings.Add(new("ImageInRegion", "An image remains inside a redacted area.", pageIndex));
                    break;
                }
            }
            if (raster && content.Images.Count(image => image.ResourceName == RedactionImageName) != 1)
                findings.Add(new("RasterPage", "The replaced page does not contain exactly one sanitized image.", pageIndex));
            foreach (PdfExtractedShading shading in content.Shadings)
                if (pageRegions.Any(region => Overlaps(shading.BoundingBox, region)))
                {
                    findings.Add(new("ShadingInRegion", "A shading remains inside a redacted area.", pageIndex));
                    break;
                }
            if (IntersectingAnnotations(document, tree.Pages[pageIndex], pageGeometry, pageRegions).Count != 0)
                findings.Add(new("AnnotationInRegion", "An annotation remains over a redacted area.", pageIndex));
            if (FieldWidgetRemains(document, tree, pageIndex, pageGeometry, pageRegions))
                findings.Add(new("FieldInRegion", "A form field widget remains over a redacted area.", pageIndex));
            foreach (PdfName key in RemovedPageKeys)
                if (tree.Pages[pageIndex].Dictionary.ContainsKey(key))
                    findings.Add(new("PageData", $"The redacted page still contains /{key.ValueAsLatin1()}.", pageIndex));
        }
        if (prohibited.Length != 0)
        {
            for (int pageIndex = 0; pageIndex < tree.Pages.Count; pageIndex++)
            {
                string text = reader.Read(pageIndex, cancellationToken).Text;
                foreach (string value in prohibited)
                    if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                        findings.Add(new("ProhibitedText", $"Page text still contains '{value}'.", pageIndex));
            }
            InspectObjects(document, prohibited, findings, cancellationToken);
        }
        return new PdfRedactionVerificationReport(Array.AsReadOnly(findings.ToArray()));
    }

    private static void InspectObjects(PdfDocument document, string[] prohibited,
        List<PdfRedactionVerificationFinding> findings, CancellationToken cancellationToken)
    {
        var reported = new HashSet<(int, string)>();
        foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse
                or PdfCrossReferenceEntryType.Compressed)) continue;
            PdfObject value;
            try { value = document.Resolve(entry.ObjectNumber); }
            catch (Exception error) when (error is FormatException or InvalidOperationException)
            {
                findings.Add(new("ObjectInspectionFailure", $"Object {entry.ObjectNumber} could not be read.",
                    ObjectNumber: entry.ObjectNumber));
                continue;
            }
            Inspect(value, entry.ObjectNumber, 0);
        }

        void Inspect(PdfObject value, int objectNumber, int depth)
        {
            if (depth > 256) return;
            switch (value)
            {
                case PdfString text:
                    Check(PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "Object text"), objectNumber);
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) Inspect(item, objectNumber, depth + 1);
                    break;
                case PdfStream stream:
                    Inspect(stream.Dictionary, objectNumber, depth + 1);
                    if (IsNamed(stream.Dictionary, SubtypeName, "Image")) break;
                    byte[] decoded;
                    try { decoded = PdfStreamDecoder.Decode(stream, document.Resolve, InspectionDecodeLimit); }
                    catch (Exception error) when (error is PdfFilterException or FormatException
                        or InvalidDataException or NotSupportedException)
                    {
                        // Data that cannot be decoded cannot be shown to be free of the prohibited text.
                        findings.Add(new("ObjectInspectionFailure",
                            $"Stream object {objectNumber} could not be inspected: {error.Message}",
                            ObjectNumber: objectNumber));
                        break;
                    }
                    Check(Encoding.Latin1.GetString(decoded), objectNumber);
                    Check(Encoding.BigEndianUnicode.GetString(decoded), objectNumber);
                    break;
                case PdfDictionary dictionary:
                    foreach (PdfObject item in dictionary.Values) Inspect(item, objectNumber, depth + 1);
                    break;
            }
        }

        void Check(string candidate, int objectNumber)
        {
            foreach (string blocked in prohibited)
                if (candidate.Contains(blocked, StringComparison.OrdinalIgnoreCase) && reported.Add((objectNumber, blocked)))
                    findings.Add(new("ProhibitedObjectText",
                        $"Object {objectNumber} still contains '{blocked}'.", ObjectNumber: objectNumber));
        }
    }

    private static Dictionary<int, PdfImage> RenderRasterPages(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfRedactionRegion[]> covers, HashSet<int> rasterPages,
        PdfContentRedactionOptions options, CancellationToken cancellationToken)
    {
        // Render from a copy whose replaced pages carry the filled regions, so the boxes are in the pixels.
        var boxed = new PdfIncrementalUpdateBuilder(document);
        foreach (int pageIndex in rasterPages)
        {
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            PageGeometry geometry = Geometry(document, page);
            var items = new List<PdfObject> { boxed.AddObject(new PdfStream(new PdfDictionary([]), "q\n"u8)) };
            if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? contents))
            {
                if (Resolve(document, contents) is PdfArray array) items.AddRange(array);
                else items.Add(contents);
            }
            var tail = new List<PdfContentInstruction> { Op("Q") };
            foreach (PdfRedactionRegion cover in covers[pageIndex])
            {
                PdfContentBounds region = cover.Bounds;
                PdfRgbColor fill = cover.FillColor ?? DefaultFill;
                tail.Add(Op("rg", fill.Red, fill.Green, fill.Blue));
                tail.Add(Op("re", region.Left + geometry.CropLeft, region.Bottom + geometry.CropBottom, region.Width, region.Height));
                tail.Add(Op("f"));
            }
            items.Add(boxed.AddObject(new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(tail))));
            var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ContentsName] = new PdfArray(items);
            boxed.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        }
        PdfDocument boxedDocument = PdfDocument.Open(boxed.Build());
        var renderer = new PdfPageRenderer(boxedDocument, options.FontResolver);
        var images = new Dictionary<int, PdfImage>();
        foreach (int pageIndex in rasterPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageGeometry geometry = Geometry(document, tree.Pages[pageIndex]);
            bool quarter = geometry.Rotation is 90 or 270;
            double dpi = options.RasterDpi;
            int width, height;
            while (true)
            {
                width = Math.Max(1, (int)Math.Ceiling(geometry.Width * dpi / 72));
                height = Math.Max(1, (int)Math.Ceiling(geometry.Height * dpi / 72));
                if (width <= PdfRenderOptions.MaximumDimension && height <= PdfRenderOptions.MaximumDimension
                    && (long)width * height * 4 <= PdfRenderOptions.MaximumPixelBytes) break;
                dpi /= 2;
            }
            var render = new PdfRenderOptions(quarter ? height : width, quarter ? width : height,
                includeAnnotations: false, includeFormFields: false) { CacheResult = false };
            PdfRenderedPage rendered = renderer.Render(pageIndex, render, cancellationToken);
            images[pageIndex] = PdfImage.FromRgb(width, height, Unrotate(rendered, geometry.Rotation, width, height));
        }
        return images;
    }

    /// <summary>Converts a display-oriented BGRA page bitmap into unrotated RGB samples.</summary>
    internal static byte[] Unrotate(PdfRenderedPage rendered, int rotation, int width, int height)
    {
        ReadOnlySpan<byte> source = rendered.Pixels.Span;
        int sourceWidth = rendered.Width;
        var rgb = new byte[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = rotation switch
                {
                    90 => (height - 1 - y, x),
                    180 => (width - 1 - x, height - 1 - y),
                    270 => (y, width - 1 - x),
                    _ => (x, y)
                };
                int from = (sy * sourceWidth + sx) * 4, to = (y * width + x) * 3;
                rgb[to] = source[from + 2];
                rgb[to + 1] = source[from + 1];
                rgb[to + 2] = source[from];
            }
        return rgb;
    }

    private static List<PdfObject> IntersectingAnnotations(PdfDocument document, PdfPageTreeEntry page,
        PageGeometry geometry, PdfContentBounds[] regions)
    {
        var result = new List<PdfObject>();
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? value)
            || Resolve(document, value) is not PdfArray annotations) return result;
        foreach (PdfObject item in annotations)
        {
            if (Resolve(document, item) is not PdfDictionary annotation
                || IsNamed(annotation, SubtypeName, "Popup")) continue;
            // An annotation that cannot be placed cannot be shown to be clear of the area, so it counts as covered.
            if (!TryAnnotationBounds(document, annotation, geometry, out PdfContentBounds bounds)
                || regions.Any(region => OverlapsOrTouches(bounds, region))) result.Add(item);
        }
        return result;
    }

    private static bool TryAnnotationBounds(PdfDocument document, PdfDictionary annotation, PageGeometry geometry,
        out PdfContentBounds bounds)
    {
        bounds = default;
        if (!annotation.TryGetValue(RectName, out PdfObject? rectValue)
            || Resolve(document, rectValue) is not PdfArray rect || rect.Count != 4) return false;
        double[] values = new double[4];
        for (int index = 0; index < 4; index++)
        {
            switch (Resolve(document, rect[index]))
            {
                case PdfInteger integer: values[index] = integer.Value; break;
                case PdfReal real when double.IsFinite(real.Value): values[index] = real.Value; break;
                default: return false;
            }
        }
        bounds = new PdfContentBounds(Math.Min(values[0], values[2]) - geometry.CropLeft,
            Math.Min(values[1], values[3]) - geometry.CropBottom,
            Math.Max(values[0], values[2]) - geometry.CropLeft, Math.Max(values[1], values[3]) - geometry.CropBottom);
        return true;
    }

    // Walks the whole field tree, since a widget dropped from the page can stay reachable through it.
    private static bool FieldWidgetRemains(PdfDocument document, PdfPageTree tree, int pageIndex,
        PageGeometry geometry, PdfContentBounds[] regions) =>
        CoveredFieldWidgets(document, tree, pageIndex, geometry, regions).Any();

    // Yields every field-tree node that is a widget of the page and overlaps a region or cannot be placed.
    private static IEnumerable<PdfObject> CoveredFieldWidgets(PdfDocument document, PdfPageTree tree, int pageIndex,
        PageGeometry geometry, PdfContentBounds[] regions)
    {
        if (!tree.Catalog.TryGetValue(AcroFormName, out PdfObject? acroFormValue)
            || Resolve(document, acroFormValue) is not PdfDictionary acroForm
            || !acroForm.TryGetValue(FieldsName, out PdfObject? fieldsValue)
            || Resolve(document, fieldsValue) is not PdfArray fields) yield break;
        int pageNumber = tree.Pages[pageIndex].Reference.ObjectNumber;
        var visited = new HashSet<int>();
        var pending = new Stack<PdfObject>(fields);
        while (pending.Count != 0)
        {
            PdfObject item = pending.Pop();
            if (item is PdfIndirectReference reference && !visited.Add(reference.ObjectNumber)) continue;
            if (Resolve(document, item) is not PdfDictionary field) continue;
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue) && Resolve(document, kidsValue) is PdfArray kids)
                foreach (PdfObject kid in kids) pending.Push(kid);
            if (!field.TryGetValue(Name("P"), out PdfObject? owner)
                || owner is not PdfIndirectReference { ObjectNumber: var ownerNumber } || ownerNumber != pageNumber
                || !(IsNamed(field, SubtypeName, "Widget") || field.ContainsKey(RectName))) continue;
            if (!TryAnnotationBounds(document, field, geometry, out PdfContentBounds bounds)
                || regions.Any(region => OverlapsOrTouches(bounds, region))) yield return item;
        }
    }

    private static PdfArray? RemainingAnnotations(PdfDocument document, PdfPageTreeEntry page,
        List<PdfObject> removed, HashSet<int> removedAnnotations, HashSet<int> removedWidgets)
    {
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? value)) return null;
        if (Resolve(document, value) is not PdfArray annotations) return null;
        var drop = new HashSet<PdfObject>(removed, ReferenceEqualityComparer.Instance);
        var droppedNumbers = new HashSet<int>();
        foreach (PdfObject item in removed)
        {
            if (item is PdfIndirectReference reference)
            {
                droppedNumbers.Add(reference.ObjectNumber);
                removedAnnotations.Add(reference.ObjectNumber);
            }
            if (Resolve(document, item) is not PdfDictionary annotation) continue;
            if (IsNamed(annotation, SubtypeName, "Widget") && item is PdfIndirectReference widget)
                removedWidgets.Add(widget.ObjectNumber);
            if (annotation.TryGetValue(PopupName, out PdfObject? popup) && popup is PdfIndirectReference popupReference)
            {
                droppedNumbers.Add(popupReference.ObjectNumber);
                removedAnnotations.Add(popupReference.ObjectNumber);
            }
        }
        var kept = new List<PdfObject>();
        foreach (PdfObject item in annotations)
        {
            if (drop.Contains(item)) continue;
            if (item is PdfIndirectReference reference && droppedNumbers.Contains(reference.ObjectNumber)) continue;
            if (Resolve(document, item) is PdfDictionary annotation
                && annotation.TryGetValue(ParentName, out PdfObject? parent)
                && parent is PdfIndirectReference parentReference
                && droppedNumbers.Contains(parentReference.ObjectNumber)
                && IsNamed(annotation, SubtypeName, "Popup")) continue;
            kept.Add(item);
        }
        return kept.Count == 0 ? null : new PdfArray(kept);
    }

    // Drops removed widgets from the field tree, clears the value of a field that lost one of its widgets,
    // and removes the XFA packet, whose datasets repeat every field value. The form keeps working as a
    // plain AcroForm, which is also what Acrobat requires before it redacts.
    private static bool PruneFields(PdfDocument document, PdfIncrementalUpdateBuilder update,
        PdfObject acroFormValue, HashSet<int> removedWidgets, Dictionary<PdfName, PdfObject> catalogEntries)
    {
        if (Resolve(document, acroFormValue) is not PdfDictionary acroForm) return false;
        var removedFields = new HashSet<int>(removedWidgets);
        PdfArray? pruned = removedWidgets.Count != 0 && acroForm.TryGetValue(FieldsName, out PdfObject? fieldsValue)
            && Resolve(document, fieldsValue) is PdfArray fields ? PruneKids(fields, 0, out _) : null;
        if (pruned is null && !acroForm.ContainsKey(XfaName)) return false;
        var entries = acroForm.Where(entry => !entry.Key.Equals(XfaName))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        if (pruned is not null) entries[FieldsName] = pruned;
        PdfName calculationOrder = Name("CO");
        if (acroForm.TryGetValue(calculationOrder, out PdfObject? orderValue)
            && Resolve(document, orderValue) is PdfArray order)
            entries[calculationOrder] = new PdfArray(order.Where(item =>
                item is not PdfIndirectReference reference || !removedFields.Contains(reference.ObjectNumber)));
        var replacement = new PdfDictionary(entries);
        if (acroFormValue is PdfIndirectReference acroFormReference)
        {
            update.ReplaceObject(acroFormReference.ObjectNumber, replacement);
            return false;
        }
        catalogEntries[AcroFormName] = replacement;
        return true;

        PdfArray? PruneKids(PdfArray kids, int depth, out bool removedKid)
        {
            // Stopping early would leave a removed widget, and its value, reachable from the form.
            if (depth > MaximumFieldDepth)
                throw new NotSupportedException("The form field hierarchy is too deep to redact safely.");
            bool changed = false;
            removedKid = false;
            var kept = new List<PdfObject>();
            foreach (PdfObject kid in kids)
            {
                if (kid is PdfIndirectReference reference && removedFields.Contains(reference.ObjectNumber))
                {
                    changed = true;
                    removedKid = true;
                    continue;
                }
                if (kid is PdfIndirectReference fieldReference
                    && Resolve(document, kid) is PdfDictionary field
                    && field.TryGetValue(KidsName, out PdfObject? childrenValue)
                    && Resolve(document, childrenValue) is PdfArray children)
                {
                    PdfArray? prunedChildren = PruneKids(children, depth + 1, out bool removedChild);
                    if (prunedChildren is not null)
                    {
                        changed = true;
                        if (prunedChildren.Count == 0)
                        {
                            removedFields.Add(fieldReference.ObjectNumber);
                            continue;
                        }
                        // The value of a field with several widgets lives on the field, so it goes with the widget.
                        var fieldEntries = field.Where(entry => !removedChild || !FieldValueKeys.Contains(entry.Key))
                            .ToDictionary(entry => entry.Key, entry => entry.Value);
                        fieldEntries[KidsName] = prunedChildren;
                        update.ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(fieldEntries));
                    }
                }
                kept.Add(kid);
            }
            return changed ? new PdfArray(kept) : null;
        }
    }

    private static bool CleanStructure(PdfDocument document, PdfIncrementalUpdateBuilder update,
        PdfObject rootValue, Dictionary<int, (HashSet<int> Mcids, bool Whole)> touched,
        HashSet<int> removedAnnotations, Dictionary<PdfName, PdfObject> catalogEntries)
    {
        if (Resolve(document, rootValue) is not PdfDictionary root
            || !root.TryGetValue(Name("K"), out PdfObject? kids)) return false;
        var visited = new HashSet<int>();
        (PdfObject rootKids, bool rootChanged, _) = Clean(kids, null, 0);
        if (!rootChanged) return false;
        var entries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("K")] = rootKids;
        var replacement = new PdfDictionary(entries);
        if (rootValue is PdfIndirectReference rootReference)
        {
            update.ReplaceObject(rootReference.ObjectNumber, replacement);
            return false;
        }
        catalogEntries[StructTreeRootName] = replacement;
        return true;

        // Returns the possibly rewritten value, whether it changed, and whether it covers redacted content.
        (PdfObject Value, bool Changed, bool Touched) Clean(PdfObject value, int? page, int depth)
        {
            if (depth > 256) return (value, false, false);
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add(reference.ObjectNumber)) return (value, false, false);
                (PdfObject inner, bool changed, bool hit) = Clean(document.Resolve(reference), page, depth + 1);
                if (changed) update.ReplaceObject(reference.ObjectNumber, inner);
                return (value, false, hit);
            }
            if (value is PdfInteger mcid)
                return (value, false, page is int pageNumber && touched.TryGetValue(pageNumber, out var hits)
                    && (hits.Whole || hits.Mcids.Contains((int)mcid.Value)));
            if (value is PdfArray array)
            {
                bool changed = false, hit = false;
                var items = new List<PdfObject>();
                foreach (PdfObject item in array)
                {
                    if (IsRemovedObjectReference(item)) { changed = true; continue; }
                    (PdfObject cleanedItem, bool itemChanged, bool itemHit) = Clean(item, page, depth + 1);
                    changed |= itemChanged;
                    hit |= itemHit;
                    items.Add(cleanedItem);
                }
                return (changed ? new PdfArray(items) : value, changed, hit);
            }
            if (value is not PdfDictionary dictionary) return (value, false, false);
            int? ownPage = dictionary.TryGetValue(Name("Pg"), out PdfObject? pg) && pg is PdfIndirectReference pageReference
                ? pageReference.ObjectNumber : page;
            if (IsNamed(dictionary, Name("Type"), "MCR"))
            {
                bool inForm = dictionary.ContainsKey(Name("Stm"));
                bool hit = ownPage is int mcrPage && touched.TryGetValue(mcrPage, out var mcrHits)
                    && (mcrHits.Whole || inForm
                        || dictionary.TryGetValue(Name("MCID"), out PdfObject? id) && id is PdfInteger number
                            && mcrHits.Mcids.Contains((int)number.Value));
                return (value, false, hit);
            }
            if (IsNamed(dictionary, Name("Type"), "OBJR")) return (value, false, false);
            bool childChanged = false, childHit = false;
            PdfObject? newKids = null;
            if (dictionary.TryGetValue(Name("K"), out PdfObject? elementKids))
            {
                if (IsRemovedObjectReference(elementKids))
                {
                    childChanged = true;
                    newKids = new PdfArray([]);
                }
                else
                {
                    (newKids, childChanged, childHit) = Clean(elementKids, ownPage, depth + 1);
                }
            }
            bool strip = childHit && ReplacementTextKeys.Any(dictionary.ContainsKey);
            if (!childChanged && !strip) return (value, false, childHit);
            var entries = dictionary.Where(entry => !strip || !ReplacementTextKeys.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            if (newKids is not null) entries[Name("K")] = newKids;
            return (new PdfDictionary(entries), true, childHit);
        }

        bool IsRemovedObjectReference(PdfObject item)
        {
            PdfObject resolved = item is PdfIndirectReference reference ? document.Resolve(reference) : item;
            return resolved is PdfDictionary objectReference && IsNamed(objectReference, Name("Type"), "OBJR")
                && objectReference.TryGetValue(Name("Obj"), out PdfObject? target)
                && target is PdfIndirectReference targetReference
                && removedAnnotations.Contains(targetReference.ObjectNumber);
        }
    }

    private static Dictionary<int, PdfContentBounds[]> GroupRegions(IEnumerable<PdfRedactionRegion> regions, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var result = new Dictionary<int, List<PdfContentBounds>>();
        foreach (PdfRedactionRegion region in regions)
        {
            ArgumentNullException.ThrowIfNull(region, nameof(regions));
            PdfContentBounds bounds = region.Bounds;
            if (region.PageIndex < 0 || region.PageIndex >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction region refers to a missing page.");
            if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(bounds.Right)
                || !double.IsFinite(bounds.Top) || bounds.Width <= 0 || bounds.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction region must have a positive finite size.");
            if (region.FillColor is PdfRgbColor fill && !(fill.Red is >= 0 and <= 1 && fill.Green is >= 0 and <= 1
                && fill.Blue is >= 0 and <= 1))
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction fill color must use components from 0 through 1.");
            if (!result.TryGetValue(region.PageIndex, out List<PdfContentBounds>? list))
                result[region.PageIndex] = list = [];
            list.Add(bounds);
        }
        return result.ToDictionary(item => item.Key, item => item.Value.ToArray());
    }

    private static bool IsPainted(PdfDocument document, PdfExtractedImage image, PdfRedactionRegion[] hits)
    {
        if (image.Source is not PdfStream source || image.Placement is not Matrix placement
            || PdfImageRedaction.Decode(document, source) is not PdfImageRedaction.Samples samples) return false;
        PdfImageRedaction.Plan? plan = PdfImageRedaction.Covers(placement, samples,
            hits.Select(hit => (hit.Bounds, hit.FillColor ?? DefaultFill)));
        return plan is not null && PdfImageRedaction.IsPainted(samples, plan);
    }

    private static Dictionary<int, PdfRedactionRegion[]> GroupCovers(IEnumerable<PdfRedactionRegion> regions) =>
        regions.GroupBy(region => region.PageIndex).ToDictionary(group => group.Key, group => group.ToArray());

    private const string OverlayFontFamily = "Helvetica";
    private const string OverlayFontName = "KPRedactFont";
    private const double MaximumOverlaySize = 12;
    private const double MinimumOverlaySize = 3;
    private const double OverlayPadding = 2;
    private static readonly PdfRgbColor DefaultFill = new(0, 0, 0);

    /// <summary>True when overlay text can be drawn exactly with the standard overlay font.</summary>
    public static bool SupportsOverlayText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PdfWinAnsiText.CanEncode(text);
    }

    private readonly record struct OverlayPlacement(byte[] Text, double Size, double[] TextMatrix);

    // The overlay text, sized to fit and centered, reads upright on the displayed page.
    private static OverlayPlacement? Overlay(PdfRedactionRegion region, PageGeometry geometry)
    {
        string? text = (region.OverlayText ?? region.ReasonCode)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        byte[] bytes = PdfWinAnsiText.Encode(text);
        double unit = PdfWinAnsiText.Width(OverlayFontFamily, bytes);
        PdfContentBounds bounds = region.Bounds;
        bool quarter = geometry.Rotation is 90 or 270;
        double across = quarter ? bounds.Height : bounds.Width, up = quarter ? bounds.Width : bounds.Height;
        double size = Math.Min(MaximumOverlaySize, up * 0.7);
        if (unit > 0) size = Math.Min(size, (across - 2 * OverlayPadding) / unit);
        if (!(size >= MinimumOverlaySize)) return null;
        double u = (across - unit * size) / 2, v = (up - size * 0.7) / 2;
        double x = bounds.Left + geometry.CropLeft, y = bounds.Bottom + geometry.CropBottom;
        double w = bounds.Width, h = bounds.Height;
        (double tx, double ty, double cos, double sin) = geometry.Rotation switch
        {
            90 => (x + w - v, y + u, 0d, 1d),
            180 => (x + w - u, y + h - v, -1d, 0d),
            270 => (x + v, y + h - u, 0d, -1d),
            _ => (x + u, y + v, 1d, 0d)
        };
        return new OverlayPlacement(bytes, size, [cos, sin, -sin, cos, tx, ty]);
    }

    private static void AddCover(List<PdfContentInstruction> instructions, PdfRedactionRegion region, PageGeometry geometry)
    {
        PdfContentBounds bounds = region.Bounds;
        double x = bounds.Left + geometry.CropLeft, y = bounds.Bottom + geometry.CropBottom;
        PdfRgbColor fill = region.FillColor ?? DefaultFill;
        instructions.Add(Op("q"));
        instructions.Add(Op("rg", fill.Red, fill.Green, fill.Blue));
        instructions.Add(Op("re", x, y, bounds.Width, bounds.Height));
        instructions.Add(Op("f"));
        if (Overlay(region, geometry) is OverlayPlacement overlay)
        {
            instructions.Add(Op("re", x, y, bounds.Width, bounds.Height));
            instructions.Add(Op("W"));
            instructions.Add(Op("n"));
            double ink = 0.2126 * fill.Red + 0.7152 * fill.Green + 0.0722 * fill.Blue < 0.5 ? 1 : 0;
            instructions.Add(Op("rg", ink, ink, ink));
            instructions.Add(Op("BT"));
            instructions.Add(new PdfContentInstruction("Tf", 0, [Name(OverlayFontName), new PdfReal(overlay.Size)]));
            instructions.Add(Op("Tm", overlay.TextMatrix));
            instructions.Add(new PdfContentInstruction("Tj", 0, [new PdfString(overlay.Text, PdfStringForm.Hexadecimal)]));
            instructions.Add(Op("ET"));
        }
        instructions.Add(Op("Q"));
    }

    // Text in a redacted area fails verification unless it is exactly the overlay text drawn there:
    // every letter must sit in an area with overlay text, and together they must spell those texts.
    private static bool TextRemains(IReadOnlyList<PdfExtractedLetter> letters, PdfContentBounds[] regions,
        PdfRedactionRegion[] covers, PageGeometry geometry)
    {
        PdfContentBounds[] overlaid = [.. covers.Where(cover => Overlay(cover, geometry) is not null)
            .Select(cover => cover.Bounds)];
        var found = new List<char>();
        foreach (PdfExtractedLetter letter in letters)
        {
            if (!regions.Any(region => Overlaps(letter.BoundingBox, region))) continue;
            if (!overlaid.Any(region => ContainsCenter(letter.BoundingBox, region))) return true;
            found.AddRange(letter.Value.Where(character => !char.IsWhiteSpace(character)));
        }
        if (found.Count == 0) return false;
        var expected = new List<char>();
        foreach (PdfRedactionRegion cover in covers)
            if (Overlay(cover, geometry) is OverlayPlacement overlay)
                expected.AddRange(PdfWinAnsiText.Decode(overlay.Text).Where(character => !char.IsWhiteSpace(character)));
        found.Sort();
        expected.Sort();
        // Letters may only come from the overlays, never more than they draw.
        int next = 0;
        foreach (char character in found)
        {
            while (next < expected.Count && expected[next] < character) next++;
            if (next >= expected.Count || expected[next] != character) return true;
            next++;
        }
        return false;
    }

    internal readonly record struct PageGeometry(double CropLeft, double CropBottom, double Width, double Height, int Rotation);

    private static PageGeometry Geometry(PdfDocument document, PdfPageTreeEntry page)
    {
        PdfObject box = page.InheritedValues.TryGetValue(Name("CropBox"), out PdfObject? crop) ? crop
            : page.InheritedValues[Name("MediaBox")];
        if (Resolve(document, box) is not PdfArray array || array.Count != 4)
            throw new FormatException("Invalid page bounds.");
        double x1 = Number(document, array[0]), y1 = Number(document, array[1]);
        double x2 = Number(document, array[2]), y2 = Number(document, array[3]);
        int rotation = page.InheritedValues.TryGetValue(Name("Rotate"), out PdfObject? rotate)
            && Resolve(document, rotate) is PdfInteger degrees ? (int)(((degrees.Value % 360) + 360) % 360) : 0;
        if (rotation % 90 != 0) rotation = 0;
        return new PageGeometry(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1), rotation);
    }

    private static PdfDictionary WithEntry(PdfDictionary resources, PdfName category, PdfName key,
        PdfObject value, PdfDocument document)
    {
        var entries = resources.ToDictionary(entry => entry.Key, entry => entry.Value);
        var inner = entries.TryGetValue(category, out PdfObject? existing) && Resolve(document, existing) is PdfDictionary dictionary
            ? dictionary.ToDictionary(entry => entry.Key, entry => entry.Value) : [];
        inner[key] = value;
        entries[category] = new PdfDictionary(inner);
        return new PdfDictionary(entries);
    }

    // Overlaps smaller than this are rounding noise from rewritten text positions, not visible content.
    private const double OverlapTolerance = 1e-4;

    internal static bool Overlaps(PdfContentBounds content, PdfContentBounds region) =>
        content.Right > region.Left + OverlapTolerance && content.Left < region.Right - OverlapTolerance
        && content.Top > region.Bottom + OverlapTolerance && content.Bottom < region.Top - OverlapTolerance;

    private static bool OverlapsOrTouches(PdfContentBounds content, PdfContentBounds region) =>
        content.Right >= region.Left && content.Left <= region.Right
        && content.Top >= region.Bottom && content.Bottom <= region.Top;

    private static bool ContainsCenter(PdfContentBounds content, PdfContentBounds region)
    {
        double x = (content.Left + content.Right) / 2, y = (content.Bottom + content.Top) / 2;
        return x >= region.Left && x <= region.Right && y >= region.Bottom && y <= region.Top;
    }

    private static bool Inside(PdfContentBounds content, PdfContentBounds region) =>
        content.Left >= region.Left && content.Right <= region.Right
        && content.Bottom >= region.Bottom && content.Top <= region.Top;

    private static bool IsNamed(PdfDictionary dictionary, PdfName key, string value) =>
        dictionary.TryGetValue(key, out PdfObject? item) && item is PdfName name && name.ValueAsLatin1() == value;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static double Number(PdfDocument document, PdfObject value) => Resolve(document, value) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("Expected a number.")
    };

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfContentInstruction Op(string op, params double[] values) =>
        new(op, 0, values.Select(value => value == Math.Truncate(value) && Math.Abs(value) < 1e15
            ? (PdfObject)new PdfInteger((long)value) : new PdfReal(value)));

    /// <summary>Resource names used by one rewritten content scope.</summary>
    private sealed class ResourceScope(PdfDocument document, PdfDictionary resources)
    {
        private readonly Dictionary<PdfName, HashSet<PdfName>> _used = [];
        private readonly Dictionary<PdfName, Dictionary<PdfName, PdfObject>> _added = [];
        private int _counter;

        internal PdfDictionary Resources { get; } = resources;
        internal bool KeepEverything { get; set; }
        internal bool ReplacedNamedDescription { get; set; }

        internal PdfObject? Lookup(PdfName category, PdfName key) =>
            Resources.TryGetValue(category, out PdfObject? value) && Resolve(document, value) is PdfDictionary items
            && items.TryGetValue(key, out PdfObject? item) ? item : null;

        internal void Use(PdfName category, PdfName key)
        {
            if (!_used.TryGetValue(category, out HashSet<PdfName>? names)) _used[category] = names = [];
            names.Add(key);
        }

        internal PdfName Add(PdfName category, string prefix, PdfObject value)
        {
            PdfName key;
            do key = Name(prefix + ++_counter);
            while (Lookup(category, key) is not null
                || _added.TryGetValue(category, out var existing) && existing.ContainsKey(key));
            if (!_added.TryGetValue(category, out Dictionary<PdfName, PdfObject>? entries))
                _added[category] = entries = [];
            entries[key] = value;
            return key;
        }

        internal PdfDictionary Build()
        {
            var entries = new Dictionary<PdfName, PdfObject>();
            foreach (KeyValuePair<PdfName, PdfObject> entry in Resources)
            {
                if (KeepEverything || !PrunedCategories.Contains(entry.Key))
                {
                    entries[entry.Key] = entry.Value;
                    continue;
                }
                if (Resolve(document, entry.Value) is not PdfDictionary items) continue;
                HashSet<PdfName> used = _used.GetValueOrDefault(entry.Key) ?? [];
                var kept = items.Where(item => used.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value);
                if (kept.Count != 0) entries[entry.Key] = new PdfDictionary(kept);
            }
            foreach ((PdfName category, Dictionary<PdfName, PdfObject> added) in _added)
            {
                var merged = entries.TryGetValue(category, out PdfObject? existing) && Resolve(document, existing) is PdfDictionary items
                    ? items.ToDictionary(item => item.Key, item => item.Value) : [];
                foreach ((PdfName key, PdfObject value) in added) merged[key] = value;
                entries[category] = new PdfDictionary(merged);
            }
            return new PdfDictionary(entries);
        }
    }

    private sealed record TextState(Matrix Ctm, PdfContentBounds Clip, PdfExtractionFont? Font = null,
        bool FontMissing = false, double Size = 0, double CharacterSpacing = 0, double WordSpacing = 0,
        double Scale = 1, double Leading = 0, double Rise = 0)
    {
        // The font as a resource value and, when Tf selected it, its resource name.
        internal PdfObject? FontObject { get; init; }
        internal PdfName? FontResource { get; init; }
        // The operators that set the current nonstroking color, replayed to restore it.
        internal PdfContentInstruction[] Fill { get; init; } = [];
        internal int RenderMode { get; init; }
        internal double LineWidth { get; init; } = 1;
        internal double MiterLimit { get; init; } = 10;
        internal double LineJoin { get; init; }
        internal double LineCap { get; init; }
    }

    /// <summary>Rewrites page and form content so nothing drawn inside a region survives.</summary>
    /// <remarks>With <paramref name="glyphsOnly"/> only text goes: a glyph whose center lies in a
    /// region is removed, while images, drawings and every resource stay as they were.</remarks>
    private sealed class ContentRewriter(PdfDocument document, PdfIncrementalUpdateBuilder? update,
        PdfContentBounds[] regions, bool textOnly, Dictionary<PdfDictionary, PdfExtractionFont?> fonts,
        CancellationToken cancellationToken, bool glyphsOnly = false, PdfRgbColor[]? fills = null,
        LineInsertion? insertion = null, Dictionary<object, Dictionary<uint, PdfDecodedCharacter>>? usedCodes = null)
    {
        private static readonly PdfName FontCategory = Name("Font");
        private static readonly PdfName XObjectCategory = Name("XObject");
        private static readonly PdfName ExtGStateCategory = Name("ExtGState");
        private static readonly PdfName ColorSpaceCategory = Name("ColorSpace");
        private static readonly PdfName PatternCategory = Name("Pattern");
        private static readonly PdfName ShadingCategory = Name("Shading");
        private static readonly PdfName PropertiesCategory = Name("Properties");
        private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
        private int _instructions;

        internal HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        internal HashSet<int> RetiredSourceStreams { get; } = [];
        internal HashSet<string> RetiredDescriptions { get; } = new(StringComparer.Ordinal);
        internal HashSet<int> TouchedMcids { get; } = [];
        internal bool FormMarkedContentTouched { get; private set; }
        internal int RemovedGlyphs { get; private set; }
        internal int RemovedImages { get; private set; }
        internal int RedactedImages { get; private set; }
        internal int RemovedPaths { get; private set; }
        internal ResourceScope? PageScope { get; private set; }

        internal List<PdfContentInstruction> RewritePage(PdfPageTreeEntry page, PageGeometry geometry,
            IReadOnlyList<PdfRedactionRegion>? excludedCovers = null)
        {
            PdfDictionary resources = page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? value)
                && Resolve(document, value) is PdfDictionary dictionary ? dictionary : new PdfDictionary([]);
            PageScope = new ResourceScope(document, resources) { KeepEverything = glyphsOnly };
            byte[] bytes = page.Dictionary.TryGetValue(ContentsName, out PdfObject? contents) ? PageBytes(contents) : [];
            if (excludedCovers is not null)
            {
                // Exempt only the exact appended covers, never an earlier matching source path.
                var expected = new List<PdfContentInstruction>();
                foreach (PdfRedactionRegion cover in excludedCovers) AddCover(expected, cover, geometry);
                IReadOnlyList<PdfContentInstruction> instructions = PdfContentStreamReader.Read(bytes,
                    resolveColorComponents: name => ColorComponents(PageScope, name),
                    cancellationToken: cancellationToken, compatibilityRecovery: document.UsesCompatibilityRecovery);
                int start = instructions.Count - expected.Count;
                if (start >= 0 && PdfContentStreamWriter.Write(instructions.Skip(start)).AsSpan()
                        .SequenceEqual(PdfContentStreamWriter.Write(expected)))
                    bytes = PdfContentStreamWriter.Write(instructions.Take(start));
                else
                    Reasons.Add("RedactionCover");
            }
            var initial = new TextState(new Matrix(1, 0, 0, 1, -geometry.CropLeft, -geometry.CropBottom),
                new PdfContentBounds(0, 0, geometry.Width, geometry.Height));
            return Rewrite(bytes, PageScope, initial, 0, out _);
        }

        private byte[] PageBytes(PdfObject contents)
        {
            using var output = new MemoryStream();
            PdfObject resolved = Resolve(document, contents);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            foreach (PdfObject item in items)
            {
                if (Resolve(document, item) is not PdfStream stream) continue;
                output.Write(document.DecodeStream(stream, PdfContentStreamReader.MaximumSourceBytes));
                output.WriteByte((byte)'\n');
                if (output.Length > PdfContentStreamReader.MaximumSourceBytes)
                    throw new FormatException("Page content exceeds the redaction limit.");
            }
            return output.ToArray();
        }

        private List<PdfContentInstruction> Rewrite(byte[] bytes, ResourceScope scope, TextState initial,
            int depth, out bool changed)
        {
            changed = false;
            var output = new List<PdfContentInstruction>();
            var stack = new Stack<TextState>();
            var marked = new Stack<(int OutputIndex, PdfDictionary? Properties, bool Touched, int? Mcid)>();
            var pendingPath = new List<PdfContentInstruction>();
            var pathPoints = new List<PdfPoint>();
            bool pendingClip = false;
            TextState state = initial;
            Matrix text = Matrix.Identity, line = Matrix.Identity;
            bool insideText = false;
            IReadOnlyList<PdfContentInstruction> instructions = PdfContentStreamReader.Read(bytes,
                resolveColorComponents: name => ColorComponents(scope, name),
                compatibilityRecovery: document.UsesCompatibilityRecovery, cancellationToken: cancellationToken);
            bool localChanged = false;
            foreach (PdfContentInstruction instruction in instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++_instructions > 2_000_000) throw new FormatException("Redaction instruction limit exceeded.");
                IReadOnlyList<PdfObject> args = instruction.Operands;
                switch (instruction.Operator)
                {
                    case "q":
                        stack.Push(state); Emit(instruction); break;
                    case "Q":
                        if (stack.TryPop(out TextState? restored)) state = restored;
                        Emit(instruction); break;
                    case "cm":
                        if (args.Count == 6)
                            state = state with { Ctm = new Matrix(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5])).Then(state.Ctm) };
                        Emit(instruction); break;
                    case "BT":
                        insertion?.BeginTextObject();
                        insideText = true; text = line = Matrix.Identity; Emit(instruction);
                        if (textOnly) { output.Add(Op("Tr", 3)); localChanged = true; }
                        break;
                    case "ET":
                        insideText = false; Emit(instruction);
                        if (insertion?.AfterText is { } after && insertion.AfterTextDepth == depth)
                        {
                            output.AddRange(after);
                            insertion.AfterText = null;
                        }
                        break;
                    case "Tf":
                        if (args.Count == 2 && args[0] is PdfName fontName)
                        {
                            scope.Use(FontCategory, fontName);
                            PdfObject? fontObject = scope.Lookup(FontCategory, fontName);
                            PdfExtractionFont? font = ReadFont(fontObject);
                            state = state with
                            {
                                Font = font, FontMissing = font is null, Size = N(args[1]),
                                FontObject = fontObject, FontResource = fontName
                            };
                        }
                        Emit(instruction); break;
                    case "Tc": state = state with { CharacterSpacing = Arg(args, 0) }; Emit(instruction); break;
                    case "Tw": state = state with { WordSpacing = Arg(args, 0) }; Emit(instruction); break;
                    case "Tz": state = state with { Scale = Arg(args, 0) / 100 }; Emit(instruction); break;
                    case "TL": state = state with { Leading = Arg(args, 0) }; Emit(instruction); break;
                    case "Ts": state = state with { Rise = Arg(args, 0) }; Emit(instruction); break;
                    case "Tr":
                        if (textOnly) { output.Add(Op("Tr", 3)); localChanged = true; }
                        else
                        {
                            if (args.Count == 1) state = state with { RenderMode = (int)N(args[0]) };
                            Emit(instruction);
                        }
                        break;
                    case "Tm":
                        if (args.Count == 6) text = line = new Matrix(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5]));
                        Emit(instruction); break;
                    case "Td":
                        if (args.Count == 2) text = line = line.Move(N(args[0]), N(args[1]));
                        Emit(instruction); break;
                    case "TD":
                        if (args.Count == 2) { state = state with { Leading = -N(args[1]) }; text = line = line.Move(N(args[0]), N(args[1])); }
                        Emit(instruction); break;
                    case "T*":
                        text = line = line.Move(0, -state.Leading); Emit(instruction); break;
                    case "Tj":
                        if (args.Count == 1) ShowAndEmit(instruction, [], args[0]);
                        else Emit(instruction);
                        break;
                    case "'":
                        if (args.Count != 1) { Emit(instruction); break; }
                        text = line = line.Move(0, -state.Leading);
                        ShowAndEmit(instruction, [Op("T*")], args[0]);
                        break;
                    case "\"":
                        if (args.Count != 3) { Emit(instruction); break; }
                        state = state with { WordSpacing = N(args[0]), CharacterSpacing = N(args[1]) };
                        text = line = line.Move(0, -state.Leading);
                        ShowAndEmit(instruction, [new PdfContentInstruction("Tw", 0, [args[0]]),
                            new PdfContentInstruction("Tc", 0, [args[1]]), Op("T*")], args[2]);
                        break;
                    case "TJ":
                        if (args.Count == 1 && args[0] is PdfArray array) ShowArray(instruction, array);
                        else Emit(instruction);
                        break;
                    case "gs":
                        GraphicsState(instruction);
                        break;
                    case "Do":
                        Draw(instruction);
                        break;
                    case "BI":
                        InlineImage(instruction);
                        break;
                    case "sh":
                        if (args.Count == 1 && args[0] is PdfName shadingName)
                        {
                            if (textOnly) { MarkRemoved(); break; }
                            if (!glyphsOnly && regions.Any(region => Overlaps(state.Clip, region)))
                            {
                                if (regions.Any(region => Inside(state.Clip, region))) { MarkRemoved(); RemovedPaths++; break; }
                                Reasons.Add("Shading");
                            }
                            scope.Use(ShadingCategory, shadingName);
                        }
                        Emit(instruction); break;
                    case "m": case "l": case "c": case "v": case "y": case "re": case "h":
                        PathSegment(instruction); break;
                    case "W": case "W*":
                        pendingClip = true; pendingPath.Add(instruction); break;
                    case "n": case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                        PaintPath(instruction); break;
                    case "CS": case "cs":
                        if (textOnly) { localChanged = true; break; }
                        if (args.Count == 1 && args[0] is PdfName space && !IsDeviceSpace(space))
                            scope.Use(ColorSpaceCategory, space);
                        if (instruction.Operator == "cs") state = state with { Fill = [instruction] };
                        Emit(instruction); break;
                    case "SC": case "SCN": case "sc": case "scn": case "G": case "g": case "RG": case "rg": case "K": case "k":
                        if (textOnly) { localChanged = true; break; }
                        if (args.Count > 0 && args[^1] is PdfName pattern) scope.Use(PatternCategory, pattern);
                        if (instruction.Operator is "g" or "rg" or "k") state = state with { Fill = [instruction] };
                        else if (instruction.Operator is "sc" or "scn")
                            state = state with
                            {
                                Fill = state.Fill.Length > 0 && state.Fill[0].Operator == "cs"
                                    ? [state.Fill[0], instruction] : [instruction]
                            };
                        Emit(instruction); break;
                    case "BMC":
                        marked.Push((output.Count, null, false, null)); Emit(instruction); break;
                    case "BDC":
                        MarkedContent(instruction); break;
                    case "EMC":
                        EndMarkedContent(); Emit(instruction); break;
                    case "MP":
                        Emit(instruction); break;
                    case "DP":
                        if (args.Count == 2 && args[1] is PdfName point) scope.Use(PropertiesCategory, point);
                        Emit(instruction); break;
                    case "w": case "J": case "j": case "M":
                        if (textOnly) { localChanged = true; break; }
                        if (!glyphsOnly) state = instruction.Operator switch
                        {
                            "w" => state with { LineWidth = Arg(args, 0) },
                            "J" => state with { LineCap = Arg(args, 0) },
                            "j" => state with { LineJoin = Arg(args, 0) },
                            _ => state with { MiterLimit = Arg(args, 0) }
                        };
                        Emit(instruction); break;
                    case "d": case "ri": case "i":
                        if (textOnly) { localChanged = true; break; }
                        Emit(instruction); break;
                    case "BX": case "EX": case "d0": case "d1":
                        Emit(instruction); break;
                    default:
                        if (args.Any(item => item is PdfName)) scope.KeepEverything = true;
                        Emit(instruction); break;
                }
            }
            while (marked.Count > 0) EndMarkedContent();
            if (!glyphsOnly && scope.KeepEverything && scope.ReplacedNamedDescription)
                Reasons.Add("MarkedContentResource");
            changed = localChanged;
            return output;

            void Emit(PdfContentInstruction instruction) => output.Add(instruction);

            double N(PdfObject value) => value switch
            {
                PdfInteger integer => integer.Value,
                PdfReal real => real.Value,
                _ => throw new FormatException("Expected a numeric content operand.")
            };

            double Arg(IReadOnlyList<PdfObject> values, int index) =>
                values.Count > index ? N(values[index]) : throw new FormatException("Missing content operand.");

            void MarkRemoved()
            {
                localChanged = true;
                var frames = marked.ToArray();
                marked.Clear();
                for (int index = frames.Length - 1; index >= 0; index--)
                {
                    var frame = frames[index];
                    if (frame.Mcid is int mcid)
                    {
                        if (depth == 0) TouchedMcids.Add(mcid);
                        else FormMarkedContentTouched = true;
                    }
                    marked.Push(frame with { Touched = true });
                }
                if (depth > 0 && frames.Length == 0) FormMarkedContentTouched = true;
            }

            void MarkedContent(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                PdfDictionary? properties = null;
                if (args.Count == 2 && args[1] is PdfDictionary inline) properties = inline;
                else if (args.Count == 2 && args[1] is PdfName propertyName)
                {
                    properties = scope.Lookup(PropertiesCategory, propertyName) is { } found
                        ? Resolve(document, found) as PdfDictionary : null;
                }
                int? mcid = properties is not null && properties.TryGetValue(Name("MCID"), out PdfObject? id)
                    && id is PdfInteger number ? (int)number.Value : null;
                marked.Push((output.Count, properties, false, mcid));
                Emit(instruction);
            }

            void EndMarkedContent()
            {
                if (!marked.TryPop(out var frame)) return;
                PdfContentInstruction original = output[frame.OutputIndex];
                if (!frame.Touched || frame.Properties is null
                    || !ReplacementTextKeys.Any(frame.Properties.ContainsKey))
                {
                    KeepNamedProperty();
                    return;
                }
                var stripped = frame.Properties.Where(entry => !ReplacementTextKeys.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                PdfObject sanitized = stripped.Values.Any(ContainsReference)
                    ? scope.Add(PropertiesCategory, "KPRP", new PdfDictionary(stripped))
                    : new PdfDictionary(stripped);
                output[frame.OutputIndex] = new PdfContentInstruction("BDC", 0,
                    [original.Operands[0], sanitized]);
                RetiredDescriptions.Add(DescriptionIdentity(frame.Properties));
                if (original.Operands.Count == 2 && original.Operands[1] is PdfName)
                {
                    scope.ReplacedNamedDescription = true;
                }
                localChanged = true;

                void KeepNamedProperty()
                {
                    if (original.Operands.Count == 2 && original.Operands[1] is PdfName name)
                        scope.Use(PropertiesCategory, name);
                }
            }

            void ShowAndEmit(PdfContentInstruction instruction, PdfContentInstruction[] prefix, PdfObject value)
            {
                if (!insideText || value is not PdfString source) { Emit(instruction); return; }
                List<PdfObject>? items = Show(source, out bool removed);
                if (!removed || items is null) { Emit(instruction); return; }
                output.AddRange(prefix);
                output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray(Merge(items))]));
                insertion?.WritePending(output, state, text, line, scope, depth);
            }

            void ShowArray(PdfContentInstruction instruction, PdfArray array)
            {
                if (!insideText) { Emit(instruction); return; }
                var items = new List<PdfObject>();
                bool removedAny = false;
                foreach (PdfObject item in array)
                {
                    if (item is PdfString source)
                    {
                        List<PdfObject>? shown = Show(source, out bool removed);
                        removedAny |= removed;
                        if (shown is null) items.Add(item);
                        else items.AddRange(shown);
                    }
                    else if (item is PdfInteger or PdfReal)
                    {
                        double adjustment = N(item);
                        text = state.Font?.IsVertical == true ? text.Move(0, -adjustment / 1000 * state.Size)
                            : text.Move(-adjustment / 1000 * state.Size * state.Scale, 0);
                        items.Add(item);
                    }
                }
                if (!removedAny) { Emit(instruction); return; }
                output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray(Merge(items))]));
                insertion?.WritePending(output, state, text, line, scope, depth);
            }

            List<PdfObject>? Show(PdfString source, out bool removed)
            {
                removed = false;
                PdfExtractionFont? font = state.Font;
                if (font is null)
                {
                    if (source.Bytes.Length > 0) Reasons.Add("UnreadableFont");
                    return null;
                }
                IReadOnlyList<PdfDecodedCharacter> characters;
                try { characters = font.Decode(source.Bytes); }
                catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
                {
                    Reasons.Add("UndecodableText");
                    return null;
                }
                if (characters.Sum(character => character.ByteLength) != source.Bytes.Length)
                {
                    Reasons.Add("UndecodableText");
                    return null;
                }
                var items = new List<PdfObject>();
                var kept = new List<byte>();
                int offset = 0;
                Dictionary<uint, PdfDecodedCharacter>? used = null;
                if (usedCodes is not null && FontKey(document, state.FontObject) is { } key
                    && !usedCodes.TryGetValue(key, out used))
                    usedCodes[key] = used = [];
                foreach (PdfDecodedCharacter character in characters)
                {
                    used?.TryAdd(character.Code, character);
                    double width = font.GetWidth(character.Code) / 1000 * state.Size * state.Scale;
                    PdfVerticalGlyphMetrics vertical = font.GetVerticalMetrics(character.Code);
                    double advanceY = font.IsVertical ? vertical.Advance / 1000 * state.Size : 0;
                    double originX = font.IsVertical ? -vertical.OriginX / 1000 * state.Size * state.Scale : 0;
                    double originY = font.IsVertical ? -vertical.OriginY / 1000 * state.Size : 0;
                    Matrix transform = text.Then(state.Ctm);
                    PdfGlyphBounds? glyph = font.GetGlyphBounds(character.Code);
                    double left = (glyph?.Left ?? 0) / 1000 * state.Size * state.Scale + originX;
                    double right = (glyph?.Right ?? font.GetWidth(character.Code)) / 1000 * state.Size * state.Scale + originX;
                    double bottom = (glyph?.Bottom ?? font.Descent) / 1000 * state.Size + state.Rise + originY;
                    double top = (glyph?.Top ?? font.Ascent) / 1000 * state.Size + state.Rise + originY;
                    PdfPoint[] corners = [transform.Point(left, bottom), transform.Point(right, bottom),
                        transform.Point(left, top), transform.Point(right, top)];
                    var bounds = new PdfContentBounds(corners.Min(p => p.X), corners.Min(p => p.Y),
                        corners.Max(p => p.X), corners.Max(p => p.Y));
                    double spacing = state.CharacterSpacing;
                    if (character.ByteLength == 1 && character.Code == 32) spacing += state.WordSpacing;
                    bool covered = glyphsOnly
                        ? regions.Any(region => ContainsCenter(bounds, region))
                        : regions.Any(region => Overlaps(bounds, region));
                    if (covered)
                    {
                        if (state.Size == 0) Reasons.Add("ZeroSizeText");
                        insertion?.Observe(state, text);
                        Flush();
                        double displacement = font.IsVertical
                            ? vertical.Advance + (state.Size == 0 ? 0 : spacing * 1000 / state.Size)
                            : font.GetWidth(character.Code) + (state.Size == 0 ? 0 : spacing * 1000 / state.Size);
                        items.Add(new PdfReal(-displacement));
                        removed = true;
                        RemovedGlyphs++;
                    }
                    else kept.AddRange(source.Bytes.Slice(offset, character.ByteLength).ToArray());
                    offset += character.ByteLength;
                    text = font.IsVertical ? text.Move(0, advanceY + spacing) : text.Move(width + spacing * state.Scale, 0);
                }
                Flush();
                if (removed) MarkRemoved();
                return items;

                void Flush()
                {
                    if (kept.Count == 0) return;
                    items.Add(new PdfString(kept.ToArray(), source.Form));
                    kept.Clear();
                }
            }

            void GraphicsState(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                if (args.Count != 1 || args[0] is not PdfName name) { Emit(instruction); return; }
                PdfDictionary? gs = scope.Lookup(ExtGStateCategory, name) is { } value
                    ? Resolve(document, value) as PdfDictionary : null;
                if (gs is not null)
                {
                    if (gs.TryGetValue(Name("LW"), out PdfObject? width))
                        state = state with { LineWidth = Number(document, width) };
                    if (gs.TryGetValue(Name("ML"), out PdfObject? miter))
                        state = state with { MiterLimit = Number(document, miter) };
                    if (gs.TryGetValue(Name("LJ"), out PdfObject? join))
                        state = state with { LineJoin = Number(document, join) };
                    if (gs.TryGetValue(Name("LC"), out PdfObject? cap))
                        state = state with { LineCap = Number(document, cap) };
                }
                PdfObject? fontValue = null;
                if (gs is not null && gs.TryGetValue(Name("Font"), out PdfObject? fontEntry)
                    && Resolve(document, fontEntry) is PdfArray fontArray && fontArray.Count == 2)
                {
                    fontValue = fontArray[0];
                    PdfExtractionFont? font = ReadFont(fontValue);
                    state = state with
                    {
                        Font = font, FontMissing = font is null, Size = Number(document, fontArray[1]),
                        FontObject = fontValue, FontResource = null
                    };
                }
                if (textOnly)
                {
                    localChanged = true;
                    if (fontValue is not null)
                    {
                        PdfName alias = scope.Add(FontCategory, "KPRF", fontValue);
                        output.Add(new PdfContentInstruction("Tf", 0, [alias, new PdfReal(state.Size)]));
                    }
                    return;
                }
                if (gs is not null && gs.TryGetValue(Name("SMask"), out PdfObject? maskValue)
                    && Resolve(document, maskValue) is PdfDictionary mask
                    && mask.TryGetValue(Name("G"), out PdfObject? groupValue)
                    && Resolve(document, groupValue) is PdfStream group
                    && group.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                    && Resolve(document, boxValue) is PdfArray box && box.Count == 4)
                {
                    PdfContentBounds maskBounds = Transform(new PdfContentBounds(
                        Math.Min(Number(document, box[0]), Number(document, box[2])),
                        Math.Min(Number(document, box[1]), Number(document, box[3])),
                        Math.Max(Number(document, box[0]), Number(document, box[2])),
                        Math.Max(Number(document, box[1]), Number(document, box[3]))), state.Ctm);
                    if (regions.Any(region => Overlaps(maskBounds, region))) Reasons.Add("SoftMask");
                }
                scope.Use(ExtGStateCategory, name);
                Emit(instruction);
            }

            void Draw(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                if (args.Count != 1 || args[0] is not PdfName name) { Emit(instruction); return; }
                PdfObject? value = scope.Lookup(XObjectCategory, name);
                if (value is null || Resolve(document, value) is not PdfStream xobject)
                {
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                if (IsNamed(xobject.Dictionary, SubtypeName, "Image"))
                {
                    PdfContentBounds bounds = Visible(Transform(new PdfContentBounds(0, 0, 1, 1), state.Ctm));
                    // A dropped or repainted image is retired with its masks, so the original samples
                    // cannot stay behind through a resource dictionary another page shares or inherits.
                    if (textOnly)
                    {
                        MarkRemoved();
                        if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                        return;
                    }
                    if (!glyphsOnly && bounds.Width > 0 && bounds.Height > 0
                        && regions.Any(region => Overlaps(bounds, region)))
                    {
                        if (regions.Any(region => Inside(bounds, region)))
                        {
                            RemovedImages++;
                            MarkRemoved();
                            if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                            return;
                        }
                        if (PaintImage(xobject))
                        {
                            if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                            return;
                        }
                        Reasons.Add("Image");
                    }
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                if (!IsNamed(xobject.Dictionary, SubtypeName, "Form"))
                {
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                if (depth >= 32 || !_activeForms.Add(xobject))
                {
                    Reasons.Add("FormNesting");
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                try
                {
                    Matrix formMatrix = Matrix.Identity;
                    if (xobject.Dictionary.TryGetValue(Name("Matrix"), out PdfObject? matrixValue)
                        && Resolve(document, matrixValue) is PdfArray matrix && matrix.Count == 6)
                        formMatrix = new Matrix(Number(document, matrix[0]), Number(document, matrix[1]),
                            Number(document, matrix[2]), Number(document, matrix[3]),
                            Number(document, matrix[4]), Number(document, matrix[5]));
                    Matrix formCtm = formMatrix.Then(state.Ctm);
                    PdfContentBounds formClip = state.Clip;
                    if (xobject.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                        && Resolve(document, boxValue) is PdfArray box && box.Count == 4)
                        formClip = Intersect(formClip, Transform(new PdfContentBounds(
                            Math.Min(Number(document, box[0]), Number(document, box[2])),
                            Math.Min(Number(document, box[1]), Number(document, box[3])),
                            Math.Max(Number(document, box[0]), Number(document, box[2])),
                            Math.Max(Number(document, box[1]), Number(document, box[3]))), formCtm));
                    bool ownResources = xobject.Dictionary.TryGetValue(ResourcesName, out PdfObject? resourcesValue)
                        && Resolve(document, resourcesValue) is PdfDictionary;
                    ResourceScope formScope = ownResources
                        ? new ResourceScope(document, (PdfDictionary)Resolve(document, resourcesValue!))
                        {
                            KeepEverything = glyphsOnly
                        }
                        : scope;
                    byte[] formBytes = document.DecodeStream(xobject, PdfContentStreamReader.MaximumSourceBytes);
                    List<PdfContentInstruction> rewritten = Rewrite(formBytes, formScope,
                        state with { Ctm = formCtm, Clip = formClip }, depth + 1, out bool formChanged);
                    if (!formChanged)
                    {
                        scope.Use(XObjectCategory, name);
                        Emit(instruction);
                        return;
                    }
                    MarkRemoved();
                    if (update is null) return;
                    CollectSourceStreams(document, value, RetiredSourceStreams);
                    var entries = xobject.Dictionary
                        .Where(entry => entry.Key.ValueAsLatin1() is not ("Length" or "Filter" or "DecodeParms"))
                        .ToDictionary(entry => entry.Key, entry => entry.Value);
                    if (ownResources) entries[ResourcesName] = formScope.Build();
                    PdfIndirectReference copy = update.AddObject(new PdfStream(new PdfDictionary(entries),
                        PdfContentStreamWriter.Write(rewritten)));
                    PdfName alias = scope.Add(XObjectCategory, "KPRX", copy);
                    output.Add(new PdfContentInstruction("Do", 0, [alias]));
                }
                finally { _activeForms.Remove(xobject); }
            }

            // A partly covered image keeps its picture with the covered pixels painted over,
            // drawn from a new image so the original samples leave the page.
            bool PaintImage(PdfStream xobject)
            {
                if (PdfImageRedaction.Decode(document, xobject) is not PdfImageRedaction.Samples samples) return false;
                PdfImageRedaction.Plan? plan = PdfImageRedaction.Covers(state.Ctm, samples,
                    regions.Select((region, index) => (region, fills is not null && index < fills.Length
                        ? fills[index] : DefaultFill)));
                if (plan is null || plan.Image.Count == 0) return false;
                RedactedImages++;
                MarkRemoved();
                if (update is null) return true;
                PdfImageRedaction.Paint(samples, plan);
                PdfIndirectReference? mask = samples.Mask is PdfImageRedaction.Samples alpha
                    ? update.AddObject(PdfImageRedaction.Encode(alpha)) : null;
                PdfIndirectReference copy = update.AddObject(PdfImageRedaction.Encode(samples, mask));
                PdfName alias = scope.Add(XObjectCategory, PdfImageRedaction.ResourcePrefix, copy);
                output.Add(new PdfContentInstruction("Do", 0, [alias]));
                return true;
            }

            void InlineImage(PdfContentInstruction instruction)
            {
                if (textOnly) { MarkRemoved(); return; }
                PdfContentBounds bounds = Visible(Transform(new PdfContentBounds(0, 0, 1, 1), state.Ctm));
                if (!glyphsOnly && bounds.Width > 0 && bounds.Height > 0
                    && regions.Any(region => Overlaps(bounds, region)))
                {
                    if (regions.Any(region => Inside(bounds, region)))
                    {
                        RemovedImages++;
                        MarkRemoved();
                        return;
                    }
                    Reasons.Add("Image");
                }
                if (instruction.Operands.Count == 1 && instruction.Operands[0] is PdfDictionary image)
                    foreach (string key in new[] { "CS", "ColorSpace" })
                        if (image.TryGetValue(Name(key), out PdfObject? space) && space is PdfName spaceName
                            && !IsDeviceSpace(spaceName))
                            scope.Use(ColorSpaceCategory, spaceName);
                Emit(instruction);
            }

            void PathSegment(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                if (instruction.Operator == "re" && args.Count == 4)
                {
                    double x = N(args[0]), y = N(args[1]), w = N(args[2]), h = N(args[3]);
                    pathPoints.AddRange([state.Ctm.Point(x, y), state.Ctm.Point(x + w, y),
                        state.Ctm.Point(x + w, y + h), state.Ctm.Point(x, y + h)]);
                }
                else
                {
                    for (int index = 0; index + 1 < args.Count; index += 2)
                        pathPoints.Add(state.Ctm.Point(N(args[index]), N(args[index + 1])));
                }
                pendingPath.Add(instruction);
            }

            void PaintPath(PdfContentInstruction paint)
            {
                PdfContentBounds bounds = pathPoints.Count == 0 ? default : new PdfContentBounds(
                    pathPoints.Min(point => point.X), pathPoints.Min(point => point.Y),
                    pathPoints.Max(point => point.X), pathPoints.Max(point => point.Y));
                PdfContentBounds paintedBounds = !textOnly && !glyphsOnly && pathPoints.Count > 0
                    && paint.Operator is "S" or "s" or "B" or "B*" or "b" or "b*"
                    ? StrokeBounds(bounds, state) : bounds;
                if (textOnly)
                {
                    if (paint.Operator != "n" || pendingPath.Count > 0) MarkRemoved();
                }
                // A clip-only path (W n) paints nothing, so it is neither recoverable geometry
                // nor a reason to rasterize; it stays so later content keeps its clip.
                else if (!glyphsOnly && paint.Operator != "n" && pathPoints.Count > 0
                    && regions.Any(region => Overlaps(paintedBounds, region)))
                {
                    if (!pendingClip && regions.Any(region => Inside(paintedBounds, region)))
                    {
                        RemovedPaths++;
                        MarkRemoved();
                    }
                    else
                    {
                        Reasons.Add("Vector");
                        output.AddRange(pendingPath);
                        output.Add(paint);
                    }
                }
                else
                {
                    output.AddRange(pendingPath);
                    output.Add(paint);
                }
                if (pendingClip && pathPoints.Count > 0) state = state with { Clip = Intersect(state.Clip, bounds) };
                pendingPath.Clear();
                pathPoints.Clear();
                pendingClip = false;
            }

            PdfContentBounds Visible(PdfContentBounds bounds) => Intersect(bounds, state.Clip);
        }

        private static PdfContentBounds StrokeBounds(PdfContentBounds bounds, TextState state)
        {
            // A device hairline has no fixed extent in page coordinates.
            if (state.LineWidth == 0) return state.Clip;
            double extent = Math.Abs(state.LineWidth) * 0.5
                * Math.Max(state.LineCap == 2 ? Math.Sqrt(2) : 1,
                    state.LineJoin == 0 ? Math.Max(1, Math.Abs(state.MiterLimit)) : 1);
            double x = extent * (Math.Abs(state.Ctm.A) + Math.Abs(state.Ctm.C));
            double y = extent * (Math.Abs(state.Ctm.B) + Math.Abs(state.Ctm.D));
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new FormatException("The redaction stroke bounds are not finite.");
            return new PdfContentBounds(bounds.Left - x, bounds.Bottom - y, bounds.Right + x, bounds.Top + y);
        }

        private PdfExtractionFont? ReadFont(PdfObject? value)
        {
            if (value is null || Resolve(document, value) is not PdfDictionary dictionary) return null;
            if (fonts.TryGetValue(dictionary, out PdfExtractionFont? cached)) return cached;
            PdfExtractionFont? font;
            try { font = PdfFontResourceReader.Read(document, dictionary); }
            catch (Exception error) when (error is FormatException or NotSupportedException
                or InvalidOperationException or ArgumentException or PdfFilterException or InvalidDataException)
            {
                font = null;
            }
            fonts[dictionary] = font;
            return font;
        }

        internal int? ColorComponents(ResourceScope scope, PdfName name)
        {
            string value = name.ValueAsLatin1();
            int? standard = value switch
            {
                "DeviceGray" or "G" => 1,
                "DeviceRGB" or "RGB" => 3,
                "DeviceCMYK" or "CMYK" => 4,
                _ => null
            };
            if (standard.HasValue) return standard;
            PdfObject? space = scope.Lookup(ColorSpaceCategory, name);
            if (space is null) return null;
            PdfObject resolved = Resolve(document, space);
            if (resolved is PdfName alias) return alias.Equals(name) ? null : ColorComponents(scope, alias);
            if (resolved is not PdfArray array || array.Count == 0 || Resolve(document, array[0]) is not PdfName family) return null;
            return family.ValueAsLatin1() switch
            {
                "CalGray" or "Indexed" or "I" or "Separation" => 1,
                "CalRGB" or "Lab" => 3,
                "DeviceN" when array.Count > 1 && Resolve(document, array[1]) is PdfArray names => names.Count,
                "ICCBased" when array.Count > 1 && Resolve(document, array[1]) is PdfStream profile
                    && profile.Dictionary.TryGetValue(Name("N"), out PdfObject? count) => (int)Number(document, count),
                _ => null
            };
        }

        private static bool IsDeviceSpace(PdfName name) => name.ValueAsLatin1() is
            "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK" or "I" or "Indexed";

        private static bool ContainsReference(PdfObject value) => value switch
        {
            PdfIndirectReference => true,
            PdfArray array => array.Any(ContainsReference),
            PdfDictionary dictionary => dictionary.Values.Any(ContainsReference),
            _ => false
        };

        private static List<PdfObject> Merge(List<PdfObject> items)
        {
            var merged = new List<PdfObject>();
            double pending = 0;
            bool hasPending = false;
            foreach (PdfObject item in items)
            {
                if (item is PdfInteger or PdfReal)
                {
                    pending += item is PdfInteger integer ? integer.Value : ((PdfReal)item).Value;
                    hasPending = true;
                    continue;
                }
                if (hasPending) { merged.Add(new PdfReal(pending)); pending = 0; hasPending = false; }
                merged.Add(item);
            }
            if (hasPending) merged.Add(new PdfReal(pending));
            return merged;
        }

        private static PdfContentBounds Transform(PdfContentBounds box, Matrix matrix)
        {
            PdfPoint[] points = [matrix.Point(box.Left, box.Bottom), matrix.Point(box.Right, box.Bottom),
                matrix.Point(box.Left, box.Top), matrix.Point(box.Right, box.Top)];
            return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }

        private static PdfContentBounds Intersect(PdfContentBounds a, PdfContentBounds b) =>
            new(Math.Max(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom), Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));
    }
}
