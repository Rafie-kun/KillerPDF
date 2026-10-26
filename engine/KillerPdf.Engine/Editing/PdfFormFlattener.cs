using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Removes editable fields, painting visible appearances and retaining print-only annotations.</summary>
public static class PdfFormFlattener
{
    private static readonly PdfName AppearanceName = Name("AP");
    private static readonly PdfName NormalName = Name("N");
    private static readonly PdfName AppearanceStateName = Name("AS");
    private static readonly PdfName BoundingBoxName = Name("BBox");
    private static readonly PdfName MatrixName = Name("Matrix");
    private static readonly PdfName ResourcesName = Name("Resources");
    private static readonly PdfName XObjectName = Name("XObject");
    private static readonly PdfName ContentsName = Name("Contents");

    /// <summary>Flattens every AcroForm widget using its selected normal appearance.</summary>
    public static byte[] Flatten(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfFormWidgetInfo[] widgets = [.. Enumerable.Range(0, tree.Pages.Count)
            .SelectMany(pageIndex => PdfFormWidgetReader.ReadPage(document, pageIndex))];
        if (widgets.Length == 0) return document.Source.ToArray();
        if (widgets.Any(widget => widget.ObjectNumber <= 0))
            throw new NotSupportedException(
                "Flattening direct form widgets is not supported because they have no stable identity.");

        PdfFormWidgetInfo[] missingTextAppearances = [.. widgets.Where(widget =>
            widget.FieldKind == PdfFormFieldKind.Text
            && Resolve(document, new PdfIndirectReference(widget.ObjectNumber, widget.Generation))
                is PdfDictionary field && !field.ContainsKey(AppearanceName))];
        if (missingTextAppearances.Length > 0)
        {
            var appearanceEditor = new PdfIncrementalPageEditor(document);
            foreach (PdfFormWidgetInfo widget in missingTextAppearances.DistinctBy(widget => widget.FieldName))
                appearanceEditor.SetTextFieldValue(widget.FieldName, widget.Value);
            document = PdfDocument.Open(appearanceEditor.Build());
            tree = PdfPageTree.Read(document);
        }

        var update = new PdfIncrementalUpdateBuilder(document);
        var importer = new PdfObjectGraphImporter(document, update, []);
        var contentAppearances = new Dictionary<PdfStream, PdfIndirectReference>(ReferenceEqualityComparer.Instance);
        foreach (PdfOptionalContentGroupInfo group in PdfOptionalContentReader.Read(document).Groups)
        {
            var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
            importer.SeedReference(reference, reference);
        }
        int resourceNumber = 1;
        foreach (IGrouping<int, PdfFormWidgetInfo> pageWidgets in widgets
            .GroupBy(widget => widget.PageIndex))
        {
            PdfPageTreeEntry page = tree.Pages[pageWidgets.Key];
            PdfDictionary resources = EffectiveResources(document, page);
            var resourceEntries = resources.ToDictionary(item => item.Key, item => item.Value);
            PdfDictionary existingXObjects = resourceEntries.TryGetValue(
                    XObjectName, out PdfObject? xObjectValue)
                ? Resolve(document, xObjectValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A page /XObject resource is not a dictionary.")
                : new PdfDictionary([]);
            var xObjects = existingXObjects.ToDictionary(item => item.Key, item => item.Value);
            var properties = resourceEntries.TryGetValue(Name("Properties"), out PdfObject? propertiesValue)
                ? (Resolve(document, propertiesValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A page /Properties resource is not a dictionary."))
                    .ToDictionary(item => item.Key, item => item.Value)
                : new Dictionary<PdfName, PdfObject>();
            var retainedAppearances = new List<PdfObject>();
            var commands = new StringBuilder("Q\n/Artifact BMC\n");
            foreach (PdfFormWidgetInfo widget in pageWidgets)
            {
                if ((widget.AnnotationFlags & 2) != 0
                    || (widget.AnnotationFlags & 36) == 32) continue;
                var reference = new PdfIndirectReference(widget.ObjectNumber, widget.Generation);
                PdfDictionary dictionary = Resolve(document, reference) as PdfDictionary
                    ?? throw new InvalidOperationException("A form widget is not a dictionary.");
                PdfObject? appearanceValue = SelectedAppearance(document, dictionary,
                    widget.FieldName);
                if (appearanceValue is null) continue;
                PdfStream appearance = Resolve(document, appearanceValue) as PdfStream
                    ?? throw new InvalidOperationException(
                        $"Form field '{widget.FieldName}' has no stream normal appearance.");
                (double left, double bottom, double right, double top) =
                    TransformedBounds(document, appearance, widget.FieldName);
                double sourceWidth = right - left;
                double sourceHeight = top - bottom;
                double targetWidth = widget.Right - widget.Left;
                double targetHeight = widget.Top - widget.Bottom;
                if (sourceWidth <= 0 || sourceHeight <= 0
                    || targetWidth <= 0 || targetHeight <= 0)
                    throw new InvalidOperationException(
                        $"Form field '{widget.FieldName}' has invalid appearance geometry.");
                if ((widget.AnnotationFlags & 36) == 36)
                {
                    var stamp = new Dictionary<PdfName, PdfObject>
                    {
                        [Name("Type")] = Name("Annot"),
                        [Name("Subtype")] = Name("Stamp"),
                        [Name("Rect")] = dictionary[Name("Rect")],
                        [Name("P")] = page.Reference,
                        [Name("F")] = new PdfInteger(widget.AnnotationFlags | 64 | 128 | 512),
                        [AppearanceName] = new PdfDictionary(new Dictionary<PdfName, PdfObject>
                        {
                            [NormalName] = importer.Import(appearanceValue)
                        })
                    };
                    if (dictionary.TryGetValue(Name("OC"), out PdfObject? optionalContent))
                        stamp[Name("OC")] = optionalContent;
                    retainedAppearances.Add(update.AddObject(new PdfDictionary(stamp)));
                    continue;
                }
                double scaleX = targetWidth / sourceWidth;
                double scaleY = targetHeight / sourceHeight;
                double translateX = widget.Left - left * scaleX;
                double translateY = widget.Bottom - bottom * scaleY;
                PdfName resource;
                do resource = Name("KpfFlat" + resourceNumber++);
                while (xObjects.ContainsKey(resource));
                xObjects[resource] = ImportContentAppearance(appearanceValue, appearance);
                bool layered = dictionary.TryGetValue(Name("OC"), out PdfObject? layer);
                if (layered)
                {
                    PdfName property;
                    do property = Name("KpfFlatOc" + resourceNumber++);
                    while (properties.ContainsKey(property));
                    properties[property] = layer!;
                    commands.Append("/OC /").Append(property.ValueAsLatin1()).Append(" BDC\n");
                }
                commands.Append("q ")
                    .Append(Number(scaleX)).Append(" 0 0 ")
                    .Append(Number(scaleY)).Append(' ')
                    .Append(Number(translateX)).Append(' ')
                    .Append(Number(translateY)).Append(" cm /")
                    .Append(resource.ValueAsLatin1()).Append(" Do Q\n");
                if (layered) commands.Append("EMC\n");
            }
            commands.Append("EMC\n");
            resourceEntries[XObjectName] = new PdfDictionary(xObjects);
            if (properties.Count > 0) resourceEntries[Name("Properties")] = new PdfDictionary(properties);
            PdfIndirectReference overlay = update.AddObject(new PdfStream(
                new PdfDictionary([]), Encoding.ASCII.GetBytes(commands.ToString())));
            PdfIndirectReference saveState = update.AddObject(new PdfStream(
                new PdfDictionary([]), "q\n"u8));
            PdfObject contents = page.Dictionary.TryGetValue(
                    ContentsName, out PdfObject? existingContents)
                ? Resolve(document, existingContents) is PdfArray array
                    ? new PdfArray(new PdfObject[] { saveState }.Concat(array).Append(overlay))
                    : new PdfArray([saveState, existingContents, overlay])
                : new PdfArray([saveState, overlay]);
            var pageEntries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);
            pageEntries[ResourcesName] = new PdfDictionary(resourceEntries);
            pageEntries[ContentsName] = contents;
            if (retainedAppearances.Count > 0)
            {
                PdfName annotationsName = Name("Annots");
                PdfArray annotations = page.Dictionary.TryGetValue(annotationsName, out PdfObject? value)
                    ? Resolve(document, value) as PdfArray
                        ?? throw new InvalidOperationException("A page /Annots value is not an array.")
                    : new PdfArray([]);
                pageEntries[annotationsName] = new PdfArray(annotations.Concat(retainedAppearances));
            }
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        }
        PdfDocument painted = PdfDocument.Open(update.Build());
        var editor = new PdfIncrementalPageEditor(painted);
        foreach (string fieldName in widgets.Select(widget => widget.FieldName)
            .Distinct(StringComparer.Ordinal))
            editor.RemoveFormField(fieldName);
        return editor.Build();

        PdfObject ImportContentAppearance(PdfObject value, PdfStream appearance)
        {
            PdfName optionalContentName = Name("OC");
            if (!appearance.Dictionary.ContainsKey(optionalContentName)) return importer.Import(value);
            if (contentAppearances.TryGetValue(appearance, out PdfIndirectReference? existing)) return existing;
            // The annotation appearance did not invoke its root through Do. Baking it through Do
            // must not introduce a new outer layer condition. Ordinary uses keep their original
            // XObject identity, while layers inside the appearance and on the annotation remain.
            var dictionary = new PdfDictionary(appearance.Dictionary.Where(pair => !pair.Key.Equals(optionalContentName)));
            PdfIndirectReference reference = update.AddObject(importer.Import(new PdfStream(dictionary, appearance.EncodedData.Span)));
            contentAppearances.Add(appearance, reference);
            return reference;
        }
    }

    private static PdfObject? SelectedAppearance(PdfDocument document,
        PdfDictionary widget, string fieldName)
    {
        PdfDictionary appearance = widget.TryGetValue(AppearanceName, out PdfObject? value)
            ? Resolve(document, value) as PdfDictionary
                ?? throw new InvalidOperationException(
                    $"Form field '{fieldName}' has an invalid appearance dictionary.")
            : throw new InvalidOperationException(
                $"Form field '{fieldName}' has no appearance dictionary.");
        PdfObject normal = appearance.TryGetValue(NormalName, out PdfObject? normalValue)
            ? normalValue : throw new InvalidOperationException(
                $"Form field '{fieldName}' has no normal appearance.");
        if (Resolve(document, normal) is PdfStream) return normal;
        PdfDictionary states = Resolve(document, normal) as PdfDictionary
            ?? throw new InvalidOperationException(
                $"Form field '{fieldName}' has an invalid normal appearance.");
        PdfName state = widget.TryGetValue(AppearanceStateName, out PdfObject? stateValue)
            ? Resolve(document, stateValue) as PdfName ?? Name("Off") : Name("Off");
        if (state.Equals(Name("Off")) && !states.ContainsKey(state)) return null;
        return states.TryGetValue(state, out PdfObject? selected) ? selected
            : states.TryGetValue(Name("Off"), out PdfObject? off) ? off
            : throw new InvalidOperationException(
                $"Form field '{fieldName}' has no selected appearance state.");
    }

    private static (double Left, double Bottom, double Right, double Top) TransformedBounds(
        PdfDocument document, PdfStream appearance, string fieldName)
    {
        PdfArray box = appearance.Dictionary.TryGetValue(BoundingBoxName, out PdfObject? value)
            ? Resolve(document, value) as PdfArray
                ?? throw new InvalidOperationException(
                    $"Form field '{fieldName}' has an invalid appearance bounding box.")
            : throw new InvalidOperationException(
                $"Form field '{fieldName}' has no appearance bounding box.");
        if (box.Count != 4) throw new InvalidOperationException(
            $"Form field '{fieldName}' has an invalid appearance bounding box.");
        double[] matrix = appearance.Dictionary.TryGetValue(MatrixName, out PdfObject? matrixValue)
            ? Values(document, matrixValue, 6, fieldName) : [1, 0, 0, 1, 0, 0];
        double[] bounds = Values(document, box, 4, fieldName);
        (double X, double Y)[] corners =
        [
            Transform(bounds[0], bounds[1]), Transform(bounds[0], bounds[3]),
            Transform(bounds[2], bounds[1]), Transform(bounds[2], bounds[3])
        ];
        return (corners.Min(point => point.X), corners.Min(point => point.Y),
            corners.Max(point => point.X), corners.Max(point => point.Y));

        (double X, double Y) Transform(double x, double y) =>
            (matrix[0] * x + matrix[2] * y + matrix[4],
             matrix[1] * x + matrix[3] * y + matrix[5]);
    }

    private static double[] Values(PdfDocument document, PdfObject value,
        int count, string fieldName)
    {
        PdfArray array = Resolve(document, value) as PdfArray
            ?? throw new InvalidOperationException(
                $"Form field '{fieldName}' has invalid appearance geometry.");
        if (array.Count != count) throw new InvalidOperationException(
            $"Form field '{fieldName}' has invalid appearance geometry.");
        return [.. array.Select(item => Resolve(document, item) switch
        {
            PdfInteger integer => (double)integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException(
                $"Form field '{fieldName}' has invalid appearance geometry.")
        })];
    }

    private static PdfDictionary EffectiveResources(
        PdfDocument document, PdfPageTreeEntry page) =>
        page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? value)
            ? Resolve(document, value) as PdfDictionary
                ?? throw new InvalidOperationException("A page /Resources value is not a dictionary.")
            : new PdfDictionary([]);

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A PDF reference chain contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static PdfName Name(string value) =>
        new(Encoding.ASCII.GetBytes(value));
}
