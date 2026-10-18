using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Writing;

internal static class PdfPageResizeDetails
{
    internal static byte[] Scale(PdfDocument document, double factor)
    {
        var update = new PdfIncrementalUpdateBuilder(document);
        var visited = new HashSet<(int, int)>();
        var tree = PdfPageTree.Read(document);
        var pages = tree.Pages
            .Select(page => (page.Reference.ObjectNumber, page.Reference.Generation)).ToHashSet();
        if (tree.Catalog.TryGetValue(new PdfName("AcroForm"u8), out PdfObject? formValue)
            && Resolve(formValue) is PdfDictionary form && form.TryGetValue(new PdfName("XFA"u8), out PdfObject? xfa)
            && Resolve(xfa) is not PdfNull)
            throw new NotSupportedException("Resize cannot preserve XFA layouts.");
        bool changed = false;
        if (!document.CrossReferences.TryGetTrailerValue(new PdfName("Root"u8), out PdfObject? root))
            throw new InvalidOperationException("The PDF has no catalog.");
        Visit(root, 0);
        return changed ? update.Build() : document.Source.ToArray();

        PdfObject Visit(PdfObject value, int depth, bool isAcroForm = false)
        {
            if (depth > 256) throw new NotSupportedException("The PDF object graph is too deeply nested to resize.");
            if (value is PdfIndirectReference reference)
            {
                if (visited.Add((reference.ObjectNumber, reference.Generation)))
                {
                    PdfObject original = document.Resolve(reference);
                    PdfObject replacement = Visit(original, depth + 1, isAcroForm);
                    if (!ReferenceEquals(original, replacement))
                    {
                        update.ReplaceObject(reference.ObjectNumber, replacement);
                        changed = true;
                    }
                }
                return value;
            }
            if (value is PdfStream stream)
            {
                var dictionary = (PdfDictionary)Visit(stream.Dictionary, depth + 1);
                return ReferenceEquals(dictionary, stream.Dictionary) ? stream
                    : PdfStream.FromOwnedData(dictionary, stream.EncodedData);
            }
            if (value is PdfArray array)
            {
                PdfObject[] items = [.. array.Select(item => Visit(item, depth + 1))];
                if (array.Count >= 2 && array[0] is PdfIndirectReference page
                    && pages.Contains((page.ObjectNumber, page.Generation)) && array[1] is PdfName mode)
                {
                    int coordinates = mode.ValueAsLatin1() switch
                    {
                        "XYZ" => 2,
                        "FitH" or "FitV" or "FitBH" or "FitBV" => 1,
                        "FitR" => 4,
                        _ => 0
                    };
                    if (array.Count < coordinates + 2)
                        throw new InvalidOperationException("A page destination has missing coordinates.");
                    for (int index = 2; index < coordinates + 2; index++)
                    {
                        PdfObject coordinate = array[index] is PdfIndirectReference coordinateReference
                            ? document.Resolve(coordinateReference) : array[index];
                        items[index] = coordinate switch
                        {
                            PdfInteger integer => new PdfReal(integer.Value * factor),
                            PdfReal real => new PdfReal(real.Value * factor),
                            PdfNull => coordinate,
                            _ => throw new InvalidOperationException("A page destination coordinate is not numeric.")
                        };
                    }
                }
                return items.Where((item, index) => !ReferenceEquals(item, array[index])).Any()
                    ? new PdfArray(items) : array;
            }
            if (value is not PdfDictionary source) return value;
            var entries = source.ToDictionary(item => item.Key, item => item.Value);
            bool rewritten = false;
            bool annotation = source.TryGetValue(new PdfName("Subtype"u8), out PdfObject? subtype)
                && subtype is PdfName subtypeName && subtypeName.ValueAsLatin1() is
                    "Widget" or "Line" or "PolyLine" or "Polygon" or "Ink" or "FreeText"
                    or "Square" or "Circle" or "Link" or "Highlight" or "Underline"
                    or "StrikeOut" or "Squiggly" or "Caret" or "Redact" or "Text" or "Popup" or "Stamp";
            foreach ((PdfName name, PdfObject item) in source)
            {
                PdfObject replacement;
                PdfObject resolved = Resolve(item);
                if (resolved is not PdfNull && ((annotation && name.ValueAsLatin1() is "RC" or "DS")
                    || (name.ValueAsLatin1() is "RV" or "DS" && FieldType(source) == "Tx")))
                    throw new NotSupportedException("Resize cannot preserve rich-text layouts.");
                if (annotation && name.ValueAsLatin1() is "L" or "Vertices" or "InkList" or "CL" or "RD"
                    or "CO" or "LL" or "LLE" or "LLO" or "KillerPDFCaption" or "Border")
                    replacement = ScaleGeometry(resolved);
                else if (annotation && name.Equals(new PdfName("BS"u8)) && resolved is PdfDictionary border)
                {
                    var style = border.ToDictionary(entry => entry.Key, entry => entry.Value);
                    PdfName width = new("W"u8);
                    style[width] = border.TryGetValue(width, out PdfObject? existingWidth)
                        ? ScaleGeometry(existingWidth) : new PdfReal(factor);
                    PdfName dash = new("D"u8);
                    if (border.TryGetValue(dash, out PdfObject? pattern)) style[dash] = ScaleGeometry(pattern);
                    replacement = new PdfDictionary(style);
                }
                else if (name.Equals(new PdfName("DA"u8)) && resolved is PdfString appearance
                    && (isAcroForm || annotation || FieldType(source) is "Tx" or "Ch"))
                {
                    IReadOnlyList<PdfContentInstruction> instructions = PdfContentStreamReader.Read(appearance.Bytes);
                    bool fontChanged = false;
                    var scaled = instructions.Select(instruction =>
                    {
                        if (instruction.Operator != "Tf" || instruction.Operands.Count != 2) return instruction;
                        double size = instruction.Operands[1] switch
                        {
                            PdfInteger integer => integer.Value,
                            PdfReal real => real.Value,
                            _ => throw new InvalidOperationException("A form font size is not numeric.")
                        };
                        if (size == 0) return instruction;
                        fontChanged = true;
                        return new PdfContentInstruction(instruction.Operator, instruction.Offset,
                            [instruction.Operands[0], new PdfReal(size * factor)]);
                    }).ToArray();
                    replacement = fontChanged ? new PdfString(PdfContentStreamWriter.Write(scaled), appearance.Form) : item;
                }
                else replacement = Visit(item, depth + 1, name.Equals(new PdfName("AcroForm"u8))
                    && source.TryGetValue(new PdfName("Type"u8), out PdfObject? type)
                    && Resolve(type) is PdfName kind && kind.ValueAsLatin1() == "Catalog");
                if (!ReferenceEquals(item, replacement))
                {
                    entries[name] = replacement;
                    rewritten = true;
                }
            }
            return rewritten ? new PdfDictionary(entries) : source;
        }

        PdfObject ScaleGeometry(PdfObject value)
        {
            value = Resolve(value);
            return value switch
            {
                PdfArray array => new PdfArray(array.Select(ScaleGeometry)),
                PdfInteger integer => new PdfReal(integer.Value * factor),
                PdfReal real => new PdfReal(real.Value * factor),
                _ => throw new InvalidOperationException("Annotation geometry is not numeric.")
            };
        }

        PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 64) throw new InvalidOperationException("An object reference chain is too deeply nested.");
                value = document.Resolve(reference);
            }
            return value;
        }

        string? FieldType(PdfDictionary field)
        {
            for (int depth = 0; depth < 64; depth++)
            {
                if (field.TryGetValue(new PdfName("FT"u8), out PdfObject? kind))
                    return Resolve(kind) is PdfName type ? type.ValueAsLatin1() : null;
                if (!field.TryGetValue(new PdfName("Parent"u8), out PdfObject? parent)
                    || Resolve(parent) is not PdfDictionary inherited) return null;
                field = inherited;
            }
            throw new InvalidOperationException("A field inheritance chain is too deeply nested.");
        }
    }
}
