using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Writing;

/// <summary>Rewrites a document as PDF 2.0 and corrects the constructs PDF 2.0 deprecates.</summary>
public static class PdfVersion2Upgrade
{
    private static readonly PdfName InfoName = new("Info"u8);
    private static readonly PdfName CreationDateName = new("CreationDate"u8);
    private static readonly PdfName ModDateName = new("ModDate"u8);
    private static readonly PdfName RootName = new("Root"u8);
    private static readonly PdfName MetadataName = new("Metadata"u8);
    private static readonly PdfName ProcSetName = new("ProcSet"u8);
    private static readonly PdfName AlternatesName = new("Alternates"u8);
    private static readonly PdfName OpiName = new("OPI"u8);
    private static readonly PdfName SubtypeName = new("Subtype"u8);
    private static readonly PdfName ImageName = new("Image"u8);
    private static readonly PdfName FormName = new("Form"u8);
    private static readonly PdfName PostScriptName = new("PS"u8);

    /// <summary>
    /// Returns the document rewritten as PDF 2.0. The rewrite invalidates signatures and Reader usage
    /// rights, so a document carrying either is refused unless <paramref name="allowSignatureInvalidation"/>
    /// says to give them up.
    /// </summary>
    public static byte[] Apply(PdfDocument document, bool allowSignatureInvalidation = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        bool changed = false;
        PdfDocumentInformation original = PdfDocumentInformation.Read(document);
        // Without an XMP packet the descriptive fields would be lost along with /Info, so the
        // packet is written from them first.
        PdfXmpDocumentMetadata? existing = PdfXmpDocumentMetadata.Read(document);
        if (existing is not null)
        {
            if (PdfXmpDocumentMetadata.AddMissing(document, original) is byte[] merged)
            {
                PdfObject metadataValue = PdfPageTree.Read(document).Catalog[MetadataName];
                if (metadataValue is not PdfIndirectReference metadataReference)
                    throw new InvalidOperationException("The catalog /Metadata value is not an indirect stream.");
                update.ReplaceObject(metadataReference.ObjectNumber, MetadataStream(merged));
                changed = true;
            }
        }
        else if (HasDescriptiveFields(original))
        {
            byte[] packet = PdfDocumentBuilder.BuildXmp(new PdfDocumentMetadata
            {
                Title = original.Title,
                Author = original.Author,
                Subject = original.Subject,
                Keywords = original.Keywords,
                Creator = original.Creator,
                Producer = original.Producer,
                Language = original.Language,
                CreationDate = original.CreationDate,
                ModificationDate = original.ModificationDate,
                Trapped = original.Trapped
            });
            PdfIndirectReference metadata = update.AddObject(MetadataStream(packet));
            if (!document.Trailer.TryGetValue(RootName, out PdfObject? root)
                || root is not PdfIndirectReference catalogReference)
                throw new InvalidOperationException("The trailer /Root value is not an indirect reference.");
            PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
            update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(
                [.. catalog.Where(entry => !entry.Key.Equals(MetadataName)),
                    new KeyValuePair<PdfName, PdfObject>(MetadataName, metadata)]));
            changed = true;
        }
        // ISO 32000-2 14.3.3 deprecates every /Info entry except the creation and modification
        // dates; the descriptive fields belong in the catalog's XMP metadata.
        if (document.Trailer.TryGetValue(InfoName, out PdfObject? info)
            && Resolve(document, info) is PdfDictionary informationDictionary)
        {
            KeyValuePair<PdfName, PdfObject>[] dates = [.. informationDictionary.Where(entry =>
                entry.Key.Equals(CreationDateName) || entry.Key.Equals(ModDateName))];
            if (dates.Length != informationDictionary.Count)
            {
                if (info is PdfIndirectReference reference)
                    update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(dates));
                else
                    update.SetDocumentInformation(update.AddObject(new PdfDictionary(dates)));
                changed = true;
            }
        }
        var replaced = new HashSet<int>();
        if (document.Trailer.TryGetValue(InfoName, out PdfObject? infoValue)
            && infoValue is PdfIndirectReference infoReference)
            replaced.Add(infoReference.ObjectNumber);
        if (document.Trailer.TryGetValue(RootName, out PdfObject? rootValue)
            && rootValue is PdfIndirectReference rootReference)
            replaced.Add(rootReference.ObjectNumber);
        foreach (int objectNumber in document.CrossReferences.Keys.Order().ToArray())
        {
            if (replaced.Contains(objectNumber)) continue;
            PdfObject value = document.Resolve(objectNumber);
            PdfObject cleaned = RemoveDeprecated(value);
            if (ReferenceEquals(cleaned, value)) continue;
            update.ReplaceObject(objectNumber, cleaned);
            changed = true;
        }
        PdfDocument revised = changed ? PdfDocument.Open(update.Build()) : document;
        // A full rewrite drops the earlier revision, so the removed fields do not survive in it.
        try
        {
            return PdfDocumentWriter.Write(revised, new PdfDocumentWriteOptions
            {
                TargetVersion = PdfVersion.Pdf20,
                AllowSignatureInvalidation = allowSignatureInvalidation
            });
        }
        catch (InvalidOperationException error) when (PdfRewriteRefusal.Matches(error))
        {
            throw PdfRewriteRefusal.Restate(error, "Pass allowSignatureInvalidation: true to upgrade it anyway.");
        }
    }

    // Removes what ISO 32000-2 deprecates and conforming readers ignore: resource /ProcSet
    // arrays, image /Alternates and /OPI, and form /OPI. PostScript XObjects cannot be
    // preserved as PDF 2.0 content, so the upgrade rejects them. Returns the same instance
    // when nothing changed.
    private static PdfObject RemoveDeprecated(PdfObject value)
    {
        switch (value)
        {
            case PdfStream stream:
            {
                PdfDictionary dictionary = stream.Dictionary;
                PdfName? subtype = dictionary.TryGetValue(SubtypeName, out PdfObject? subtypeValue)
                    ? subtypeValue as PdfName : null;
                if (PostScriptName.Equals(subtype))
                    throw new NotSupportedException(
                        "PDF 2.0 upgrade cannot preserve a PostScript XObject.");
                PdfDictionary cleaned = (PdfDictionary)RemoveDeprecated(dictionary);
                if (ImageName.Equals(subtype) || FormName.Equals(subtype))
                    cleaned = Without(cleaned, ImageName.Equals(subtype)
                        ? [AlternatesName, OpiName] : [OpiName]);
                return ReferenceEquals(cleaned, dictionary)
                    ? stream : new PdfStream(cleaned, stream.EncodedData.Span);
            }
            case PdfDictionary dictionary:
            {
                bool changed = false;
                var entries = new List<KeyValuePair<PdfName, PdfObject>>(dictionary.Count);
                foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary)
                {
                    // /ProcSet only occurs in resource dictionaries, where it lists PostScript
                    // procedure sets that PDF 2.0 readers ignore.
                    if (entry.Key.Equals(ProcSetName) && entry.Value is PdfArray or PdfIndirectReference)
                    {
                        changed = true;
                        continue;
                    }
                    PdfObject child = RemoveDeprecated(entry.Value);
                    changed |= !ReferenceEquals(child, entry.Value);
                    entries.Add(new(entry.Key, child));
                }
                return changed ? new PdfDictionary(entries) : dictionary;
            }
            case PdfArray array:
            {
                PdfObject[] items = [.. array.Select(RemoveDeprecated)];
                return items.Where((item, index) => !ReferenceEquals(item, array[index])).Any()
                    ? new PdfArray(items) : array;
            }
            default:
                return value;
        }
    }

    private static PdfDictionary Without(PdfDictionary dictionary, PdfName[] names) =>
        dictionary.Keys.Any(key => names.Contains(key))
            ? new PdfDictionary(dictionary.Where(entry => !names.Contains(entry.Key)))
            : dictionary;

    // Metadata streams stay unfiltered so that tools reading the file as text can find them.
    private static PdfStream MetadataStream(byte[] packet) => new(new PdfDictionary(
    [
        new(new PdfName("Type"u8), new PdfName("Metadata"u8)),
        new(new PdfName("Subtype"u8), new PdfName("XML"u8))
    ]), packet);

    private static bool HasDescriptiveFields(PdfDocumentInformation information) =>
        information.Title is not null || information.Author is not null
        || information.Subject is not null || information.Keywords is not null
        || information.Creator is not null || information.Producer is not null
        || information.Trapped is not null;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("The document information reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }
}
