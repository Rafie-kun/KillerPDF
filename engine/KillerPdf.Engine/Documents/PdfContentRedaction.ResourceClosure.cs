using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

public static partial class PdfContentRedaction
{
    private static void CollectSourceStreams(PdfDocument document, PdfObject value, HashSet<int> numbers,
        int depth = 0)
    {
        if (depth > 32) throw new FormatException("The source stream reference chain is too deep.");
        if (value is PdfIndirectReference reference)
        {
            PdfObject resolved = document.Resolve(reference);
            if (resolved is PdfStream) numbers.Add(reference.ObjectNumber);
            else CollectSourceStreams(document, resolved, numbers, depth + 1);
        }
        else if (value is PdfArray array)
            foreach (PdfObject item in array) CollectSourceStreams(document, item, numbers, depth + 1);
    }

    private static void VerifyRetiredStreams(PdfDocument document, HashSet<int> retired,
        CancellationToken cancellationToken)
    {
        // Object numbers are preserved by the compact writer. A resource entry alone is
        // not evidence that an unredacted page still needs the original stream bytes.
        var retained = retired.Where(number => document.CrossReferences.TryGetValue(number, out var entry)
            && entry.Type is CrossReference.PdfCrossReferenceEntryType.InUse
                or CrossReference.PdfCrossReferenceEntryType.Compressed
            && document.Resolve(number) is PdfStream).ToHashSet();
        if (retained.Count == 0) return;
        var used = new HashSet<int>();
        var active = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        var context = new ContentRewriter(document, null, [], false, [], cancellationToken);
        int instructionCount = 0;
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!page.Dictionary.TryGetValue(ContentsName, out PdfObject? contents)) continue;
            CollectSourceStreams(document, contents, used);
            PdfDictionary resources = page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? value)
                && Resolve(document, value) is PdfDictionary dictionary ? dictionary : new PdfDictionary([]);
            PdfObject resolved = Resolve(document, contents);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            using var bytes = new MemoryStream();
            foreach (PdfObject item in items)
            {
                if (Resolve(document, item) is not PdfStream stream) continue;
                bytes.Write(document.DecodeStream(stream, PdfContentStreamReader.MaximumSourceBytes));
                bytes.WriteByte((byte)'\n');
                if (bytes.Length > PdfContentStreamReader.MaximumSourceBytes)
                    throw new FormatException("Page content exceeds the redaction verification limit.");
            }
            Visit(bytes.ToArray(), resources, 0);
        }
        var findings = retained.Except(used).Select(number => new PdfRedactionVerificationFinding(
            "RetainedSourceStream", "An original redacted stream remains without a verified unredacted use.",
            ObjectNumber: number)).ToArray();
        if (findings.Length != 0)
            throw new PdfRedactionVerificationException(new PdfRedactionVerificationReport(Array.AsReadOnly(findings)));

        void Visit(byte[] bytes, PdfDictionary resources, int depth)
        {
            if (depth > 32) throw new FormatException("Form nesting exceeds the redaction verification limit.");
            var scope = new ResourceScope(document, resources);
            foreach (PdfContentInstruction instruction in PdfContentStreamReader.Read(bytes,
                resolveColorComponents: name => context.ColorComponents(scope, name),
                compatibilityRecovery: document.UsesCompatibilityRecovery, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++instructionCount > 2_000_000)
                    throw new FormatException("Redaction verification instruction limit exceeded.");
                if (instruction.Operands.Count != 1 || instruction.Operands[0] is not PdfName name) continue;
                PdfObject? value = null;
                if (instruction.Operator == "Do") value = scope.Lookup(Name("XObject"), name);
                else if (instruction.Operator == "gs"
                    && scope.Lookup(Name("ExtGState"), name) is PdfObject gsValue
                    && Resolve(document, gsValue) is PdfDictionary gs
                    && gs.TryGetValue(Name("SMask"), out PdfObject? maskValue)
                    && Resolve(document, maskValue) is PdfDictionary mask
                    && mask.TryGetValue(Name("G"), out PdfObject? groupValue)) value = groupValue;
                if (value is null || Resolve(document, value) is not PdfStream stream) continue;
                CollectSourceStreams(document, value, used);
                if (!IsNamed(stream.Dictionary, SubtypeName, "Form")) continue;
                if (!active.Add(stream)) throw new FormatException("Recursive form in redaction verification.");
                try
                {
                    PdfDictionary nested = stream.Dictionary.TryGetValue(ResourcesName, out PdfObject? own)
                        && Resolve(document, own) is PdfDictionary dictionary ? dictionary : resources;
                    Visit(document.DecodeStream(stream, PdfContentStreamReader.MaximumSourceBytes), nested, depth + 1);
                }
                finally { active.Remove(stream); }
            }
        }
    }

    private static void MaterializeInheritedResources(PdfDocument document, PdfPageTree tree,
        PdfIncrementalUpdateBuilder update, HashSet<int> redactedPages, CancellationToken cancellationToken)
    {
        var leaves = tree.Pages.Select(page => page.Reference.ObjectNumber).ToHashSet();
        var parents = new List<(int Number, PdfDictionary Dictionary)>();
        var visited = new HashSet<int>();
        var pending = new Stack<(PdfIndirectReference Reference, int Depth)>();
        pending.Push((tree.RootReference, 0));
        while (pending.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Depth > 256) throw new FormatException("The page resource inheritance is too deep.");
            if (!visited.Add(node.Reference.ObjectNumber) || leaves.Contains(node.Reference.ObjectNumber)) continue;
            if (Resolve(document, node.Reference) is not PdfDictionary dictionary)
                throw new FormatException("A page resource ancestor is not a dictionary.");
            if (dictionary.ContainsKey(ResourcesName)) parents.Add((node.Reference.ObjectNumber, dictionary));
            if (!dictionary.TryGetValue(KidsName, out PdfObject? value)
                || Resolve(document, value) is not PdfArray kids)
                throw new FormatException("A page resource ancestor has no readable children.");
            foreach (PdfObject kid in kids)
            {
                if (kid is not PdfIndirectReference child)
                    throw new FormatException("A page resource child is not an indirect reference.");
                pending.Push((child, node.Depth + 1));
            }
        }
        if (parents.Count == 0) return;

        // Capture each leaf's nearest effective dictionary before removing inheritance.
        // Redacted leaves receive their rebuilt resources in the caller's rewrite loop.
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (redactedPages.Contains(page.Index) || page.Dictionary.ContainsKey(ResourcesName)) continue;
            var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ResourcesName] = page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? resources)
                ? resources : new PdfDictionary([]);
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        }
        foreach ((int number, PdfDictionary dictionary) in parents)
            update.ReplaceObject(number, new PdfDictionary(dictionary.Where(entry => !entry.Key.Equals(ResourcesName))));
    }
}
