using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

// A recovered page tree alone does not prove that its owned objects are consistent.
internal sealed class PdfRecoveredOwnershipValidator(PdfDocument document)
{
    private readonly Dictionary<int, (PdfIndirectReference Page, PdfDictionary Dictionary)> _annotations = [];
    private readonly Dictionary<(int Page, long Mcid), PdfIndirectReference> _content = [];
    private readonly Dictionary<int, PdfIndirectReference> _objects = [];
    private readonly Dictionary<int, PdfIndirectReference> _structure = [];
    private readonly Dictionary<int, (PdfIndirectReference Reference, PdfIndirectReference Page, PdfDictionary Dictionary)> _streams = [];
    private int _edges;

    internal void Validate()
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var widgets = new HashSet<int>();
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (Get(page.Dictionary, "Resources") is PdfObject resources)
                CollectTaggedStreams(resources, page.Reference, new HashSet<int>(), 0);
            if (Get(page.Dictionary, "Annots") is not PdfObject value) continue;
            foreach (PdfObject item in Array(value))
            {
                Bound(0);
                PdfIndirectReference reference = Reference(item);
                PdfDictionary annotation = Dictionary(reference);
                if (!_annotations.TryAdd(reference.ObjectNumber, (page.Reference, annotation)))
                    throw Refuse("Recovered annotations do not have unique page ownership.");
                if (Get(annotation, "P") is PdfObject owner && !Same(Reference(owner), page.Reference))
                    throw Refuse("A recovered annotation points to a different page.");
                if (Get(annotation, "NM") is PdfObject nameValue)
                {
                    PdfString name = Resolve(nameValue) as PdfString ?? throw Refuse("An annotation name is not a string.");
                    if (!names.Add(PdfUnicodeEncoding.DecodeTextString(name.Bytes.Span, "A recovered annotation name")))
                        throw Refuse("Recovered annotations have duplicate names.");
                }
                if (IsName(Get(annotation, "Subtype"), "Widget")) widgets.Add(reference.ObjectNumber);
            }
        }
        ValidatePopupClosure(names);
        ValidateFields(tree.Catalog, widgets);
        if (Get(tree.Catalog, "StructTreeRoot") is not PdfObject structureValue)
        {
            if (tree.Pages.Any(page => Get(page.Dictionary, "StructParents") is not null)
                || _annotations.Values.Any(owner => Get(owner.Dictionary, "StructParent") is not null) || _streams.Count > 0)
                throw Refuse("Recovered tagged owners have no structure tree.");
            return;
        }
        PdfIndirectReference root = Reference(structureValue);
        PdfDictionary structure = Dictionary(root);
        if (Get(structure, "K") is PdfObject children) Visit(children, root, null, null, 0);
        foreach (PdfIndirectReference reference in _structure.Values)
        {
            if (Get(Dictionary(reference), "Ref") is not PdfObject relationships) continue;
            foreach (PdfObject item in Array(relationships))
            {
                Bound(0);
                PdfIndirectReference target = Reference(item);
                if (!_structure.TryGetValue(target.ObjectNumber, out PdfIndirectReference? owner) || !Same(target, owner))
                    throw Refuse("A recovered structure relationship points outside the structure hierarchy.");
            }
        }
        if (Get(structure, "ParentTree") is not PdfObject parentValue)
            throw Refuse("Recovered structure has no parent tree.");
        var parents = PdfNumberTree.Read(document, parentValue).ToDictionary(entry => entry.Key, entry => entry.Value);
        if (parents.Count > 16384) throw Refuse("Recovered parent-tree validation exceeds its budget.");
        var keys = new HashSet<long>();
        var checkedContent = new HashSet<(int Page, long Mcid)>();
        var checkedObjects = new HashSet<int>();
        var contentOwners = tree.Pages.Select(page => (page.Reference, page.Dictionary))
            .Concat(_streams.Values.Select(stream => (stream.Reference, stream.Dictionary)));
        foreach (var page in contentOwners)
        {
            if (Get(page.Dictionary, "StructParents") is not PdfObject keyValue) continue;
            long key = Integer(keyValue);
            if (!keys.Add(key) || !parents.TryGetValue(key, out PdfObject? entry))
                throw Refuse("A recovered page has an ambiguous or missing parent-tree key.");
            PdfArray slots = Array(entry);
            for (int mcid = 0; mcid < slots.Count; mcid++)
            {
                Bound(0);
                if (Resolve(slots[mcid]) is PdfNull) continue;
                var identity = (page.Reference.ObjectNumber, (long)mcid);
                if (!_content.TryGetValue(identity, out PdfIndirectReference? element)
                    || !Same(Reference(slots[mcid]), element))
                    throw Refuse("A recovered page parent-tree entry points to the wrong marked content.");
                checkedContent.Add(identity);
            }
        }
        foreach (var pair in _annotations)
        {
            if (Get(pair.Value.Dictionary, "StructParent") is not PdfObject keyValue) continue;
            long key = Integer(keyValue);
            if (!keys.Add(key) || !parents.TryGetValue(key, out PdfObject? entry)
                || !_objects.TryGetValue(pair.Key, out PdfIndirectReference? element)
                || !Same(Reference(entry), element))
                throw Refuse("A recovered annotation parent-tree entry points to the wrong object reference.");
            checkedObjects.Add(pair.Key);
        }
        if (checkedContent.Count != _content.Count || checkedObjects.Count != _objects.Count)
            throw Refuse("Recovered structure content has no reciprocal parent-tree ownership.");
    }

    private void ValidatePopupClosure(HashSet<string> names)
    {
        var pending = new Queue<int>(_annotations.Keys);
        while (pending.TryDequeue(out int number))
        {
            Bound(0);
            var owner = _annotations[number];
            bool popup = IsName(Get(owner.Dictionary, "Subtype"), "Popup");
            string key = popup ? "Parent" : "Popup";
            if (Get(owner.Dictionary, key) is not PdfObject linked)
            {
                if (popup) throw Refuse("A recovered popup has no parent note.");
                continue;
            }
            PdfIndirectReference target = Reference(linked);
            PdfDictionary annotation = Dictionary(target);
            string reciprocal = popup ? "Popup" : "Parent";
            if (!popup && !IsName(Get(annotation, "Subtype"), "Popup")
                || Get(annotation, reciprocal) is not PdfObject back || Reference(back).ObjectNumber != number)
                throw Refuse("A recovered popup relationship is not reciprocal.");
            if (_annotations.TryGetValue(target.ObjectNumber, out var existing))
            {
                if (!Same(existing.Page, owner.Page)) throw Refuse("A recovered popup belongs to another page.");
                continue;
            }
            if (Get(annotation, "P") is not PdfObject page || !Same(Reference(page), owner.Page))
                throw Refuse("A recovered linked popup has a different page owner.");
            if (Get(annotation, "NM") is PdfObject nameValue)
            {
                PdfString name = Resolve(nameValue) as PdfString ?? throw Refuse("A linked annotation name is not a string.");
                if (!names.Add(PdfUnicodeEncoding.DecodeTextString(name.Bytes.Span, "A recovered annotation name")))
                    throw Refuse("A linked recovered annotation has a duplicate name.");
            }
            _annotations.Add(target.ObjectNumber, (owner.Page, annotation));
            pending.Enqueue(target.ObjectNumber);
        }
    }

    private void ValidateFields(PdfDictionary catalog, HashSet<int> widgets)
    {
        var found = new HashSet<int>();
        var visited = new HashSet<int>();
        if (Get(catalog, "AcroForm") is PdfObject formValue
            && Get(Dictionary(formValue), "Fields") is PdfObject fields)
            foreach (PdfObject field in Array(fields)) VisitField(field, null, 0);
        if (!found.SetEquals(widgets)) throw Refuse("Recovered form widgets do not match the logical field tree.");

        void VisitField(PdfObject value, PdfIndirectReference? parent, int depth)
        {
            Bound(depth);
            PdfIndirectReference reference = Reference(value);
            if (!visited.Add(reference.ObjectNumber)) throw Refuse("Recovered fields contain repeated ownership or a cycle.");
            PdfDictionary field = Dictionary(reference);
            if (parent is not null && (Get(field, "Parent") is not PdfObject owner || !Same(Reference(owner), parent)))
                throw Refuse("A recovered field child has no reciprocal parent.");
            if (IsName(Get(field, "Subtype"), "Widget")) found.Add(reference.ObjectNumber);
            if (Get(field, "Kids") is PdfObject kids)
                foreach (PdfObject child in Array(kids)) VisitField(child, reference, depth + 1);
        }
    }

    private void Visit(PdfObject value, PdfIndirectReference parent, PdfIndirectReference? page,
        PdfIndirectReference? element, int depth)
    {
        Bound(depth);
        PdfObject resolved = Resolve(value);
        if (resolved is PdfArray array)
        {
            foreach (PdfObject child in array) Visit(child, parent, page, element, depth + 1);
            return;
        }
        if (resolved is PdfNull) return;
        if (resolved is PdfInteger mcid)
        {
            AddContent(mcid.Value, page, element);
            return;
        }
        PdfDictionary node = resolved as PdfDictionary ?? throw Refuse("Recovered structure has an unsupported child.");
        if (Get(node, "Pg") is PdfObject owner) page = Reference(owner);
        if (IsName(Get(node, "Type"), "StructElem") || Get(node, "S") is not null)
        {
            PdfIndirectReference reference = Reference(value);
            if (!_structure.TryAdd(reference.ObjectNumber, reference)) throw Refuse("Recovered structure has repeated element ownership or a cycle.");
            if (Get(node, "P") is not PdfObject parentValue || !Same(Reference(parentValue), parent))
                throw Refuse("A recovered structure element has no reciprocal parent.");
            if (Get(node, "K") is PdfObject kids) Visit(kids, reference, page, reference, depth + 1);
            return;
        }
        if (IsName(Get(node, "Type"), "MCR"))
        {
            if (Get(node, "MCID") is not PdfObject id) throw Refuse("A recovered marked-content reference has no identifier.");
            PdfIndirectReference? contentOwner = page;
            if (Get(node, "Stm") is PdfObject streamValue)
            {
                PdfIndirectReference streamReference = Reference(streamValue);
                if (page is null || Get(node, "StmOwn") is not null
                    || !_streams.TryGetValue(streamReference.ObjectNumber, out var stream)
                    || !Same(streamReference, stream.Reference) || !Same(page, stream.Page))
                    throw Refuse("Recovered marked content references a stream outside its page resources.");
                contentOwner = streamReference;
            }
            AddContent(Integer(id), contentOwner, element);
            return;
        }
        if (Get(node, "Stm") is not null) throw Refuse("Recovered structure has an unsupported stream reference.");
        if (IsName(Get(node, "Type"), "OBJR"))
        {
            if (Get(node, "Obj") is not PdfObject objectValue || page is null || element is null)
                throw Refuse("A recovered object reference has no owner.");
            PdfIndirectReference target = Reference(objectValue);
            if (!_annotations.TryGetValue(target.ObjectNumber, out var annotation)
                || !Same(annotation.Page, page) || !_objects.TryAdd(target.ObjectNumber, element))
                throw Refuse("Recovered structure points to the wrong annotation owner.");
            return;
        }
        throw Refuse("Recovered structure has an unidentified ownership child.");
    }

    private void CollectTaggedStreams(PdfObject value, PdfIndirectReference page, HashSet<int> seen, int depth)
    {
        Bound(depth);
        PdfIndirectReference? reference = value is PdfIndirectReference ? Reference(value) : null;
        if (reference is not null && !seen.Add(reference.ObjectNumber)) return;
        PdfObject resolved = Resolve(value);
        PdfDictionary? dictionary = resolved switch { PdfDictionary item => item, PdfStream stream => stream.Dictionary, _ => null };
        if (dictionary is not null)
        {
            if (Get(dictionary, "StructParents") is not null)
            {
                if (reference is null || resolved is not PdfStream || !IsName(Get(dictionary, "Subtype"), "Form"))
                    throw Refuse("A recovered tagged resource has no supported stream identity.");
                if (_streams.TryGetValue(reference.ObjectNumber, out var existing) && !Same(existing.Page, page))
                    throw Refuse("A recovered tagged stream belongs to more than one page.");
                _streams[reference.ObjectNumber] = (reference, page, dictionary);
            }
            foreach (var pair in dictionary) CollectTaggedStreams(pair.Value, page, seen, depth + 1);
        }
        else if (resolved is PdfArray array)
            foreach (PdfObject child in array) CollectTaggedStreams(child, page, seen, depth + 1);
    }

    private void AddContent(long mcid, PdfIndirectReference? page, PdfIndirectReference? element)
    {
        if (mcid < 0 || page is null || element is null || !_content.TryAdd((page.ObjectNumber, mcid), element))
            throw Refuse("Recovered marked content has ambiguous ownership.");
    }
    private void Bound(int depth)
    {
        if (depth >= 256 || ++_edges > 262144) throw Refuse("Recovered ownership validation exceeds its traversal budget.");
    }
    private PdfIndirectReference Reference(PdfObject value)
    {
        for (int depth = 0; depth < 32 && value is PdfIndirectReference reference; depth++)
        {
            PdfObject next = document.Resolve(reference);
            if (next is not PdfIndirectReference) return reference;
            value = next;
        }
        throw Refuse("A recovered owner has no bounded indirect identity.");
    }
    private PdfObject Resolve(PdfObject value) => value is PdfIndirectReference ? document.Resolve(Reference(value)) : value;
    private PdfDictionary Dictionary(PdfObject value) => Resolve(value) as PdfDictionary ?? throw Refuse("A recovered owner is not a dictionary.");
    private PdfArray Array(PdfObject value) => Resolve(value) as PdfArray ?? throw Refuse("A recovered ownership list is not an array.");
    private long Integer(PdfObject value) => Resolve(value) is PdfInteger { Value: >= 0 } integer ? integer.Value : throw Refuse("A recovered ownership key is invalid.");
    private bool IsName(PdfObject? value, string name) => value is not null && Resolve(value) is PdfName actual && actual.Equals(Name(name));
    private static bool Same(PdfIndirectReference left, PdfIndirectReference right) => left.ObjectNumber == right.ObjectNumber && left.Generation == right.Generation;
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfObject? Get(PdfDictionary value, string key) => value.TryGetValue(Name(key), out PdfObject? result) ? result : null;
    private static NotSupportedException Refuse(string message) => new(message);
}
