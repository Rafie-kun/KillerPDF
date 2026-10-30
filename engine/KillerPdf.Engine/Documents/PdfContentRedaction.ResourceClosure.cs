using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

public static partial class PdfContentRedaction
{
    private static string DescriptionIdentity(PdfDictionary dictionary) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PdfObjectWriter.Write(dictionary)));

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

    // An image's samples live in its own stream and in its /SMask and /Mask streams, so all three
    // are retired, and later credited as used, together.
    private static void CollectImageStreams(PdfDocument document, PdfObject value, HashSet<int> numbers)
    {
        CollectSourceStreams(document, value, numbers);
        if (Resolve(document, value) is not PdfStream image) return;
        foreach (PdfName key in (PdfName[])[Name("SMask"), Name("Mask")])
            if (image.Dictionary.TryGetValue(key, out PdfObject? mask) && mask is PdfIndirectReference)
                CollectSourceStreams(document, mask, numbers);
    }

    private static void VerifyRetiredStreams(PdfDocument document, HashSet<int> retired,
        HashSet<string> retiredDescriptions,
        CancellationToken cancellationToken)
    {
        // Object numbers are preserved by the compact writer. A resource entry alone is
        // not evidence that an unredacted page still needs the original stream bytes.
        var retained = retired.Where(number => document.CrossReferences.TryGetValue(number, out var entry)
            && entry.Type is CrossReference.PdfCrossReferenceEntryType.InUse
                or CrossReference.PdfCrossReferenceEntryType.Compressed
            && document.Resolve(number) is PdfStream).ToHashSet();
        var descriptions = new HashSet<string>(StringComparer.Ordinal);
        if (retiredDescriptions.Count != 0)
            foreach (var entry in document.CrossReferences.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Type is CrossReference.PdfCrossReferenceEntryType.InUse
                    or CrossReference.PdfCrossReferenceEntryType.Compressed)
                    InspectDescriptions(document.Resolve(entry.ObjectNumber), 0);
            }
        if (retained.Count == 0 && descriptions.Count == 0) return;
        var walker = new RetiredUseWalker(document, cancellationToken);
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages) walker.WalkPage(page);
        var findings = retained.Except(walker.Used).Select(number => new PdfRedactionVerificationFinding(
            "RetainedSourceStream", "An original redacted stream remains without a verified unredacted use.",
            ObjectNumber: number)).Concat(descriptions.Except(walker.UsedDescriptions).Select(identity =>
                new PdfRedactionVerificationFinding("RetainedDescription",
                    "An affected description remains without a verified unaffected content use."))).ToArray();
        if (findings.Length != 0)
            throw new PdfRedactionVerificationException(new PdfRedactionVerificationReport(Array.AsReadOnly(findings)));

        void InspectDescriptions(PdfObject value, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > PdfObjectWriter.MaximumNestingDepth)
                throw new FormatException("Description inspection nesting limit exceeded.");
            if (value is PdfStream stream) value = stream.Dictionary;
            if (value is PdfDictionary dictionary)
            {
                if (ReplacementTextKeys.Any(dictionary.ContainsKey))
                {
                    string identity = DescriptionIdentity(dictionary);
                    if (retiredDescriptions.Contains(identity)) descriptions.Add(identity);
                }
                foreach (PdfObject child in dictionary.Values) InspectDescriptions(child, depth + 1);
            }
            else if (value is PdfArray array)
                foreach (PdfObject child in array) InspectDescriptions(child, depth + 1);
        }
    }

    // Walks what a page draws, through forms, patterns, soft masks, Type 3 glyphs and annotation
    // appearances, and records the streams and descriptions that are really used.
    private sealed class RetiredUseWalker(PdfDocument document, CancellationToken cancellationToken)
    {
        private readonly HashSet<PdfStream> active = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<int> walkedNumbers = [];
        private readonly HashSet<PdfObject> walkedObjects = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<(PdfStream, PdfDictionary)> walkedInherited = [];
        private readonly ContentRewriter context = new(document, null, [], false, [], cancellationToken);
        private int instructionCount;

        internal HashSet<int> Used { get; } = [];
        internal HashSet<string> UsedDescriptions { get; } = new(StringComparer.Ordinal);

        internal void WalkPage(PdfPageTreeEntry page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfDictionary resources = page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? value)
                && Resolve(document, value) is PdfDictionary dictionary ? dictionary : new PdfDictionary([]);
            // Annotation appearances draw images and forms of their own, in every appearance state.
            if (page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotationsValue)
                && Resolve(document, annotationsValue) is PdfArray annotations)
                foreach (PdfObject item in annotations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Resolve(document, item) is not PdfDictionary annotation
                        || !annotation.TryGetValue(Name("AP"), out PdfObject? appearancesValue)
                        || Resolve(document, appearancesValue) is not PdfDictionary appearances) continue;
                    foreach (PdfObject appearance in appearances.Values)
                        if (Resolve(document, appearance) is PdfDictionary states)
                            foreach (PdfObject state in states.Values) Credit(state, resources, 0, content: true);
                        else Credit(appearance, resources, 0, content: true);
                }
            if (!page.Dictionary.TryGetValue(ContentsName, out PdfObject? contents)) return;
            CollectSourceStreams(document, contents, Used);
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

        private void Visit(byte[] bytes, PdfDictionary resources, int depth)
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
                if (instruction.Operator is "BDC" or "DP" && instruction.Operands.Count == 2)
                {
                    PdfObject? property = instruction.Operands[1] is PdfName propertyName
                        ? scope.Lookup(Name("Properties"), propertyName) : instruction.Operands[1];
                    if (property is not null && Resolve(document, property) is PdfDictionary dictionary)
                        UsedDescriptions.Add(DescriptionIdentity(dictionary));
                }
                if (instruction.Operands.Count == 0) continue;
                PdfName? first = instruction.Operands[0] as PdfName, last = instruction.Operands[^1] as PdfName;
                switch (instruction.Operator)
                {
                    case "Do" when instruction.Operands.Count == 1 && first is not null:
                        Credit(scope.Lookup(Name("XObject"), first), resources, depth, content: false);
                        break;
                    case "gs" when instruction.Operands.Count == 1 && first is not null:
                        if (scope.Lookup(Name("ExtGState"), first) is not PdfObject gsValue
                            || Resolve(document, gsValue) is not PdfDictionary gs) break;
                        if (gs.TryGetValue(Name("SMask"), out PdfObject? maskValue)
                            && Resolve(document, maskValue) is PdfDictionary mask
                            && mask.TryGetValue(Name("G"), out PdfObject? groupValue))
                            Credit(groupValue, resources, depth, content: false);
                        // The renderer honors a font selected by a graphics state as it does one set by Tf.
                        if (gs.TryGetValue(Name("Font"), out PdfObject? fontEntry)
                            && Resolve(document, fontEntry) is PdfArray { Count: 2 } selected)
                            CreditFont(selected[0], resources, depth);
                        break;
                    case "scn" or "SCN" when last is not null:
                        // A tiling pattern's cell draws resources of its own, so it is walked once. One without
                        // any draws with the resources that selected it, so it is walked once for each.
                        if (scope.Lookup(Name("Pattern"), last) is PdfObject patternValue
                            && Resolve(document, patternValue) is PdfStream pattern
                            && (pattern.Dictionary.TryGetValue(ResourcesName, out PdfObject? patternResources)
                                && Resolve(document, patternResources) is PdfDictionary
                                ? FirstWalk(patternValue, pattern) : walkedInherited.Add((pattern, resources))))
                            Credit(patternValue, resources, depth, content: true);
                        break;
                    case "Tf" when first is not null:
                        CreditFont(scope.Lookup(Name("Font"), first), resources, depth);
                        break;
                }
            }
        }

        // Type 3 glyph procedures draw with the font's resources, or the page's when it has none.
        private void CreditFont(PdfObject? fontValue, PdfDictionary resources, int depth)
        {
            if (fontValue is null || Resolve(document, fontValue) is not PdfDictionary font
                || !IsNamed(font, SubtypeName, "Type3")
                || !font.TryGetValue(Name("CharProcs"), out PdfObject? proceduresValue)
                || Resolve(document, proceduresValue) is not PdfDictionary procedures) return;
            bool ownResources = font.TryGetValue(ResourcesName, out PdfObject? fontResourcesValue)
                && Resolve(document, fontResourcesValue) is PdfDictionary;
            if (ownResources && !FirstWalk(fontValue, font)) return;
            PdfDictionary glyphResources = ownResources ? (PdfDictionary)Resolve(document, fontResourcesValue!) : resources;
            foreach (PdfObject procedure in procedures.Values) Credit(procedure, glyphResources, depth, content: true);
        }

        // Credits a drawn stream and, for a form or other content stream, walks what it draws in turn.
        private void Credit(PdfObject? value, PdfDictionary resources, int depth, bool content)
        {
            if (value is null || Resolve(document, value) is not PdfStream stream) return;
            if (IsNamed(stream.Dictionary, SubtypeName, "Image"))
            {
                CollectImageStreams(document, value, Used);
                return;
            }
            CollectSourceStreams(document, value, Used);
            if (!content && !IsNamed(stream.Dictionary, SubtypeName, "Form")) return;
            if (!active.Add(stream)) throw new FormatException("Recursive form in redaction verification.");
            try
            {
                PdfDictionary nested = stream.Dictionary.TryGetValue(ResourcesName, out PdfObject? own)
                    && Resolve(document, own) is PdfDictionary dictionary ? dictionary : resources;
                Visit(document.DecodeStream(stream, PdfContentStreamReader.MaximumSourceBytes), nested, depth + 1);
            }
            finally { active.Remove(stream); }
        }

        private bool FirstWalk(PdfObject value, PdfObject resolved) => value is PdfIndirectReference reference
            ? walkedNumbers.Add(reference.ObjectNumber) : walkedObjects.Add(resolved);
    }

    // Pages, form appearances and the AcroForm defaults can share one resource dictionary with a redacted
    // page, as LibreOffice exports do, and so still list an original image or form that nothing draws.
    // Those entries leave every XObject dictionary, so only what some page really draws stays reachable.
    // The caller verifies the result as strictly as any other, and gets null back when nothing was listed.
    // An entry that a form, pattern or glyph procedure of the written file still names stays, so a route
    // the walker misses ends in that verification's refusal instead of a silent deletion.
    private static PdfDocument? DropUndrawnRetiredResources(PdfDocument document, PdfDocument written,
        HashSet<int> retired, CancellationToken cancellationToken)
    {
        PdfName xObjectName = Name("XObject");
        int[] numbers = [.. document.CrossReferences.Values.Where(entry => entry.Type is
            CrossReference.PdfCrossReferenceEntryType.InUse or CrossReference.PdfCrossReferenceEntryType.Compressed)
            .Select(entry => entry.ObjectNumber)];
        var sharedDictionaries = new HashSet<int>();
        var listed = new HashSet<int>();
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scan(document.Resolve(number), 0);
        }
        if (listed.Count == 0) return null;

        var walker = new RetiredUseWalker(document, cancellationToken);
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages) walker.WalkPage(page);
        HashSet<int> undrawn = [.. listed.Except(walker.Used)];
        if (undrawn.Count == 0) return null;
        if (DrawnNames(written, undrawn, cancellationToken) is not HashSet<PdfName> drawnNames) return null;

        var update = new PdfIncrementalUpdateBuilder(document);
        bool replaced = false;
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfObject value = document.Resolve(number);
            // A stream with resources of its own is left alone, so that case still fails verification.
            PdfObject? changed = value is PdfStream ? null
                : sharedDictionaries.Contains(number) && value is PdfDictionary shared ? WithoutUndrawn(shared)
                : Strip(value, 0);
            if (changed is null) continue;
            update.ReplaceObject(number, changed);
            replaced = true;
        }
        return replaced ? PdfDocument.Open(update.Build()) : null;

        void Scan(PdfObject value, int depth)
        {
            if (depth > PdfObjectWriter.MaximumNestingDepth)
                throw new FormatException("Resource inspection nesting limit exceeded.");
            if (value is PdfStream stream) value = stream.Dictionary;
            if (value is PdfDictionary dictionary)
                foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary)
                {
                    if (entry.Key.Equals(xObjectName))
                    {
                        if (entry.Value is PdfIndirectReference reference && document.Resolve(reference) is PdfDictionary indirect)
                        {
                            sharedDictionaries.Add(reference.ObjectNumber);
                            List(indirect);
                        }
                        else if (entry.Value is PdfDictionary direct) List(direct);
                    }
                    Scan(entry.Value, depth + 1);
                }
            else if (value is PdfArray array)
                foreach (PdfObject item in array) Scan(item, depth + 1);
        }

        void List(PdfDictionary xObjects)
        {
            foreach (PdfObject entry in xObjects.Values)
            {
                var targets = new HashSet<int>();
                CollectImageStreams(document, entry, targets);
                listed.UnionWith(targets.Where(retired.Contains));
            }
        }

        PdfDictionary? WithoutUndrawn(PdfDictionary xObjects)
        {
            var kept = new Dictionary<PdfName, PdfObject>();
            foreach (KeyValuePair<PdfName, PdfObject> entry in xObjects)
            {
                var targets = new HashSet<int>();
                CollectImageStreams(document, entry.Value, targets);
                if (!targets.Overlaps(undrawn) || drawnNames.Contains(entry.Key)) kept[entry.Key] = entry.Value;
            }
            return kept.Count == xObjects.Count ? null : new PdfDictionary(kept);
        }

        PdfObject? Strip(PdfObject value, int depth)
        {
            if (depth > PdfObjectWriter.MaximumNestingDepth)
                throw new FormatException("Resource inspection nesting limit exceeded.");
            if (value is PdfDictionary dictionary)
            {
                Dictionary<PdfName, PdfObject>? entries = null;
                foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary)
                {
                    PdfObject? stripped = entry.Key.Equals(xObjectName) && entry.Value is PdfDictionary xObjects
                        ? WithoutUndrawn(xObjects) : Strip(entry.Value, depth + 1);
                    if (stripped is not null) (entries ??= dictionary.ToDictionary(item => item.Key, item => item.Value))[entry.Key] = stripped;
                }
                return entries is null ? null : new PdfDictionary(entries);
            }
            if (value is PdfArray array)
            {
                List<PdfObject>? items = null;
                for (int index = 0; index < array.Count; index++)
                    if (Strip(array[index], depth + 1) is { } stripped) (items ??= [.. array])[index] = stripped;
                return items is null ? null : new PdfArray(items);
            }
            return null;
        }
    }

    // The names every form, tiling pattern and Type 3 glyph procedure invokes with Do, apart from the
    // undrawn streams themselves, which leave with their entries. Null means one could not be read.
    private static HashSet<PdfName>? DrawnNames(PdfDocument document, HashSet<int> undrawn,
        CancellationToken cancellationToken)
    {
        var streams = new List<PdfStream>();
        foreach (var entry in document.CrossReferences.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type is not (CrossReference.PdfCrossReferenceEntryType.InUse
                or CrossReference.PdfCrossReferenceEntryType.Compressed) || undrawn.Contains(entry.ObjectNumber)) continue;
            PdfObject value = document.Resolve(entry.ObjectNumber);
            if (value is PdfStream stream)
            {
                if (IsNamed(stream.Dictionary, SubtypeName, "Form")
                    || stream.Dictionary.TryGetValue(Name("PatternType"), out PdfObject? kind)
                    && Resolve(document, kind) is PdfInteger { Value: 1 }) streams.Add(stream);
            }
            else if (value is PdfDictionary font && font.TryGetValue(Name("CharProcs"), out PdfObject? proceduresValue)
                && Resolve(document, proceduresValue) is PdfDictionary procedures)
                streams.AddRange(procedures.Values.Select(item => Resolve(document, item)).OfType<PdfStream>());
        }
        var names = new HashSet<PdfName>();
        foreach (PdfStream stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (PdfContentInstruction instruction in PdfContentStreamReader.Read(
                    document.DecodeStream(stream, PdfContentStreamReader.MaximumSourceBytes),
                    compatibilityRecovery: document.UsesCompatibilityRecovery, cancellationToken: cancellationToken))
                    if (instruction.Operator == "Do" && instruction.Operands.Count == 1
                        && instruction.Operands[0] is PdfName name) names.Add(name);
            }
            catch (Exception error) when (error is FormatException or NotSupportedException
                or InvalidDataException or Filters.PdfFilterException) { return null; }
        }
        return names;
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
