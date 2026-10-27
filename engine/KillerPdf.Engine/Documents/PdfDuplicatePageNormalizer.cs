using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

// Recovery builds an isolated delta. Object identity in the strict reader stays unchanged.
internal sealed class PdfDuplicatePageNormalizer(PdfDocument document)
{
    private const int MaximumObjects = 16384;
    private readonly Dictionary<int, PdfObject> _delta = [];
    private readonly List<Occurrence> _pages = [];
    private readonly Dictionary<int, List<Occurrence>> _groups = [];
    private readonly Dictionary<int, List<PdfIndirectReference>> _fieldWidgets = [];
    private readonly Dictionary<int, List<PdfIndirectReference>> _widgetCopies = [];
    private readonly Dictionary<int, int> _annotationOwners = [];
    private readonly HashSet<int> _mergedFields = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<int> _visitedNodes = [];
    private readonly SortedDictionary<string, (PdfString Key, PdfIndirectReference Owner)> _tagIdentities = new(StringComparer.Ordinal);
    private readonly int _firstNewObject = checked(document.CrossReferences.Keys.Max() + 1);
    private int _next = checked(document.CrossReferences.Keys.Max() + 1);
    private int _edges;
    internal bool HasDuplicateOccurrences { get; private set; }

    private sealed class Occurrence(PdfIndirectReference source, PdfDictionary page,
        PdfIndirectReference target, int index)
    {
        internal PdfIndirectReference Source { get; } = source;
        internal PdfDictionary Page { get; set; } = page;
        internal PdfIndirectReference Target { get; } = target;
        internal int Index { get; } = index;
        internal Dictionary<int, PdfIndirectReference> Annotations { get; } = [];
        internal Dictionary<int, PdfIndirectReference> Tags { get; } = [];
        internal Dictionary<long, long> ParentKeys { get; } = [];
    }

    internal IReadOnlyDictionary<int, PdfObject>? Normalize(PdfIndirectReference root,
        PdfDictionary catalog)
    {
        Visit(root, new Dictionary<PdfName, PdfObject>(), new HashSet<int>(), 0);
        if (!_groups.Values.Any(group => group.Count > 1)) return null;
        ValidateAnnotationOwners();
        foreach (Occurrence page in _pages)
        {
            if (_groups[page.Source.ObjectNumber].Count > 1)
            {
                CheckResourceOwnership(Get(page.Page, "Resources"), new HashSet<int>(), 0);
                PrepareAnnotations(page);
            }
        }
        foreach (Occurrence page in _pages.Where(page => _groups[page.Source.ObjectNumber].Count > 1))
        {
            WriteAnnotations(page);
            page.Page = RemapActions(page.Page, page);
        }
        WriteFields();
        NormalizeTags(catalog);
        foreach (Occurrence page in _pages)
            Put(page.Target, Set(Set(page.Page, "Parent", root), "Type", Name("Page")));
        PdfDictionary pages = Dictionary(root);
        Put(root, Set(Set(Remove(pages, "Parent"), "Kids", new PdfArray(_pages.Select(page => page.Target))),
            "Count", new PdfInteger(_pages.Count)));
        return _delta;
    }

    private void ValidateAnnotationOwners()
    {
        foreach (Occurrence page in _groups.Values.Select(group => group[0]))
        {
            if (Get(page.Page, "Annots") is not PdfObject annotations) continue;
            foreach (PdfObject value in Array(annotations))
            {
                Bound(0);
                PdfDictionary annotation = Dictionary(value);
                if (Get(annotation, "P") is PdfObject owner
                    && (owner is not PdfIndirectReference ownerReference
                        || FinalReference(ownerReference).ObjectNumber != page.Source.ObjectNumber))
                    throw Refuse("An annotation declares a different page owner.");
                if (value is PdfIndirectReference reference
                    && !_annotationOwners.TryAdd(FinalReference(reference).ObjectNumber, page.Source.ObjectNumber))
                    throw Refuse("An annotation belongs to more than one original page position.");
                if (Get(annotation, "NM") is PdfObject nameValue)
                {
                    PdfString name = Resolve(nameValue) as PdfString ?? throw Refuse("An annotation name is not a string.");
                    _names.Add(PdfUnicodeEncoding.DecodeTextString(name.Bytes.Span, "A recovered annotation name"));
                }
            }
        }
    }

    private void Visit(PdfIndirectReference reference, Dictionary<PdfName, PdfObject> inherited,
        HashSet<int> active, int depth)
    {
        Bound(depth);
        reference = FinalReference(reference);
        if (!active.Add(reference.ObjectNumber)) throw Refuse("A repeated page-tree path contains a cycle.");
        if (!_visitedNodes.Add(reference.ObjectNumber)) HasDuplicateOccurrences = true;
        try
        {
            PdfDictionary node = Dictionary(reference);
            var effective = new Dictionary<PdfName, PdfObject>(inherited);
            foreach (string key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
                if (node.TryGetValue(Name(key), out PdfObject? value)) effective[Name(key)] = value;
            if (Get(node, "Kids") is PdfObject kids)
            {
                foreach (PdfObject child in Array(kids))
                {
                    if (child is not PdfIndirectReference childReference)
                        throw Refuse("A repeated page-tree child is not an indirect reference.");
                    Visit(childReference, effective, active, depth + 1);
                }
                return;
            }
            if (_pages.Count >= 4096) throw Refuse("The projected page count exceeds the recovery budget.");
            if (!_groups.TryGetValue(reference.ObjectNumber, out List<Occurrence>? group))
                _groups.Add(reference.ObjectNumber, group = []);
            PdfDictionary materialized = node;
            foreach (var pair in effective) materialized = Set(materialized, pair.Key.ValueAsLatin1(), pair.Value);
            var occurrence = new Occurrence(reference, materialized, group.Count == 0 ? reference : Reserve(), group.Count);
            group.Add(occurrence);
            _pages.Add(occurrence);
        }
        finally { active.Remove(reference.ObjectNumber); }
    }

    private void PrepareAnnotations(Occurrence page)
    {
        if (Get(page.Page, "Annots") is not PdfObject annotations) return;
        var pending = new Queue<PdfObject>(Array(annotations));
        while (pending.TryDequeue(out PdfObject? value))
        {
            Bound(0);
            if (value is not PdfIndirectReference reference)
                throw Refuse("A duplicated page has a direct annotation with ambiguous ownership.");
            reference = FinalReference(reference);
            PdfDictionary annotation = Dictionary(reference);
            if (page.Annotations.ContainsKey(reference.ObjectNumber)) continue;
            if (_annotationOwners.TryGetValue(reference.ObjectNumber, out int owner) && owner != page.Source.ObjectNumber)
                throw Refuse("A popup relationship crosses another page's annotation ownership.");
            _annotationOwners[reference.ObjectNumber] = page.Source.ObjectNumber;
            if (Get(annotation, "P") is PdfObject ownerValue
                && (ownerValue is not PdfIndirectReference ownerReference
                    || FinalReference(ownerReference).ObjectNumber != page.Source.ObjectNumber))
                throw Refuse("A linked popup declares a different page owner.");
            if (Get(annotation, "NM") is PdfObject nameValue)
            {
                PdfString name = Resolve(nameValue) as PdfString ?? throw Refuse("An annotation name is not a string.");
                _names.Add(PdfUnicodeEncoding.DecodeTextString(name.Bytes.Span, "A recovered annotation name"));
            }
            bool widget = IsName(Get(annotation, "Subtype"), "Widget");
            bool merged = widget && new[] { "FT", "T", "V", "DV", "Ff", "Kids" }
                .Any(key => annotation.ContainsKey(Name(key)));
            if (merged && (Get(annotation, "Kids") is not null || annotation.Keys.Any(key =>
                !FieldKeys.Contains(key.ValueAsLatin1()) && !WidgetKeys.Contains(key.ValueAsLatin1()))))
                throw Refuse("A merged field/widget has ownership data that cannot be partitioned safely.");
            PdfIndirectReference target = page.Index == 0 && !merged ? reference : Reserve();
            page.Annotations.Add(reference.ObjectNumber, target);
            if (Get(annotation, "Popup") is PdfObject popup) pending.Enqueue(popup);
            if (IsName(Get(annotation, "Subtype"), "Popup") && Get(annotation, "Parent") is PdfObject parent)
                pending.Enqueue(parent);
            if (!widget) continue;
            PdfIndirectReference field = merged ? reference
                : Get(annotation, "Parent") as PdfIndirectReference
                    ?? throw Refuse("A widget has no identifiable logical field.");
            field = FinalReference(field);
            if (merged) _mergedFields.Add(field.ObjectNumber);
            if (!_fieldWidgets.TryGetValue(field.ObjectNumber, out List<PdfIndirectReference>? widgets))
                _fieldWidgets.Add(field.ObjectNumber, widgets = []);
            widgets.Add(target);
            if (!_widgetCopies.TryGetValue(reference.ObjectNumber, out List<PdfIndirectReference>? copies))
                _widgetCopies.Add(reference.ObjectNumber, copies = []);
            copies.Add(target);
        }
    }

    private void WriteAnnotations(Occurrence page)
    {
        foreach (var pair in page.Annotations)
        {
            PdfDictionary source = Dictionary(Reference(pair.Key));
            PdfDictionary result = Set(source, "P", page.Target);
            bool merged = _mergedFields.Contains(pair.Key);
            if (merged)
            {
                result = Remove(result, FieldKeys);
                result = Set(result, "Parent", Reference(pair.Key));
                if (Get(source, "AA") is PdfObject actions)
                    result = Set(result, "AA", Remove(Dictionary(actions), "K", "F", "V", "C"));
            }
            foreach (string key in new[] { "Popup", "IRT" })
                result = RemapRelationship(result, key, page);
            if (IsName(Get(source, "Subtype"), "Popup"))
                result = RemapRelationship(result, "Parent", page);
            if (Get(source, "Dest") is PdfObject destination)
                result = Set(result, "Dest", LocalDestination(destination, page));
            result = RemapActions(result, page);
            if (page.Index > 0 && Get(source, "NM") is PdfObject)
            {
                string name = "recovered-annotation-" + pair.Value.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                while (!_names.Add(name)) name += "x";
                result = Set(result, "NM", new PdfString(Encoding.ASCII.GetBytes(name), PdfStringForm.Literal));
            }
            Put(pair.Value, result);
        }
        if (Get(page.Page, "Annots") is PdfObject annotations)
            page.Page = Set(page.Page, "Annots", new PdfArray(Array(annotations).Select(value =>
                page.Annotations[FinalReference((PdfIndirectReference)value).ObjectNumber])));
    }

    private PdfDictionary RemapRelationship(PdfDictionary value, string key, Occurrence page)
    {
        if (Get(value, key) is not PdfObject target) return value;
        if (target is not PdfIndirectReference reference) throw Refuse("An annotation relationship is not indirect.");
        reference = FinalReference(reference);
        if (page.Annotations.TryGetValue(reference.ObjectNumber, out PdfIndirectReference? replacement))
            return Set(value, key, replacement);
        if (key == "IRT" && Get(Dictionary(reference), "P") is PdfIndirectReference otherPage
            && FinalReference(otherPage).ObjectNumber != page.Source.ObjectNumber) return value;
        throw Refuse("An annotation relationship leaves the page's ownership set.");
    }

    private PdfObject LocalDestination(PdfObject value, Occurrence page)
    {
        if (Resolve(value) is not PdfArray destination || destination.Count == 0
            || destination[0] is not PdfIndirectReference target
            || FinalReference(target).ObjectNumber != page.Source.ObjectNumber) return value;
        return new PdfArray(destination.Select((item, index) => index == 0 ? page.Target : item));
    }

    private PdfDictionary RemapActions(PdfDictionary owner, Occurrence page)
    {
        if (Get(owner, "A") is PdfObject action)
            owner = Set(owner, "A", RemapAction(action, page, new HashSet<int>(), 0));
        if (Get(owner, "AA") is PdfObject additional)
            owner = Set(owner, "AA", new PdfDictionary(Dictionary(additional).Select(pair =>
                new KeyValuePair<PdfName, PdfObject>(pair.Key, RemapAction(pair.Value, page, new HashSet<int>(), 0)))));
        return owner;
    }

    private PdfObject RemapAction(PdfObject value, Occurrence page, HashSet<int> active, int depth)
    {
        Bound(depth);
        PdfIndirectReference? reference = value as PdfIndirectReference;
        if (reference is not null)
        {
            reference = FinalReference(reference);
            if (!active.Add(reference.ObjectNumber)) throw Refuse("A copied action chain contains a cycle.");
        }
        try
        {
            if (Resolve(value) is PdfArray array)
                return new PdfArray(array.Select(child => RemapAction(child, page, active, depth + 1)));
            PdfDictionary action = Dictionary(value);
            if (IsName(Get(action, "S"), "GoTo") && Get(action, "D") is PdfObject target)
                action = Set(action, "D", LocalDestination(target, page));
            if (Get(action, "Next") is PdfObject next)
                action = Set(action, "Next", RemapAction(next, page, active, depth + 1));
            return action;
        }
        finally { if (reference is not null) active.Remove(reference.ObjectNumber); }
    }

    private static readonly string[] FieldKeys =
        ["FT", "T", "TU", "TM", "Ff", "V", "DV", "DA", "Q", "Opt", "MaxLen", "DS", "RV", "TI", "I", "Kids", "Lock", "SV"];
    private static readonly string[] WidgetKeys =
        ["Type", "Subtype", "Rect", "Contents", "P", "NM", "M", "F", "AP", "AS", "Border", "C",
         "StructParent", "OC", "AF", "Lang", "A", "AA", "BS", "H", "MK", "Parent"];
    private void WriteFields()
    {
        foreach (var pair in _fieldWidgets)
        {
            PdfIndirectReference reference = Reference(pair.Key);
            PdfDictionary field = Dictionary(reference);
            PdfObject[] originalKids = _mergedFields.Contains(pair.Key) ? [reference]
                : Get(field, "Kids") is PdfObject kids ? Array(kids).ToArray()
                : throw Refuse("A widget's logical field has no reciprocal children.");
            PdfArray? options = IsName(FieldType(field), "Btn") && Get(field, "Opt") is PdfObject optionValue
                ? Array(optionValue) : null;
            if (options is not null && options.Count != originalKids.Length)
                throw Refuse("Button export options do not correspond to the original widget order.");
            var widgets = new List<PdfObject>();
            var exportOptions = new List<PdfObject>();
            for (int index = 0; index < originalKids.Length; index++)
            {
                Bound(0);
                PdfIndirectReference child = originalKids[index] is PdfIndirectReference childReference
                    ? FinalReference(childReference) : throw Refuse("A logical field child has no indirect identity.");
                IEnumerable<PdfIndirectReference> copies = _widgetCopies.TryGetValue(child.ObjectNumber, out var replacements)
                    ? replacements : [child];
                foreach (PdfIndirectReference copy in copies)
                {
                    widgets.Add(copy);
                    if (options is not null) exportOptions.Add(options[index]);
                }
            }
            if (_mergedFields.Contains(pair.Key))
            {
                field = new PdfDictionary(field.Where(item => FieldKeys.Contains(item.Key.ValueAsLatin1())
                    || item.Key.Equals(Name("Parent"))));
                PdfDictionary original = Dictionary(reference);
                if (Get(original, "AA") is PdfObject actions)
                    field = Set(field, "AA", new PdfDictionary(Dictionary(actions).Where(item =>
                        new[] { "K", "F", "V", "C" }.Contains(item.Key.ValueAsLatin1()))));
            }
            if (options is not null) field = Set(field, "Opt", new PdfArray(exportOptions));
            Put(reference, Set(field, "Kids", new PdfArray(widgets)));
        }
    }

    private PdfObject? FieldType(PdfDictionary field)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (Get(field, "FT") is PdfObject type) return type;
            if (Get(field, "Parent") is not PdfObject parent) return null;
            field = Dictionary(parent);
        }
        throw Refuse("A logical field hierarchy exceeds the recovery depth.");
    }

    private void NormalizeTags(PdfDictionary catalog)
    {
        if (Get(catalog, "StructTreeRoot") is not PdfObject rootValue)
        {
            if (_pages.Any(page => Get(page.Page, "StructParents") is not null
                || page.Annotations.Keys.Any(number => Get(Dictionary(Reference(number)), "StructParent") is not null)))
                throw Refuse("Tagged ownership has no structure tree.");
            return;
        }
        if (rootValue is not PdfIndirectReference root) throw Refuse("The structure root is not indirect.");
        PdfDictionary structure = Dictionary(root);
        if (Get(structure, "IDTree") is PdfObject identityTree)
        {
            foreach (PdfNameTreeEntry entry in PdfNameTree.Read(document, identityTree))
            {
                Bound(0);
                if (_tagIdentities.Count >= MaximumObjects || entry.Value is not PdfIndirectReference identityOwner
                    || Get(Dictionary(identityOwner), "ID") is not PdfObject identifier
                    || Resolve(identifier) is not PdfString id || !id.Bytes.Span.SequenceEqual(entry.Key.Bytes.Span))
                    throw Refuse("A structure identity tree has an invalid owner or exceeds the recovery budget.");
                _tagIdentities.Add(Convert.ToHexString(entry.Key.Bytes.Span), (entry.Key, FinalReference(identityOwner)));
            }
        }
        int originalIdentityCount = _tagIdentities.Count;
        if (Get(structure, "ParentTree") is not PdfObject parentTree)
            throw Refuse("The structure root has no parent tree.");
        var entries = PdfNumberTree.Read(document, parentTree).ToDictionary(entry => entry.Key, entry => entry.Value);
        if (entries.Count > MaximumObjects || entries.Keys.Any(key => key < 0)) throw Refuse("The parent tree exceeds the recovery budget.");
        long next = entries.Count == 0 ? 0 : checked(entries.Keys.Max() + 1);
        foreach (Occurrence page in _pages.Where(page => page.Index > 0))
        {
            if (Get(page.Page, "StructParents") is PdfObject pageKey)
            {
                long old = Integer(pageKey);
                page.ParentKeys.Add(old, next++);
                page.Page = Set(page.Page, "StructParents", new PdfInteger(page.ParentKeys[old]));
            }
            foreach (var annotation in page.Annotations)
            {
                PdfDictionary source = Dictionary(Reference(annotation.Key));
                if (Get(source, "StructParent") is not PdfObject key) continue;
                long old = Integer(key);
                if (!page.ParentKeys.TryAdd(old, next++)) throw Refuse("Tagged owners share a parent-tree key.");
                Put(annotation.Value, Set((PdfDictionary)_delta[annotation.Value.ObjectNumber],
                    "StructParent", new PdfInteger(page.ParentKeys[old])));
            }
        }
        if (Get(structure, "K") is PdfObject children)
            structure = Set(structure, "K", Expand(children, root, new HashSet<int>(), 0));
        foreach (Occurrence page in _pages.Where(page => page.Index > 0))
            foreach (var tag in page.Tags)
            {
                PdfDictionary copied = (PdfDictionary)_delta[tag.Value.ObjectNumber];
                if (Get(copied, "Ref") is not PdfObject relationships) continue;
                var mapped = new List<PdfObject>();
                foreach (PdfObject item in Array(relationships))
                {
                    Bound(0);
                    if (item is not PdfIndirectReference relationship)
                        throw Refuse("A structure relationship has no indirect identity.");
                    PdfIndirectReference original = FinalReference(relationship);
                    mapped.Add(page.Tags.TryGetValue(original.ObjectNumber, out PdfIndirectReference? target) ? target : original);
                }
                Put(tag.Value, Set(copied, "Ref", new PdfArray(mapped)));
            }
        if (_tagIdentities.Count != originalIdentityCount)
            structure = Set(structure, "IDTree", new PdfDictionary([
                new(Name("Names"), new PdfArray(_tagIdentities.Values.SelectMany(entry =>
                    new PdfObject[] { entry.Key, entry.Owner })))]));
        foreach (Occurrence page in _pages.Where(page => page.Index > 0))
            foreach (var key in page.ParentKeys)
            {
                if (!entries.TryGetValue(key.Key, out PdfObject? old)) throw Refuse("A tagged owner has no parent-tree entry.");
                entries.Add(key.Value, MapParentEntry(old, page));
            }
        if (entries.Count > MaximumObjects) throw Refuse("The projected parent tree exceeds the recovery budget.");
        structure = Set(Set(structure, "ParentTree", new PdfDictionary([
            new(Name("Nums"), new PdfArray(entries.OrderBy(pair => pair.Key).SelectMany(pair =>
                new PdfObject[] { new PdfInteger(pair.Key), pair.Value })))])), "ParentTreeNextKey", new PdfInteger(next));
        Put(root, structure);
    }

    private PdfObject Expand(PdfObject value, PdfIndirectReference parent, HashSet<int> active, int depth)
    {
        Bound(depth);
        if (Resolve(value) is PdfArray children)
        {
            var result = new List<PdfObject>();
            foreach (PdfObject child in children)
            {
                PdfObject expanded = Expand(child, parent, active, depth + 1);
                if (expanded is PdfArray list) result.AddRange(list); else result.Add(expanded);
            }
            return new PdfArray(result);
        }
        if (value is not PdfIndirectReference reference) throw Refuse("A structure branch has ambiguous direct ownership.");
        reference = FinalReference(reference);
        if (!active.Add(reference.ObjectNumber)) throw Refuse("The structure tree contains a cycle.");
        try
        {
            PdfDictionary node = Dictionary(reference);
            if (Get(node, "Pg") is PdfIndirectReference owner && _groups.TryGetValue(FinalReference(owner).ObjectNumber, out List<Occurrence>? group))
            {
                CopyTag(reference, parent, group[0], false, new HashSet<int>(), depth + 1);
                if (group.Count == 1) return reference;
                var copies = new List<PdfObject> { reference };
                foreach (Occurrence page in group.Skip(1))
                {
                    PdfIndirectReference target = Reserve();
                    page.Tags.Add(reference.ObjectNumber, target);
                    CopyTag(reference, parent, page, true, new HashSet<int>(), depth + 1);
                    copies.Add(target);
                }
                return new PdfArray(copies);
            }
            if (Get(node, "K") is PdfObject kids) node = Set(node, "K", Expand(kids, reference, active, depth + 1));
            Put(reference, node);
            return reference;
        }
        finally { active.Remove(reference.ObjectNumber); }
    }

    private PdfObject CopyTag(PdfObject value, PdfIndirectReference parent, Occurrence page,
        bool clone, HashSet<int> active, int depth)
    {
        Bound(depth);
        PdfIndirectReference? reference = value as PdfIndirectReference;
        if (reference is not null)
        {
            reference = FinalReference(reference);
            if (!active.Add(reference.ObjectNumber)) throw Refuse("A page-owned structure branch contains a cycle.");
        }
        try
        {
            PdfObject resolved = Resolve(value);
            if (resolved is PdfArray array) return new PdfArray(array.Select(child => CopyTag(child, parent, page, clone, active, depth + 1)));
            if (resolved is PdfInteger or PdfNull) return resolved;
            if (resolved is not PdfDictionary node) throw Refuse("A page-owned structure child has an unsupported representation.");
            if (Get(node, "Pg") is PdfIndirectReference owner && FinalReference(owner).ObjectNumber != page.Source.ObjectNumber)
                throw Refuse("A page-owned structure branch crosses another page.");
            PdfDictionary result = node;
            bool element = IsName(Get(node, "Type"), "StructElem") || Get(node, "S") is not null;
            PdfIndirectReference target = parent;
            if (element)
            {
                if (reference is null) throw Refuse("A structure element has no indirect identity.");
                if (!clone) target = reference;
                else if (!page.Tags.TryGetValue(reference.ObjectNumber, out target!))
                    page.Tags.Add(reference.ObjectNumber, target = Reserve());
                result = Set(result, "P", parent);
                if (clone && Get(node, "ID") is PdfObject identifier)
                {
                    if (Resolve(identifier) is not PdfString id
                        || !_tagIdentities.TryGetValue(Convert.ToHexString(id.Bytes.Span), out var identity)
                        || identity.Owner.ObjectNumber != reference.ObjectNumber)
                        throw Refuse("A copied structure identifier has no matching registered owner.");
                    PdfString copiedId;
                    string key;
                    int suffix = 0;
                    do
                    {
                        Bound(0);
                        copiedId = new PdfString(Encoding.ASCII.GetBytes(FormattableString.Invariant(
                            $"recovered-structure-{target.ObjectNumber}-{suffix++}")), PdfStringForm.Hexadecimal);
                        key = Convert.ToHexString(copiedId.Bytes.Span);
                    } while (_tagIdentities.ContainsKey(key));
                    if (_tagIdentities.Count >= MaximumObjects) throw Refuse("The projected structure identity tree exceeds the recovery budget.");
                    _tagIdentities.Add(key, (copiedId, target));
                    result = Set(result, "ID", copiedId);
                }
            }
            if (Get(node, "Pg") is not null) result = Set(result, "Pg", page.Target);
            if (Get(node, "Stm") is not null) throw Refuse("A duplicated structure branch owns a tagged content stream.");
            if (Get(node, "Obj") is PdfIndirectReference obj)
            {
                PdfIndirectReference original = FinalReference(obj);
                if (!page.Annotations.TryGetValue(original.ObjectNumber, out PdfIndirectReference? annotation))
                {
                    if (clone || _groups[page.Source.ObjectNumber].Count != 1
                        || !_annotationOwners.TryGetValue(original.ObjectNumber, out int annotationOwner)
                        || annotationOwner != page.Source.ObjectNumber)
                        throw Refuse("A structure object reference leaves the page ownership set.");
                    annotation = original;
                }
                result = Set(result, "Obj", annotation);
            }
            if (Get(node, "K") is PdfObject kids)
                result = Set(result, "K", CopyTag(kids, target, page, clone, active, depth + 1));
            if (element && reference is not null && reference.ObjectNumber != parent.ObjectNumber)
            {
                Put(target, result);
                return target;
            }
            return result;
        }
        finally { if (reference is not null) active.Remove(reference.ObjectNumber); }
    }

    private PdfObject MapParentEntry(PdfObject value, Occurrence page, int depth = 0)
    {
        Bound(depth);
        if (value is PdfIndirectReference reference && page.Tags.TryGetValue(FinalReference(reference).ObjectNumber, out PdfIndirectReference? mapped)) return mapped;
        if (Resolve(value) is PdfArray array) return new PdfArray(array.Select(child => MapParentEntry(child, page, depth + 1)));
        if (value is PdfNull) return value;
        throw Refuse("A parent-tree entry has no matching projected structure element.");
    }

    private void CheckResourceOwnership(PdfObject? value, HashSet<int> seen, int depth)
    {
        if (value is null) return;
        Bound(depth);
        if (value is PdfIndirectReference reference && !seen.Add(reference.ObjectNumber)) return;
        PdfObject resolved = Resolve(value);
        PdfDictionary? dictionary = resolved switch { PdfDictionary item => item, PdfStream stream => stream.Dictionary, _ => null };
        if (dictionary is not null)
        {
            if (Get(dictionary, "StructParents") is not null || Get(dictionary, "StructParent") is not null)
                throw Refuse("Duplicated page resources contain separately owned tagged content.");
            foreach (var pair in dictionary) CheckResourceOwnership(pair.Value, seen, depth + 1);
        }
        else if (resolved is PdfArray array)
            foreach (PdfObject child in array) CheckResourceOwnership(child, seen, depth + 1);
    }

    private PdfIndirectReference Reserve()
    {
        if (_next - _firstNewObject >= MaximumObjects) throw Refuse("The projected object count exceeds the recovery budget.");
        return new PdfIndirectReference(checked(_next++), 0);
    }
    private void Put(PdfIndirectReference reference, PdfObject value)
    {
        if (_delta.Count >= MaximumObjects && !_delta.ContainsKey(reference.ObjectNumber)) throw Refuse("The recovery object delta exceeds its budget.");
        _delta[reference.ObjectNumber] = value;
    }
    private void Bound(int depth)
    {
        if (depth >= 256 || ++_edges > 262144) throw Refuse("The projected recovery graph exceeds its traversal budget.");
    }
    private PdfIndirectReference Reference(int number) => new(number,
        document.CrossReferences.TryGetValue(number, out var entry) && entry.Type == CrossReference.PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0);
    private PdfIndirectReference FinalReference(PdfIndirectReference reference)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (document.Resolve(reference) is not PdfIndirectReference next) return reference;
            reference = next;
        }
        throw Refuse("A recovered object is too deeply indirect.");
    }
    private PdfObject Resolve(PdfObject value) => value is PdfIndirectReference reference ? document.Resolve(FinalReference(reference)) : value;
    private PdfDictionary Dictionary(PdfObject value) => Resolve(value) as PdfDictionary ?? throw Refuse("A recovered owner is not a dictionary.");
    private PdfArray Array(PdfObject value) => Resolve(value) as PdfArray ?? throw Refuse("A recovered ownership list is not an array.");
    private long Integer(PdfObject value) => Resolve(value) is PdfInteger integer && integer.Value >= 0 ? integer.Value : throw Refuse("A structure parent key is invalid.");
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfObject? Get(PdfDictionary value, string key) => value.TryGetValue(Name(key), out PdfObject? result) ? result : null;
    private bool IsName(PdfObject? value, string name) => value is not null && Resolve(value) is PdfName actual && actual.Equals(Name(name));
    private static PdfDictionary Set(PdfDictionary value, string key, PdfObject replacement) => new(value.Where(pair => !pair.Key.Equals(Name(key))).Append(new(Name(key), replacement)));
    private static PdfDictionary Remove(PdfDictionary value, params string[] keys) => new(value.Where(pair => !keys.Contains(pair.Key.ValueAsLatin1())));
    private static NotSupportedException Refuse(string message) => new(message);
}
