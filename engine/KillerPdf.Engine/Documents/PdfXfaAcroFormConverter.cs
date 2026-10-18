using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Authoring;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Converts positioned static XFA controls into editable AcroForm fields.</summary>
public static class PdfXfaAcroFormConverter
{
    private static readonly HashSet<string> TextControls = new(StringComparer.OrdinalIgnoreCase)
    {
        "dateTimeEdit", "defaultUi", "numericEdit", "passwordEdit", "textEdit"
    };
    private static readonly HashSet<string> ConvertibleControls = new(
        TextControls.Concat(["barcode", "checkButton", "choiceList", "imageEdit", "signature"]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Preserves source pages, removes XFA, and authors editable text widgets.</summary>
    public static byte[] Convert(PdfDocument document) =>
        Convert(document, PdfXfaConversionMode.Editable);

    /// <summary>Converts XFA fields to editable widgets or flattened page content.</summary>
    public static byte[] Convert(PdfDocument document, PdfXfaConversionMode mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        PdfXfaInfo info = PdfXfaReader.Read(document)
            ?? throw new InvalidOperationException("The document has no XFA form.");
        info = ExpandCombinedXdp(info);
        PdfXfaTemplateInfo template = PdfXfaTemplate.Read(info);
        var fields = template.Fields.GroupBy(field => field.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var repeated in fields.Where(group => group.Value.Length > 1))
        {
            string?[] bindings = repeated.Value.Select(field => BindingName(field.Binding)).ToArray();
            if (template.ScriptCount != 0
                || template.Behaviors.Count != 0
                || repeated.Value.Any(field => field.ControlType is null || !TextControls.Contains(field.ControlType))
                || bindings.Any(string.IsNullOrWhiteSpace)
                || bindings.Distinct(StringComparer.Ordinal).Count() != bindings.Length)
                throw new NotSupportedException(
                    $"XFA conversion cannot safely distinguish repeated template field path '{repeated.Key}'.");
        }
        PdfXfaTemplateField? unsupported = template.Fields.FirstOrDefault(field =>
            field.ControlType is null || !ConvertibleControls.Contains(field.ControlType));
        if (unsupported is not null)
            throw new NotSupportedException(
                $"XFA control '{unsupported.ControlType ?? "unknown"}' cannot be converted.");
        bool hasImages = template.Fields.Any(field =>
            field.ControlType!.Equals("imageEdit", StringComparison.OrdinalIgnoreCase));
        bool hasBarcodes = template.Fields.Any(field =>
            field.ControlType!.Equals("barcode", StringComparison.OrdinalIgnoreCase));
        if ((hasImages || hasBarcodes) && mode != PdfXfaConversionMode.Flattened)
            throw new NotSupportedException(
                "XFA image and barcode fields can be preserved only in flattened output.");
        Dictionary<string, PdfXfaImageValue> images = hasImages
            ? PdfXfaImages.Read(info).ToDictionary(image => image.FieldPath, StringComparer.Ordinal)
            : [];
        PdfFormDataSet data = PdfXfaDatasets.Read(info);
        foreach (var repeated in fields.Where(group => group.Value.Length > 1))
            foreach (var field in repeated.Value)
                if (!data.Fields.Any(value => value.Name == BindingName(field.Binding) && value.Values.Count == 1))
                    throw new NotSupportedException(
                        $"XFA conversion cannot safely bind repeated template field path '{repeated.Key}'.");
        Dictionary<string, string> values = data.Fields.ToDictionary(
            field => field.Name,
            field => field.Values.Count == 0 ? string.Empty : field.Values[0],
            StringComparer.Ordinal);
        PdfFormDataSet effectiveData = ApplyBehaviors(info, template, data, values);

        var editor = new PdfIncrementalPageEditor(document).RemoveXfa();
        var authored = new PdfDocumentBuilder();
        for (int page = 0; page < editor.PageCount; page++) authored.AddBlankPage();
        bool hasAuthoredFields = false;
        if (info.FormType == PdfXfaFormType.Dynamic)
        {
            PdfPageContent firstPage = new PdfPageContentReader(document).Read(0);
            PdfXfaFlowLayoutPlan flow = PdfXfaFlowLayout.Plan(
                info, effectiveData, firstPage.Width, firstPage.Height, 0);
            while (editor.PageCount < flow.PageCount)
                editor.AddBlankPage(firstPage.Width, firstPage.Height);
            PdfDocument expanded = PdfDocument.Open(editor.Build());
            editor = new PdfIncrementalPageEditor(expanded);
            while (authored.PageCount < editor.PageCount) authored.AddBlankPage();
            foreach (PdfXfaFlowFieldPlacement placement in flow.Placements)
            {
                PdfXfaTemplateField[] definitions = fields[placement.FieldPath];
                PdfXfaTemplateField field = definitions[placement.TemplateOccurrenceIndex];
                string name = placement.FieldPath
                    + (definitions.Length > 1 ? "[" + placement.TemplateOccurrenceIndex + "]" : "")
                    + "[" + placement.OccurrenceIndex + "]";
                double bottom = firstPage.Height - placement.Y - placement.Height;
                if (field.ControlType!.Equals("imageEdit", StringComparison.OrdinalIgnoreCase))
                {
                    AddImage(editor, field, images, placement.PageIndex,
                        firstPage.Width, firstPage.Height, placement.X, bottom,
                        placement.Width, placement.Height);
                    continue;
                }
                if (field.ControlType.Equals("barcode", StringComparison.OrdinalIgnoreCase))
                {
                    AddBarcode(editor, field, placement.PageIndex,
                        firstPage.Width, firstPage.Height, placement.X, bottom,
                        placement.Width, placement.Height, placement.Value);
                    continue;
                }
                AddField(authored, field, name, placement.PageIndex, placement.X,
                    bottom,
                    placement.Width, placement.Height, placement.Value);
                hasAuthoredFields = true;
            }
            return Finish(editor, mode, hasAuthoredFields ? authored : null);
        }

        PdfXfaStaticLayoutPlan layout = PdfXfaStaticLayout.Plan(info);
        if (layout.UnsupportedFlowedFieldPaths.Count != 0)
            throw new NotSupportedException("Flowed XFA fields require a dynamic form declaration.");
        var pageSizes = new Dictionary<int, PdfPageContent>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfXfaFieldPlacement placement in layout.Placements)
        {
            if (placement.PageIndex >= editor.PageCount)
                throw new InvalidOperationException("An XFA field targets a page outside the document.");
            int occurrence = occurrences.GetValueOrDefault(placement.FieldPath);
            occurrences[placement.FieldPath] = occurrence + 1;
            PdfXfaTemplateField[] definitions = fields[placement.FieldPath];
            PdfXfaTemplateField field = definitions[occurrence];
            string name = definitions.Length == 1 ? placement.FieldPath
                : placement.FieldPath + "[" + occurrence + "]";
            if (!names.Add(name))
                throw new NotSupportedException($"XFA conversion produces a duplicate field name '{name}'.");
            string dataName = BindingName(field.Binding) ?? placement.FieldPath;
            values.TryGetValue(dataName, out string? value);
            if (!pageSizes.TryGetValue(placement.PageIndex, out PdfPageContent? page))
            {
                page = new PdfPageContentReader(document).Read(placement.PageIndex);
                pageSizes.Add(placement.PageIndex, page);
            }
            double bottom = page.Height - placement.Y - placement.Height;
            if (bottom < 0 || placement.X + placement.Width > page.Width)
                throw new InvalidOperationException(
                    $"XFA field '{placement.FieldPath}' lies outside its page.");
            if (field.ControlType!.Equals("imageEdit", StringComparison.OrdinalIgnoreCase))
            {
                AddImage(editor, field, images, placement.PageIndex, page.Width, page.Height,
                    placement.X, bottom, placement.Width, placement.Height);
                continue;
            }
            if (field.ControlType.Equals("barcode", StringComparison.OrdinalIgnoreCase))
            {
                AddBarcode(editor, field, placement.PageIndex, page.Width, page.Height,
                    placement.X, bottom, placement.Width, placement.Height, value);
                continue;
            }
            AddField(authored, field, name, placement.PageIndex,
                placement.X, bottom, placement.Width, placement.Height, value);
            hasAuthoredFields = true;
        }
        return Finish(editor, mode, hasAuthoredFields ? authored : null);
    }

    internal static PdfXfaInfo ExpandCombinedXdp(PdfXfaInfo info)
    {
        if (info.IsPacketArray) return info;
        PdfXfaPacket packet = info.Packets.Count == 1 ? info.Packets[0]
            : throw new InvalidOperationException("Combined XDP data must contain one packet.");
        using var input = new MemoryStream(packet.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XElement root = document.Root
            ?? throw new InvalidOperationException("The combined XDP stream has no root element.");
        XElement[] elements = [.. root.Elements()];
        if (elements.Length == 0)
            throw new InvalidOperationException("The combined XDP stream has no packets.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PdfXfaPacket[] packets = [.. elements.Select(element =>
        {
            string name = element.Name.LocalName;
            if (!names.Add(name))
                throw new InvalidOperationException(
                    $"The combined XDP stream contains duplicate '{name}' packets.");
            return new PdfXfaPacket(name,
                Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)));
        })];
        XElement? dynamicRender = elements.FirstOrDefault(element =>
                element.Name.LocalName.Equals("config", StringComparison.OrdinalIgnoreCase))
            ?.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("dynamicRender", StringComparison.OrdinalIgnoreCase));
        PdfXfaFormType formType = dynamicRender is not null
            && dynamicRender.Value.Trim().Equals("required", StringComparison.OrdinalIgnoreCase)
                ? PdfXfaFormType.Dynamic : PdfXfaFormType.Static;
        return info with
        {
            IsPacketArray = true,
            Packets = Array.AsReadOnly(packets),
            FormType = formType
        };
    }

    private static PdfFormDataSet ApplyBehaviors(PdfXfaInfo info, PdfXfaTemplateInfo template,
        PdfFormDataSet data, Dictionary<string, string> values)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (template.Behaviors.Any(behavior =>
                behavior.Kind == PdfXfaTemplateBehaviorKind.Calculate))
        {
            foreach (PdfXfaCalculationResult calculation in
                     PdfXfaCalculationEngine.Evaluate(info, data).Where(result =>
                         result.Status == PdfXfaCalculationStatus.Evaluated))
            {
                values[calculation.FieldPath] = calculation.Value!.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                changed.Add(calculation.FieldPath);
            }
        }
        if (template.Behaviors.Any(behavior =>
                behavior.Kind == PdfXfaTemplateBehaviorKind.Format))
        {
            var formattingData = new PdfFormDataSet
            {
                Fields = Array.AsReadOnly(values.Select(value => new PdfFormDataField
                {
                    Name = value.Key,
                    Values = [value.Value]
                }).ToArray())
            };
            foreach (PdfXfaFormatResult format in PdfXfaFormatter.Format(info, formattingData)
                         .Where(result => result.Status == PdfXfaFormatStatus.Formatted))
            {
                values[format.FieldPath] = format.Value!;
                changed.Add(format.FieldPath);
            }
        }
        var existing = data.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        return new PdfFormDataSet
        {
            Fields = Array.AsReadOnly(data.Fields.Select(field => changed.Contains(field.Name)
                    ? new PdfFormDataField { Name = field.Name, Values = [values[field.Name]] }
                    : field)
                .Concat(changed.Where(name => !existing.Contains(name)).Select(name =>
                    new PdfFormDataField { Name = name, Values = [values[name]] })).ToArray()),
            ContainsJavaScript = data.ContainsJavaScript
        };
    }

    private static void AddImage(PdfIncrementalPageEditor editor, PdfXfaTemplateField field,
        Dictionary<string, PdfXfaImageValue> images, int pageIndex,
        double pageWidth, double pageHeight, double x, double bottom, double width, double height)
    {
        if (!images.TryGetValue(field.Path, out PdfXfaImageValue? image) || image.Data.IsEmpty)
            throw new NotSupportedException(
                $"XFA image field '{field.Path}' has no embedded image value.");
        if (image.IsExternal)
            throw new NotSupportedException(
                $"XFA image field '{field.Path}' requires an external resource.");
        if (!string.Equals(image.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"XFA image field '{field.Path}' does not contain a supported JPEG image.");
        var content = new PdfContentStreamBuilder()
            .DrawImage(PdfImage.FromJpeg(image.Data), x, bottom, width, height);
        editor.AppendPageContent(pageIndex, pageWidth, pageHeight, content);
    }

    private static void AddBarcode(PdfIncrementalPageEditor editor,
        PdfXfaTemplateField field, int pageIndex, double pageWidth, double pageHeight,
        double x, double bottom, double width, double height, string? value)
    {
        PdfXfaBarcodeInfo barcode = field.Barcode
            ?? throw new NotSupportedException(
                $"XFA barcode field '{field.Path}' has no barcode parameters.");
        string type = barcode.Type?.Trim() ?? string.Empty;
        if (!type.Equals("code39", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("code3of9", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"XFA barcode type '{barcode.Type ?? "unspecified"}' cannot be converted.");
        string data = (value ?? string.Empty).ToUpperInvariant();
        if (data.Length == 0 || data.Any(character => !Code39Patterns.ContainsKey(character)))
            throw new InvalidOperationException(
                $"XFA barcode field '{field.Path}' contains invalid Code 39 data.");
        if (barcode.Attributes.TryGetValue("dataLength", out string? lengthText))
        {
            if (!int.TryParse(lengthText, out int length) || length < 0)
                throw new InvalidOperationException(
                    $"XFA barcode field '{field.Path}' has an invalid data length.");
            if (data.Length > length)
                throw new InvalidOperationException(
                    $"XFA barcode field '{field.Path}' exceeds its declared data length.");
        }
        if (barcode.Attributes.TryGetValue("checksum", out string? checksum)
            && !string.IsNullOrWhiteSpace(checksum)
            && !checksum.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!checksum.Equals("1mod43", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"XFA barcode checksum '{checksum}' cannot be converted.");
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
            data += alphabet[data.Sum(character => alphabet.IndexOf(character)) % 43];
        }
        string encoded = "*" + data + "*";
        int units = encoded.Length - 1 + 20;
        foreach (char character in encoded)
        {
            foreach (char mark in Code39Patterns[character])
                units = checked(units + (mark == 'w' ? 3 : 1));
        }
        double module = width / units;
        double cursor = x + 10 * module;
        var content = new PdfContentStreamBuilder().SetFillRgb(0, 0, 0);
        foreach (char character in encoded)
        {
            string pattern = Code39Patterns[character];
            for (int index = 0; index < pattern.Length; index++)
            {
                double span = module * (pattern[index] == 'w' ? 3 : 1);
                if (index % 2 == 0) content.Rectangle(cursor, bottom, span, height).Fill();
                cursor += span;
            }
            cursor += module;
        }
        editor.AppendPageContent(pageIndex, pageWidth, pageHeight, content);
    }

    private static readonly Dictionary<char, string> Code39Patterns =
        new()
        {
            ['0'] = "nnnwwnwnn", ['1'] = "wnnwnnnnw", ['2'] = "nnwwnnnnw",
            ['3'] = "wnwwnnnnn", ['4'] = "nnnwwnnnw", ['5'] = "wnnwwnnnn",
            ['6'] = "nnwwwnnnn", ['7'] = "nnnwnnwnw", ['8'] = "wnnwnnwnn",
            ['9'] = "nnwwnnwnn", ['A'] = "wnnnnwnnw", ['B'] = "nnwnnwnnw",
            ['C'] = "wnwnnwnnn", ['D'] = "nnnnwwnnw", ['E'] = "wnnnwwnnn",
            ['F'] = "nnwnwwnnn", ['G'] = "nnnnnwwnw", ['H'] = "wnnnnwwnn",
            ['I'] = "nnwnnwwnn", ['J'] = "nnnnwwwnn", ['K'] = "wnnnnnnww",
            ['L'] = "nnwnnnnww", ['M'] = "wnwnnnnwn", ['N'] = "nnnnwnnww",
            ['O'] = "wnnnwnnwn", ['P'] = "nnwnwnnwn", ['Q'] = "nnnnnnwww",
            ['R'] = "wnnnnnwwn", ['S'] = "nnwnnnwwn", ['T'] = "nnnnwnwwn",
            ['U'] = "wwnnnnnnw", ['V'] = "nwwnnnnnw", ['W'] = "wwwnnnnnn",
            ['X'] = "nwnnwnnnw", ['Y'] = "wwnnwnnnn", ['Z'] = "nwwnwnnnn",
            ['-'] = "nwnnnnwnw", ['.'] = "wwnnnnwnn", [' '] = "nwwnnnwnn",
            ['$'] = "nwnwnwnnn", ['/'] = "nwnwnnnwn", ['+'] = "nwnnnwnwn",
            ['%'] = "nnnwnwnwn", ['*'] = "nwnnwnwnn"
        };

    private static byte[] Finish(PdfIncrementalPageEditor editor, PdfXfaConversionMode mode, PdfDocumentBuilder? authored)
    {
        if (authored is not null) editor.AddAuthoredFields(PdfDocument.Open(authored.Build()));
        byte[] converted = editor.Build();
        return mode == PdfXfaConversionMode.Flattened
            ? PdfFormFlattener.Flatten(PdfDocument.Open(converted)) : converted;
    }

    private static void AddField(PdfDocumentBuilder builder,
        PdfXfaTemplateField field, string name, int pageIndex,
        double x, double bottom, double width, double height, string? value)
    {
        var metadata = new PdfFormFieldMetadata
        {
            Tooltip = field.Description,
            MappingName = field.Path
        };
        double fontSize = field.Appearance.FontSize ?? 12;
        PdfTextFieldAlignment alignment = field.Appearance.Alignment ?? PdfTextFieldAlignment.Left;
        var style = new PdfFormFieldAppearanceStyle
        {
            BackgroundColor = field.Appearance.BackgroundColor ?? new PdfRgbColor(1, 1, 1),
            BorderColor = field.Appearance.BorderColor ?? new PdfRgbColor(0, 0, 0),
            TextColor = field.Appearance.TextColor ?? new PdfRgbColor(0, 0, 0)
        };
        if (TextControls.Contains(field.ControlType!))
            builder.AddTextField(pageIndex, name, x, bottom, width, height,
                value ?? string.Empty, fontSize,
                options: new PdfTextFieldOptions { Alignment = alignment },
                fieldMetadata: metadata, appearanceStyle: style);
        else if (field.ControlType!.Equals("checkButton", StringComparison.OrdinalIgnoreCase))
            builder.AddCheckBox(pageIndex, name, x, bottom, width, height,
                Checked(value), fieldMetadata: metadata, appearanceStyle: style);
        else if (field.ControlType.Equals("choiceList", StringComparison.OrdinalIgnoreCase))
        {
            if (field.ChoiceOptions.Count == 0)
                throw new NotSupportedException($"XFA choice field '{field.Path}' has no options.");
            builder.AddComboBoxOptions(pageIndex, name, x, bottom, width, height,
                field.ChoiceOptions.Select(option => new PdfChoiceOption(
                    option.ExportValue, option.DisplayValue)), value,
                fontSize: fontSize, fieldMetadata: metadata,
                choiceOptions: new PdfChoiceFieldOptions
                {
                    Alignment = alignment,
                    AppearanceStyle = style
                });
        }
        else
        {
            if (!string.IsNullOrEmpty(value))
                throw new NotSupportedException(
                    $"Signed XFA field '{field.Path}' cannot be converted safely.");
            builder.AddSignatureField(pageIndex, name, x, bottom, width, height,
                fieldMetadata: metadata, fontSize: fontSize,
                appearanceStyle: style, appearanceAlignment: alignment);
        }
    }

    private static string? BindingName(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding) || binding == "none") return null;
        const string record = "$record.";
        return binding.StartsWith(record, StringComparison.Ordinal)
            ? binding[record.Length..] : binding;
    }

    private static bool Checked(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "0" or "false" or "off" or "no" => false,
        "1" or "true" or "on" or "yes" => true,
        _ => throw new InvalidOperationException(
            "An XFA check-button value is not a recognized boolean state.")
    };
}

/// <summary>The standard-PDF output produced by XFA conversion.</summary>
public enum PdfXfaConversionMode
{
    /// <summary>Keep converted fields editable as AcroForm widgets.</summary>
    Editable,
    /// <summary>Paint widget appearances into page content and remove the fields.</summary>
    Flattened
}
