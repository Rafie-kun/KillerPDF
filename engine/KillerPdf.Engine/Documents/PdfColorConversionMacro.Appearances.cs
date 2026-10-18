using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

public static partial class PdfColorConversionMacro
{
    private enum AppearanceContext { Document, AcroForm, WidgetColors, GraphicsStates, GraphicsState, ColorSpaces }

    private static byte[] ConvertAppearances(PdfDocument document, PdfMacroColorOptions options, CancellationToken token)
    {
        var update = new PdfIncrementalUpdateBuilder(document);
        var visited = new Dictionary<(int Number, int Generation, AppearanceContext Context), PdfObject>();
        bool changed = false;
        if (!document.CrossReferences.TryGetTrailerValue(Name("Root"), out PdfObject? root))
            throw new InvalidOperationException("The PDF has no catalog.");
        Visit(root, 0);
        return changed ? update.Build() : document.Source.ToArray();

        PdfObject Visit(PdfObject value, int depth, AppearanceContext context = AppearanceContext.Document)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 256) throw new NotSupportedException("The PDF object graph is too deeply nested for color conversion.");
            if (value is PdfIndirectReference reference)
            {
                var key = (reference.ObjectNumber, reference.Generation, context);
                if (visited.TryGetValue(key, out PdfObject? completed)) return completed;
                visited[key] = reference;
                PdfObject original = document.Resolve(reference);
                PdfObject replacement = Visit(original, depth + 1, context);
                if (ReferenceEquals(original, replacement)) return reference;
                changed = true;
                if (original is PdfStream) return visited[key] = update.AddObject(replacement);
                update.ReplaceObject(reference.ObjectNumber, replacement);
                return reference;
            }
            if (value is PdfStream stream)
            {
                string? subtype = Kind(stream.Dictionary, "Subtype");
                if (Kind(stream.Dictionary, "Type") == "XObject" && subtype is not ("Form" or "Image"))
                    throw new NotSupportedException("Editable color conversion does not yet preserve this XObject type.");
                var dictionary = (PdfDictionary)Visit(stream.Dictionary, depth + 1);
                if (subtype == "Image") return ConvertImage(document, stream, dictionary, options, token);
                if (subtype != "Form") return ReferenceEquals(dictionary, stream.Dictionary) ? stream
                    : PdfStream.FromOwnedData(dictionary, stream.EncodedData);
                var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
                foreach (string key in new[] { "Filter", "DecodeParms", "Length" }) entries.Remove(Name(key));
                byte[] data = PdfContentStreamWriter.Write(ConvertContent(PdfContentStreamReader.Read(document.DecodeStream(stream),
                    cancellationToken: token), options, token, inheritColorSpace: true,
                    colorSpaces: ReadDeviceColorSpaces(document, stream.Dictionary.GetValueOrDefault(Name("Resources")))));
                return new PdfStream(new PdfDictionary(entries), data);
            }
            if (value is PdfArray array)
            {
                PdfObject[] items = [.. array.Select(item => Visit(item, depth + 1))];
                return items.Where((item, index) => !ReferenceEquals(item, array[index])).Any() ? new PdfArray(items) : array;
            }
            if (value is not PdfDictionary source) return value;
            string? subtypeName = Kind(source, "Subtype");
            bool annotation = Kind(source, "Type") == "Annot"
                || subtypeName is "Widget" or "FreeText" or "Text" or "Line" or "Square" or "Circle" or "Ink";
            if (subtypeName == "Type3") throw new NotSupportedException("Color conversion of Type 3 glyphs is not yet supported.");
            if (context == AppearanceContext.AcroForm && source.ContainsKey(Name("XFA")))
                throw new NotSupportedException("Editable color conversion does not yet preserve XFA colors.");
            if (context == AppearanceContext.ColorSpaces
                && new[] { "DefaultGray", "DefaultRGB", "DefaultCMYK" }.Any(key => source.ContainsKey(Name(key))))
                throw new NotSupportedException("Editable color conversion does not yet preserve device color-space replacements.");
            if ((FieldType(source) == "Tx" && (source.ContainsKey(Name("RV")) || source.ContainsKey(Name("DS"))))
                || (annotation && (source.ContainsKey(Name("RC")) || source.ContainsKey(Name("DS")))))
                throw new NotSupportedException("Editable color conversion does not yet preserve rich-text colors.");
            if (context == AppearanceContext.GraphicsState || Kind(source, "Type") == "ExtGState")
            {
                if (new[] { "op", "OP" }.Any(key => source.TryGetValue(Name(key), out PdfObject? setting)
                    && Resolve(setting) is PdfBoolean { Value: true }))
                    throw new NotSupportedException("Editable color conversion does not yet preserve overprinting.");
                if (source.TryGetValue(Name("SMask"), out PdfObject? softMask) && Resolve(softMask) is PdfDictionary)
                    throw new NotSupportedException("Editable color conversion does not yet preserve group soft masks.");
                if (source.ContainsKey(Name("TR")) || source.ContainsKey(Name("TR2")))
                    throw new NotSupportedException("Editable color conversion does not yet preserve transfer functions.");
            }
            var copied = source.ToDictionary(entry => entry.Key, entry => entry.Value);
            bool rewritten = false;
            foreach ((PdfName name, PdfObject item) in source)
            {
                PdfObject replacement;
                if (name.ValueAsLatin1() == "SMask") replacement = item;
                else if (name.ValueAsLatin1() == "DA" && Resolve(item) is PdfString appearance
                    && (context == AppearanceContext.AcroForm || annotation || FieldType(source) is "Tx" or "Ch"))
                    replacement = new PdfString(PdfContentStreamWriter.Write(ConvertContent(
                        PdfContentStreamReader.Read(appearance.Bytes, cancellationToken: token), options, token)), appearance.Form);
                else if (((context == AppearanceContext.WidgetColors && name.ValueAsLatin1() is "BG" or "BC")
                    || (annotation && name.ValueAsLatin1() is "C" or "IC")) && Resolve(item) is PdfArray colors)
                    replacement = ConvertColors(colors);
                else replacement = Visit(item, depth + 1, name.ValueAsLatin1() switch
                {
                    _ when context == AppearanceContext.GraphicsStates => AppearanceContext.GraphicsState,
                    "ExtGState" => AppearanceContext.GraphicsStates,
                    "ColorSpace" => AppearanceContext.ColorSpaces,
                    "AcroForm" when Kind(source, "Type") == "Catalog" => AppearanceContext.AcroForm,
                    "MK" when annotation => AppearanceContext.WidgetColors,
                    _ => AppearanceContext.Document
                });
                if (!ReferenceEquals(replacement, item)) { copied[name] = replacement; rewritten = true; }
            }
            return rewritten ? new PdfDictionary(copied) : source;
        }

        PdfArray ConvertColors(PdfArray colors)
        {
            if (colors.Count == 0) return colors;
            byte gray = colors.Count switch
            {
                1 => Byte(Number(Resolve(colors[0]))),
                3 => Gray(Byte(Number(Resolve(colors[0]))), Byte(Number(Resolve(colors[1]))), Byte(Number(Resolve(colors[2])))),
                4 => CmykGray(Byte(Number(Resolve(colors[0]))), Byte(Number(Resolve(colors[1]))),
                    Byte(Number(Resolve(colors[2]))), Byte(Number(Resolve(colors[3])))),
                _ => throw new NotSupportedException("The annotation color space is not yet supported.")
            };
            if (options.Mode == PdfMacroColorMode.BlackAndWhite) gray = gray >= options.Threshold ? (byte)255 : (byte)0;
            return new PdfArray([new PdfReal(gray / 255d)]);
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

        string? Kind(PdfDictionary dictionary, string key) =>
            dictionary.TryGetValue(Name(key), out PdfObject? value) && Resolve(value) is PdfName name
                ? name.ValueAsLatin1() : null;

        string? FieldType(PdfDictionary field)
        {
            for (int depth = 0; depth < 64; depth++)
            {
                if (field.TryGetValue(Name("FT"), out PdfObject? kind)) return Resolve(kind) is PdfName type ? type.ValueAsLatin1() : null;
                if (!field.TryGetValue(Name("Parent"), out PdfObject? parent) || Resolve(parent) is not PdfDictionary inherited) return null;
                field = inherited;
            }
            throw new InvalidOperationException("A field inheritance chain is too deeply nested.");
        }
    }

    private static PdfName Name(string name) => new(System.Text.Encoding.ASCII.GetBytes(name));
}
