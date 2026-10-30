using System.Security.Cryptography;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

public static partial class PdfContentRedaction
{
    internal const string SharedContentReason = "SharedContent";
    private const string RetainedHiddenContentCode = "RetainedHiddenContent";
    private const int MaximumAreas = 32;

    /// <summary>
    /// What a redaction covers, kept per piece of content rather than per object, so that any other
    /// draw of the same image, form or page content can be held to it, even from a byte-identical copy
    /// such as the ones Divide Spreads makes. It is filled in while the regions are analyzed.
    /// </summary>
    /// <remarks>
    /// A draw that shows the covered part is the user's own visible copy and stays. Only uses with a
    /// position are judged: annotation appearances, pattern cells, Type 3 glyphs and soft-mask groups
    /// keep the credit <see cref="RetiredUseWalker"/> gives them, and nothing looks at whether later
    /// content paints over a draw or a soft mask hides it.
    /// </remarks>
    private sealed class RetiredContent(PdfDocument document, CancellationToken cancellationToken)
    {
        // Fingerprints are only worked out when two different sources have the same bytes.
        private sealed record ContentAreas(object Source, Lazy<string> Resources, List<PdfContentBounds> Areas)
        {
            internal bool Matches(object source, Func<string> resources) =>
                ReferenceEquals(Source, source) || Resources.Value == resources();
        }

        private sealed class Store
        {
            internal Dictionary<string, List<PdfContentBounds>> Images { get; } = new(StringComparer.Ordinal);
            internal HashSet<long> ImageLengths { get; } = [];
            internal HashSet<(int, int)> UnfilteredSizes { get; } = [];
            internal Dictionary<string, List<ContentAreas>> Forms { get; } = new(StringComparer.Ordinal);
            internal Dictionary<string, List<ContentAreas>> Pages { get; } = new(StringComparer.Ordinal);
        }

        private Store _store = new();
        private readonly Dictionary<PdfStream, string?> _identities = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PdfStream, byte[]> _contentHashes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PdfDictionary, string> _fingerprints = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, PdfIndirectReference> _copies = new(StringComparer.Ordinal);
        private readonly HashSet<(PdfStream, PdfDictionary)> _formsWithNothingRetired = [];

        internal bool Sealed { get; private set; }
        internal bool IsEmpty => _store.Images.Count == 0 && _store.Forms.Count == 0 && _store.Pages.Count == 0;

        internal void Seal() => Sealed = true;

        /// <summary>The same record, reading another document: a normalized source or the output.</summary>
        internal RetiredContent For(PdfDocument other) =>
            new(other, cancellationToken) { _store = _store, Sealed = Sealed };

        /// <summary>Records areas of an image's unit square.</summary>
        internal void CoverImage(PdfStream image, IEnumerable<PdfContentBounds> areas)
        {
            PdfContentBounds[] list = [.. areas];
            if (list.Length == 0) return;
            if (!_identities.TryGetValue(image, out string? identity) || identity is null)
                _identities[image] = identity = PdfImageRedaction.Identity(document, image);
            if (!_store.Images.TryGetValue(identity, out List<PdfContentBounds>? known))
                _store.Images[identity] = known = [];
            AddAreas(known, list);
            _store.ImageLengths.Add(image.EncodedData.Length);
            if (PdfImageRedaction.IsUnfiltered(document, image) && PdfImageRedaction.Size(document, image) is { } size)
                _store.UnfilteredSizes.Add(size);
        }

        /// <summary>Records areas of a form's own space.</summary>
        internal void CoverForm(PdfStream form, byte[] content, IEnumerable<PdfContentBounds> areas) =>
            Cover(_store.Forms, Hash(content), form, () => FormResources(form), areas);

        /// <summary>Records areas of a page's default user space.</summary>
        internal void CoverPage(byte[] content, PdfDictionary resources, IEnumerable<PdfContentBounds> areas) =>
            Cover(_store.Pages, Hash(content), resources, () => Fingerprint(resources), areas);

        private static void Cover(Dictionary<string, List<ContentAreas>> records, string content, object source,
            Func<string> resources, IEnumerable<PdfContentBounds> areas)
        {
            PdfContentBounds[] list = [.. areas];
            if (list.Length == 0) return;
            if (!records.TryGetValue(content, out List<ContentAreas>? entries)) records[content] = entries = [];
            if (entries.Find(entry => entry.Matches(source, resources)) is not { } known)
                entries.Add(known = new ContentAreas(source, new Lazy<string>(resources), []));
            AddAreas(known.Areas, list);
        }

        // A stamp drawn thousands of times under a region gives the same area each time, and every
        // later draw is compared with the whole list, so repeats leave and a long list becomes its
        // bounds. Covering more than was asked is safe: only what a draw does not show is ever taken.
        private static void AddAreas(List<PdfContentBounds> areas, PdfContentBounds[] added)
        {
            foreach (PdfContentBounds area in added)
            {
                if (areas.Exists(known => Holds(known, area))) continue;
                areas.RemoveAll(known => Holds(area, known));
                areas.Add(area);
            }
            if (areas.Count <= MaximumAreas) return;
            PdfContentBounds bounds = PdfContentBounds.Union(areas);
            areas.Clear();
            areas.Add(bounds);
        }

        private static bool Holds(PdfContentBounds outer, PdfContentBounds inner) =>
            inner.Left >= outer.Left - 1e-6 && inner.Right <= outer.Right + 1e-6
            && inner.Bottom >= outer.Bottom - 1e-6 && inner.Top <= outer.Top + 1e-6;

        // Another image of the same size and storage is hashed only if it could be the same one. The
        // writer compresses unfiltered images, so one of those can come back with another stored length.
        internal IReadOnlyList<PdfContentBounds>? ImageAreas(PdfStream image)
        {
            if (_store.Images.Count == 0) return null;
            if (!_identities.TryGetValue(image, out string? identity))
            {
                bool candidate = _store.ImageLengths.Contains(image.EncodedData.Length)
                    || PdfImageRedaction.Size(document, image) is { } size && _store.UnfilteredSizes.Contains(size)
                        && PdfImageRedaction.IsFlate(document, image);
                _identities[image] = identity = candidate ? PdfImageRedaction.Identity(document, image) : null;
            }
            return identity is not null && _store.Images.TryGetValue(identity, out List<PdfContentBounds>? areas) ? areas : null;
        }

        internal IReadOnlyList<PdfContentBounds>? FormAreas(PdfStream form, byte[] content) =>
            _store.Forms.TryGetValue(Hash(content), out List<ContentAreas>? entries)
            && entries.Find(entry => entry.Matches(form, () => FormResources(form))) is { } match ? match.Areas : null;

        /// <summary>True when a page must be read at all, given the box it shows.</summary>
        internal bool NeedsWalk(PageGeometry geometry)
        {
            if (_store.Images.Count != 0 || _store.Forms.Count != 0) return true;
            PdfContentBounds crop = CropBox(geometry);
            return _store.Pages.Values.Any(entries => entries.Any(entry => entry.Areas.Any(area => !Inside(area, crop))));
        }

        /// <summary>
        /// The areas covered on any page with this same content that lie past this page's box, in its
        /// crop-relative coordinates. The rest of those pages' areas this page shows, so they stay.
        /// </summary>
        internal PdfContentBounds[] PageAreas(byte[] content, PdfDictionary resources, PageGeometry geometry)
        {
            if (!_store.Pages.TryGetValue(Hash(content), out List<ContentAreas>? entries)) return [];
            PdfContentBounds crop = CropBox(geometry);
            return [.. entries.Where(entry => entry.Areas.Any(area => !Inside(area, crop))
                    && entry.Matches(resources, () => Fingerprint(resources)))
                .SelectMany(entry => entry.Areas).Where(area => !Inside(area, crop))
                .Select(area => new PdfContentBounds(area.Left - geometry.CropLeft, area.Bottom - geometry.CropBottom,
                    area.Right - geometry.CropLeft, area.Top - geometry.CropBottom))];
        }

        internal bool HoldsNothingRetired(PdfStream form, PdfDictionary resources) =>
            _formsWithNothingRetired.Contains((form, resources));

        internal void MarkHoldsNothingRetired(PdfStream form, PdfDictionary resources) =>
            _formsWithNothingRetired.Add((form, resources));

        internal PdfIndirectReference PrivateCopy(string key, Func<PdfIndirectReference> create)
        {
            if (!_copies.TryGetValue(key, out PdfIndirectReference? copy)) _copies[key] = copy = create();
            return copy;
        }

        private static PdfContentBounds CropBox(PageGeometry geometry) => new(geometry.CropLeft, geometry.CropBottom,
            geometry.CropLeft + geometry.Width, geometry.CropBottom + geometry.Height);

        private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

        // A form with no resources of its own uses whatever draws it, so its bytes are all that identify it.
        private string FormResources(PdfStream form) =>
            form.Dictionary.TryGetValue(ResourcesName, out PdfObject? value) && Resolve(document, value) is PdfDictionary resources
                ? Fingerprint(resources) : string.Empty;

        // Two pages or forms with the same bytes are the same content only if their names mean the same
        // things, so scanned pages that each draw their own /Im0 stay apart. References are followed,
        // because a copy's objects have other numbers, and entries the writer may change are left out.
        // So are the resources of what the names point at, since forms can list each other by the hundred.
        private string Fingerprint(PdfDictionary resources)
        {
            if (_fingerprints.TryGetValue(resources, out string? known)) return known;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var seen = new Dictionary<int, int>();
            Write(resources, 0);
            return _fingerprints[resources] = Convert.ToHexString(hash.GetHashAndReset());

            void Write(PdfObject value, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth > PdfObjectWriter.MaximumNestingDepth) { hash.AppendData("deep"u8); return; }
                switch (value)
                {
                    case PdfIndirectReference reference:
                        if (seen.TryGetValue(reference.ObjectNumber, out int first))
                        {
                            hash.AppendData(BitConverter.GetBytes(first));
                            return;
                        }
                        seen[reference.ObjectNumber] = seen.Count;
                        Write(Resolve(document, reference), depth + 1);
                        break;
                    case PdfStream stream:
                        hash.AppendData("stream"u8);
                        Write(stream.Dictionary, depth + 1);
                        if (!_contentHashes.TryGetValue(stream, out byte[]? content))
                            _contentHashes[stream] = content = SHA256.HashData(PdfImageRedaction.ContentBytes(document, stream));
                        hash.AppendData(content);
                        break;
                    case PdfDictionary dictionary:
                        hash.AppendData("<<"u8);
                        foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary
                            .Where(item => !WriterKeys.Contains(item.Key))
                            .OrderBy(item => item.Key.ValueAsLatin1(), StringComparer.Ordinal))
                        {
                            hash.AppendData(PdfObjectWriter.Write(entry.Key));
                            Write(entry.Value, depth + 1);
                        }
                        break;
                    case PdfArray array:
                        hash.AppendData("["u8);
                        foreach (PdfObject item in array) Write(item, depth + 1);
                        break;
                    default:
                        hash.AppendData(PdfObjectWriter.Write(value));
                        break;
                }
            }
        }

        private static readonly PdfName[] WriterKeys = [Name("Length"), Name("Filter"), Name("DecodeParms"), Name("DL"),
            Name("Metadata"), Name("Parent"), Name("StructParent"), Name("StructParents"), ResourcesName];
    }

    // Annotations a page carries past its box, over an area redacted on a page with the same content.
    private static List<PdfObject> HiddenAnnotations(PdfDocument document, PdfPageTreeEntry page, PageGeometry geometry,
        PdfContentBounds[] areas)
    {
        if (areas.Length == 0) return [];
        var box = new PdfContentBounds(0, 0, geometry.Width, geometry.Height);
        return [.. IntersectingAnnotations(document, page, geometry, areas).Where(item =>
            Resolve(document, item) is PdfDictionary annotation
            && TryAnnotationBounds(document, annotation, geometry, out PdfContentBounds bounds) && !Overlaps(bounds, box))];
    }

    // Reads a page against what the redaction covers. A page that cannot be read cannot be shown to be
    // clear of it, so null sends that page down the same refusal as content it cannot take apart.
    private static ContentRewriter? WalkSurvivors(PdfDocument document, PdfIncrementalUpdateBuilder? update,
        PdfPageTreeEntry page, PdfContentBounds[] regions, Dictionary<PdfDictionary, Fonts.PdfExtractionFont?> fonts,
        RetiredContent retired, CancellationToken cancellationToken, out List<PdfContentInstruction> content)
    {
        content = [];
        try
        {
            var rewriter = new ContentRewriter(document, update, regions, false, fonts, cancellationToken, retired: retired);
            PageGeometry geometry = Geometry(document, page);
            if (retired.NeedsWalk(geometry)) content = rewriter.RewritePage(page, geometry);
            return rewriter;
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or NotSupportedException
            or KeyNotFoundException or Filters.PdfFilterException or InvalidDataException)
        {
            return null;
        }
    }

    // The pages the regions cannot be applied to without an image: those whose draws hide retired
    // content they cannot give up, whether or not they carry a region themselves.
    private static HashSet<int> SharedContentPages(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfContentBounds[]> byPage, Dictionary<PdfDictionary, Fonts.PdfExtractionFont?> fonts,
        RetiredContent retired, CancellationToken cancellationToken)
    {
        var shared = new HashSet<int>();
        if (retired.IsEmpty) return shared;
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContentRewriter? rewriter = WalkSurvivors(document, null, page, byPage.GetValueOrDefault(page.Index, []),
                fonts, retired, cancellationToken, out _);
            if (rewriter is null || rewriter.Reasons.Contains(SharedContentReason)) shared.Add(page.Index);
        }
        return shared;
    }

    // Gives every other page its own version of whatever it hides: an image with those samples
    // painted, a form or a content stream without the glyphs and paths. What it shows stays as it was.
    private static HashSet<int> RewriteSurvivorPages(PdfDocument document, PdfPageTree tree,
        PdfIncrementalUpdateBuilder update, Dictionary<int, PdfContentBounds[]> byPage,
        Dictionary<PdfDictionary, Fonts.PdfExtractionFont?> fonts, RetiredContent retired,
        Dictionary<int, (HashSet<int> Mcids, bool Whole)> touchedContent, HashSet<int> retiredStreams,
        HashSet<string> retiredDescriptions, HashSet<int> removedAnnotations, HashSet<int> removedWidgets,
        CancellationToken cancellationToken)
    {
        var rewritten = new HashSet<int>();
        if (retired.IsEmpty) return rewritten;
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byPage.ContainsKey(page.Index)) continue;
            // Most pages hold nothing retired, so the read-only walk settles them.
            ContentRewriter? rewriter = WalkSurvivors(document, null, page, [], fonts, retired, cancellationToken, out _);
            if (rewriter is null || rewriter.Reasons.Contains(SharedContentReason))
                throw new InvalidOperationException($"Page {page.Index + 1} changed while it was being redacted.");
            List<PdfObject> annotations = HiddenAnnotations(document, page, Geometry(document, page), rewriter.HiddenPageAreas);
            if (!rewriter.ContentChanged && annotations.Count == 0) continue;
            rewriter = WalkSurvivors(document, update, page, [], fonts, retired, cancellationToken,
                out List<PdfContentInstruction> content)
                ?? throw new InvalidOperationException($"Page {page.Index + 1} changed while it was being redacted.");
            var entries = page.Dictionary.Where(entry => !RemovedPageKeys.Contains(entry.Key)
                    && (annotations.Count == 0 || !entry.Key.Equals(AnnotsName)))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            if (annotations.Count != 0
                && RemainingAnnotations(document, page, annotations, removedAnnotations, removedWidgets) is PdfArray remaining)
                entries[AnnotsName] = remaining;
            // A rewritten page is left out of the step that gives pages their inherited resources.
            if (page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? inherited)) entries[ResourcesName] = inherited;
            if (rewriter.ContentChanged)
            {
                if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? originalContents))
                    CollectSourceStreams(document, originalContents, retiredStreams);
                retiredStreams.UnionWith(rewriter.RetiredSourceStreams);
                retiredDescriptions.UnionWith(rewriter.RetiredDescriptions);
                entries[ContentsName] = update.AddObject(new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(content)));
                entries[ResourcesName] = rewriter.PageScope!.Build();
            }
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
            touchedContent[page.Reference.ObjectNumber] = (rewriter.TouchedMcids, rewriter.FormMarkedContentTouched);
            rewritten.Add(page.Index);
        }
        return rewritten;
    }

    // The same walk as the rewrite, read-only: anything the rewrite would still take from a page of
    // the written file means retired content is there, hidden, and the file is not safe to return.
    private static void VerifySurvivors(PdfDocument output, RetiredContent retired, CancellationToken cancellationToken)
    {
        if (retired.IsEmpty) return;
        RetiredContent reader = retired.For(output);
        var fonts = new Dictionary<PdfDictionary, Fonts.PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        var findings = new List<PdfRedactionVerificationFinding>();
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(output).Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContentRewriter? rewriter = WalkSurvivors(output, null, page, [], fonts, reader, cancellationToken, out _);
            if (rewriter is null || rewriter.ContentChanged || rewriter.Reasons.Contains(SharedContentReason)
                || HiddenAnnotations(output, page, Geometry(output, page), rewriter.HiddenPageAreas).Count != 0)
                findings.Add(new(RetainedHiddenContentCode,
                    "Redacted content remains on this page, hidden from view.", page.Index));
        }
        if (findings.Count != 0)
            throw new PdfRedactionVerificationException(new PdfRedactionVerificationReport(Array.AsReadOnly(findings.ToArray())));
    }

    /// <summary>Checks a document against what redacting its source would cover.</summary>
    /// <exception cref="PdfRedactionVerificationException">A page still hides covered content.</exception>
    internal static void VerifyRetainedContent(PdfDocument source, IEnumerable<PdfRedactionRegion> regions,
        PdfDocument output, CancellationToken cancellationToken = default)
    {
        PdfPageTree tree = PdfPageTree.Read(source);
        AnalyzePages(source, tree, GroupRegions(regions, tree.Pages.Count), cancellationToken, out RetiredContent retired);
        VerifySurvivors(output, retired, cancellationToken);
    }
}
