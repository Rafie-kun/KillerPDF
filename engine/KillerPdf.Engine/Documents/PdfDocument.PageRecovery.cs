using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

public sealed partial class PdfDocument
{
    private static readonly string[] PageRecoveryDiagnostics =
        ["Invalid catalog or page-tree links were recovered from intact indirect page objects."];
    private static readonly IReadOnlyList<string> EmptyAesStringDiagnostics = Array.AsReadOnly(new[]
        { "An empty or IV-only AES string was recovered as empty in compatibility mode." });
    private IReadOnlyList<string> _pageRecoveryDiagnostics = [];
    private bool _pageRecoveryAttempted;
    private volatile bool _pageStructureRead;
    private IReadOnlyDictionary<int, PdfObject> _recoveredPageObjects = new Dictionary<int, PdfObject>();

    /// <summary>Gets repairs applied by bounded compatibility recovery to objects read so far.</summary>
    public IReadOnlyList<string> RecoveryDiagnostics => _security?.RecoveredEmptyAesString == true
        ? _pageRecoveryDiagnostics.Count == 0 ? EmptyAesStringDiagnostics
            : Array.AsReadOnly(_pageRecoveryDiagnostics.Concat(EmptyAesStringDiagnostics).ToArray())
        : _pageRecoveryDiagnostics;
    internal IReadOnlyDictionary<int, PdfObject> RecoveredPageObjects => _recoveredPageObjects;

    internal void MarkPageStructureRead() => _pageStructureRead = true;

    internal void PreparePageStructureForWriting()
    {
        using var scope = _objectResolutionSync.EnterScope();
        if (!_compatibilityRecovery || _pageStructureRead || _pageRecoveryAttempted || IsEncrypted
            || _source.Length > 16 * 1024 * 1024 || CrossReferences.Count > 4096) return;
        // Finish bounded structural recovery before a writer reserves object numbers.
        // Generic metadata updates may still operate on documents without a page tree.
        try { PdfPageTree.Read(this, allowCycleRecovery: true); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or NotSupportedException)
        { }
    }

    internal bool TryRecoverPageTree()
    {
        using var scope = _objectResolutionSync.EnterScope();
        if (!_compatibilityRecovery || _pageRecoveryAttempted || IsEncrypted
            || _source.Length > 16 * 1024 * 1024 || CrossReferences.Count > 4096)
            return false;
        _pageRecoveryAttempted = true;
        try
        {
            // Use a separate model until a complete tree has been validated. In particular,
            // a failed recovery must not leave the original lazy caches half modified.
            var candidate = new PdfDocument(_source,
                PdfCrossReferenceTable.RecoverPageStructure(_source, CrossReferences), compatibilityRecovery: true)
            { _pageRecoveryAttempted = true };
            if (candidate.IsEncrypted || candidate.CrossReferences.Count > 4096) return false;
            var catalogs = new List<(PdfIndirectReference Reference, PdfDictionary Dictionary)>();
            var roots = new List<(PdfIndirectReference Reference, PdfDictionary Dictionary)>();
            var leaves = new List<(PdfIndirectReference Reference, PdfDictionary Dictionary)>();
            foreach (PdfCrossReferenceEntry entry in candidate.CrossReferences.Values)
            {
                if (entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed))
                    continue;
                PdfObject value;
                try { value = candidate.Resolve(entry.ObjectNumber); }
                catch (Exception error) when (error is InvalidOperationException or FormatException or NotSupportedException)
                { continue; }
                if (value is not PdfDictionary dictionary
                    || !dictionary.TryGetValue(Key("Type"), out PdfObject type)
                    || type is not PdfName name) continue;
                var reference = new PdfIndirectReference(entry.ObjectNumber,
                    entry.Type == PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0);
                switch (name.ValueAsLatin1())
                {
                    case "Catalog": catalogs.Add((reference, dictionary)); break;
                    case "Pages" when !dictionary.ContainsKey(Key("Parent")):
                        roots.Add((reference, dictionary)); break;
                    case "Page": leaves.Add((reference, dictionary)); break;
                }
            }
            // Multiple detached roots or catalogs provide no reliable document ordering.
            if (catalogs.Count > 1 || roots.Count > 1 || leaves.Count == 0) return false;
            int next = checked(candidate.CrossReferences.Keys.Max() + 1);
            var replacements = new Dictionary<int, PdfObject>();
            (PdfIndirectReference Reference, PdfDictionary Dictionary) root;
            if (roots.Count == 1)
                root = roots[0];
            else
            {
                if (leaves.Count != 1 || !leaves[0].Dictionary.ContainsKey(Key("MediaBox"))) return false;
                root = (new PdfIndirectReference(next++, 0), new PdfDictionary([
                    new(Key("Type"), Key("Pages")),
                    new(Key("Kids"), new PdfArray([leaves[0].Reference])),
                    new(Key("Count"), new PdfInteger(1))]));
                replacements[root.Reference.ObjectNumber] = root.Dictionary;
                replacements[leaves[0].Reference.ObjectNumber] = Set(leaves[0].Dictionary,
                    "Parent", root.Reference);
            }
            (PdfIndirectReference Reference, PdfDictionary Dictionary) catalog = catalogs.Count == 1
                ? catalogs[0] : (new PdfIndirectReference(next, 0), new PdfDictionary([
                    new(Key("Type"), Key("Catalog"))]));
            catalog.Dictionary = Set(catalog.Dictionary, "Pages", root.Reference);
            replacements[catalog.Reference.ObjectNumber] = catalog.Dictionary;
            Apply();
            bool duplicateRecoveryRefused = false;
            bool duplicateOccurrencesRecovered = false;
            PdfPageTree? tree = ReadCandidate();
            if (tree is null || tree.Pages.Count == 0)
            {
                if (duplicateRecoveryRefused) return false;
                // Only one leaf plus a declared count of one is unambiguous. Count zero
                // is an intentionally empty tree, even when old orphan pages remain.
                if (leaves.Count != 1
                    || !root.Dictionary.TryGetValue(Key("Count"), out PdfObject count)
                    || count is not PdfInteger { Value: 1 }) return false;
                root.Dictionary = Set(Set(root.Dictionary, "Kids", new PdfArray([leaves[0].Reference])),
                    "Count", new PdfInteger(1));
                replacements[root.Reference.ObjectNumber] = root.Dictionary;
                replacements[leaves[0].Reference.ObjectNumber] = Set(leaves[0].Dictionary,
                    "Parent", root.Reference);
                Apply();
                tree = ReadCandidate();
            }
            if (tree is null || tree.Pages.Count == 0 || tree.RecoveredCycle) return false;
            // Publish the repaired references and their owned immutable values together.
            CrossReferences = candidate.CrossReferences;
            _objects.Clear();
            foreach (var pair in candidate._objects) _objects.Add(pair.Key, pair.Value);
            _objectStreams.Clear();
            foreach (var pair in candidate._objectStreams) _objectStreams.Add(pair.Key, pair.Value);
            _recoveredPageObjects = replacements;
            _pageRecoveryDiagnostics = Array.AsReadOnly(duplicateOccurrencesRecovered
                ? PageRecoveryDiagnostics.Append("Repeated page references were expanded with separate owned objects. Incoming navigation targets the first occurrence; copied explicit self-destinations target their own occurrence.").ToArray()
                : PageRecoveryDiagnostics);
            return true;

            void Apply()
            {
                candidate.CrossReferences = candidate.CrossReferences.WithRecoveredPageObjects(
                    replacements, catalog.Reference);
                foreach (var pair in replacements) candidate._objects[pair.Key] = pair.Value;
            }
            PdfPageTree? ReadCandidate()
            {
                try { return PdfPageTree.Read(candidate); }
                catch (Exception error) when (error is InvalidOperationException or FormatException or NotSupportedException)
                {
                    var normalizer = new PdfDuplicatePageNormalizer(candidate);
                    try
                    {
                        IReadOnlyDictionary<int, PdfObject>? delta = normalizer.Normalize(root.Reference, catalog.Dictionary);
                        if (delta is null) return null;
                        var staged = new Dictionary<int, PdfObject>(replacements);
                        foreach (var pair in delta) staged[pair.Key] = pair.Value;
                        var verified = new PdfDocument(_source,
                            candidate.CrossReferences.WithRecoveredPageObjects(staged, catalog.Reference),
                            compatibilityRecovery: true) { _pageRecoveryAttempted = true };
                        foreach (var pair in staged) verified._objects[pair.Key] = pair.Value;
                        PdfPageTree recovered = PdfPageTree.Read(verified);
                        if (recovered.Pages.Count == 0 || recovered.RecoveredCycle) return null;
                        new PdfRecoveredOwnershipValidator(verified).Validate();
                        candidate = verified;
                        replacements = staged;
                        duplicateOccurrencesRecovered = true;
                        return recovered;
                    }
                    catch (Exception refused) when (refused is InvalidOperationException or FormatException
                        or NotSupportedException or OverflowException)
                    {
                        duplicateRecoveryRefused |= normalizer.HasDuplicateOccurrences;
                        return null;
                    }
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    private static PdfName Key(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary Set(PdfDictionary dictionary, string key, PdfObject value) =>
        new(dictionary.Where(pair => !pair.Key.Equals(Key(key))).Append(new(Key(key), value)));
}
