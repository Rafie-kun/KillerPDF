using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>
/// Finds the form fields a file lists more than once. A viewer shows such a field once, so a repairing
/// rewrite keeps its first listing and drops the rest, with every field and value still in place.
/// </summary>
internal static class PdfRepeatedFieldRepair
{
    private static readonly PdfName RootName = Name("Root");
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName KidsName = Name("Kids");
    private const int MaximumDepth = 256;

    /// <summary>
    /// Returns a replacement for each object that holds a field list with a repeat, by object number,
    /// or an empty set when no field is listed twice.
    /// </summary>
    internal static Dictionary<int, PdfObject> FindReplacements(PdfDocument document)
    {
        var replacements = new Dictionary<int, PdfObject>();
        if (!document.CrossReferences.TryGetTrailerValue(RootName, out PdfObject root)
            || root is not PdfIndirectReference rootReference
            || document.Resolve(rootReference) is not PdfDictionary catalog
            || !catalog.TryGetValue(AcroFormName, out PdfObject? formValue)) return replacements;
        PdfIndirectReference? formReference = formValue as PdfIndirectReference;
        if ((formReference is null ? formValue : document.Resolve(formReference)) is not PdfDictionary form
            || !form.TryGetValue(FieldsName, out PdfObject? fieldsValue)) return replacements;
        PdfIndirectReference? fieldsReference = fieldsValue as PdfIndirectReference;
        if ((fieldsReference is null ? fieldsValue : document.Resolve(fieldsReference)) is not PdfArray fields)
            return replacements;

        var seen = new HashSet<(int ObjectNumber, int Generation)>();
        PdfArray? cleaned = Clean(fields, 0);
        if (cleaned is null) return replacements;
        if (fieldsReference is not null) replacements[fieldsReference.ObjectNumber] = cleaned;
        else
        {
            PdfDictionary newForm = With(form, FieldsName, cleaned);
            if (formReference is not null) replacements[formReference.ObjectNumber] = newForm;
            else replacements[rootReference.ObjectNumber] = With(catalog, AcroFormName, newForm);
        }
        return replacements;

        // The array without its repeats, or null when it has none.
        PdfArray? Clean(PdfArray array, int depth)
        {
            if (depth > MaximumDepth) return null;
            List<PdfObject>? kept = null;
            for (int index = 0; index < array.Count; index++)
            {
                PdfObject item = array[index];
                if (item is PdfIndirectReference reference)
                {
                    if (!seen.Add(Identity(reference)))
                    {
                        kept ??= [.. array.Take(index)];
                        continue;
                    }
                    CleanKids(reference, depth);
                }
                kept?.Add(item);
            }
            return kept is null ? null : new PdfArray(kept);
        }

        void CleanKids(PdfIndirectReference reference, int depth)
        {
            if (document.Resolve(reference) is not PdfDictionary field
                || !field.TryGetValue(KidsName, out PdfObject? kidsValue)) return;
            PdfIndirectReference? kidsReference = kidsValue as PdfIndirectReference;
            if ((kidsReference is null ? kidsValue : document.Resolve(kidsReference)) is not PdfArray kids) return;
            PdfArray? cleanedKids = Clean(kids, depth + 1);
            if (cleanedKids is null) return;
            if (kidsReference is not null) replacements[kidsReference.ObjectNumber] = cleanedKids;
            else replacements[reference.ObjectNumber] = With(field, KidsName, cleanedKids);
        }

        // A reference that only names another reference stands for the object at the end of the chain.
        (int ObjectNumber, int Generation) Identity(PdfIndirectReference reference)
        {
            PdfIndirectReference current = reference;
            for (int hops = 0; hops < 32; hops++)
            {
                if (document.Resolve(current) is not PdfIndirectReference next) break;
                current = next;
            }
            return (current.ObjectNumber, current.Generation);
        }
    }

    private static PdfDictionary With(PdfDictionary dictionary, PdfName key, PdfObject value) =>
        new(dictionary.Select(entry => entry.Key.Equals(key)
            ? new KeyValuePair<PdfName, PdfObject>(key, value) : entry));

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
