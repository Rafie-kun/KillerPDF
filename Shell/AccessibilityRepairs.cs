using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPDF;

public partial class MainWindow
{
    private void OpenAccessibilityTools() => OpenExchangeCommands("Str_DocumentTools_Accessibility",
        ("Str_DocumentTools_Accessibility", () => ShowDocumentReport(Loc("Str_DocumentTools_Accessibility"),
            path => Services.PdfEngineIntegration.InspectAccessibility(path).ToText())),
        ("Str_Repair_Language", SetPreflightLanguage),
        ("Str_Repair_Descriptions", RepairAccessibilityDescriptions),
        ("Str_Repair_ReadingOrder", RepairReadingOrder),
        ("Str_Repair_TableHeader", RepairTableHeader));

    private void WithRepairDocument(Action<PdfDocument> action)
    {
        if (_doc is null || !EnsureEditable()) return;
        string? snapshot = null;
        try
        {
            snapshot = CreateExchangeSnapshot();
            action(PdfDocument.Open(File.ReadAllBytes(snapshot)));
        }
        catch (Exception error) { ShowExchangeError(error); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }

    private int SelectRepairTarget(string titleKey, IEnumerable<string> labels)
    {
        string[] items = labels.ToArray();
        if (items.Length == 0) { KillerDialog.Show(this, Loc("Str_Repair_NoChanges")); return -1; }
        var dialog = new DocumentListDialog(this, Loc(titleKey), items);
        return dialog.ShowDialog() == true ? dialog.SelectedIndex : -1;
    }

    private string RepairPageLabel(int page) => Loc("Str_AutoBookmarks_Page") + " " + (page + 1);

    private void RepairAccessibilityDescriptions() => WithRepairDocument(document =>
    {
        var targets = new List<(string Label, Func<PdfDocument, string, byte[]> Apply)>();
        int pages = PdfDocumentInformation.Read(document).PageCount;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 0; page < pages; page++)
        {
            foreach (var widget in PdfFormWidgetReader.ReadPage(document, page))
            {
                if (!string.IsNullOrWhiteSpace(widget.Tooltip) || string.IsNullOrWhiteSpace(widget.FieldName)
                    || !fields.Add(widget.FieldName)) continue;
                string name = widget.FieldName;
                targets.Add((RepairPageLabel(page) + ": " + name, (doc, description) =>
                    PdfAccessibilityRepair.ApplyFormFieldDescription(doc,
                        PdfAccessibilityRepair.PreviewFormFieldDescription(doc, name, description)).Document.ToArray()));
            }
            foreach (var link in PdfLinkReader.ReadPage(document, page))
            {
                if (!string.IsNullOrWhiteSpace(link.Description)) continue;
                int pageIndex = page, annotation = link.AnnotationIndex;
                targets.Add((RepairPageLabel(page) + ": " + (link.Uri ?? link.NamedDestination
                    ?? (Loc("Str_Repair_Link") + " " + (annotation + 1))), (doc, description) =>
                    PdfAccessibilityRepair.ApplyLinkDescription(doc,
                        PdfAccessibilityRepair.PreviewLinkDescription(doc, pageIndex, annotation, description)).Document.ToArray()));
            }
        }
        int figure = 0;
        foreach (var finding in PdfAccessibilityInspector.Inspect(document).Findings.Where(item =>
            item.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription && item.ObjectNumber.HasValue))
        {
            int number = finding.ObjectNumber!.Value;
            string label = Loc("Str_Repair_Figure") + " " + (++figure);
            if (finding.PageIndex is int page) label = RepairPageLabel(page) + ": " + label;
            targets.Add((label, (doc, description) => PdfAccessibilityRepair.ApplyFigureAlternateDescription(doc,
                PdfAccessibilityRepair.PreviewFigureAlternateDescription(doc, number, description)).Document.ToArray()));
        }
        int selected = SelectRepairTarget("Str_Repair_Descriptions", targets.Select(item => item.Label));
        if (selected < 0) return;
        var target = targets[selected];
        var input = new DocumentValuesDialog(this, target.Label, (Loc("Str_Attachment_Description"), ""));
        if (input.ShowDialog() != true) return;
        ReviewDocumentRepair(target.Label + ": " + input.Values[0],
            doc => target.Apply(doc, input.Values[0]), accessibility: true);
    });

    private sealed record RepairStructure(int Number, string Role, string Label, int? Table, int[] Children);

    private List<RepairStructure> ReadRepairStructure(PdfDocument document)
    {
        var nodes = new List<RepairStructure>();
        var seen = new HashSet<int>();
        if (!document.Trailer.TryGetValue(new PdfName("Root"u8), out var catalogValue)) return nodes;
        var catalog = catalogValue is PdfIndirectReference catalogReference
            ? document.Resolve(catalogReference) as PdfDictionary : catalogValue as PdfDictionary;
        if (catalog is null || !catalog.TryGetValue(new PdfName("StructTreeRoot"u8), out var root)) return nodes;
        var readingOrder = PdfAccessibilityReadingOrder.Read(document).Items;
        var pageText = new Dictionary<int, IReadOnlyList<KillerPdf.Engine.Documents.PdfExtractedLetter>>();
        Visit(root, null, 0);
        return nodes;

        void Visit(PdfObject value, int? table, int depth)
        {
            if (depth >= 256) return;
            var reference = value as PdfIndirectReference;
            if (reference is not null)
            {
                if (reference.Generation != 0 || !seen.Add(reference.ObjectNumber)) return;
                value = document.Resolve(reference);
            }
            if (value is PdfArray array)
            {
                foreach (var child in array) Visit(child, table, depth + 1);
                return;
            }
            if (value is not PdfDictionary dictionary) return;
            dictionary.TryGetValue(new PdfName("S"u8), out var roleValue);
            string role = (roleValue as PdfName)?.ValueAsLatin1() ?? "Document";
            if (role == "Table" && reference is not null) table = reference.ObjectNumber;
            dictionary.TryGetValue(new PdfName("K"u8), out var children);
            if (reference is not null)
            {
                string label = role + " " + (nodes.Count + 1);
                var content = readingOrder.Where(item => item.StructureObjectNumber == reference.ObjectNumber).ToArray();
                if (content.Length > 0)
                {
                    label = RepairPageLabel(content[0].PageIndex) + ": " + label;
                    string excerpt = string.Concat(content.Select(item =>
                    {
                        if (!string.IsNullOrWhiteSpace(item.ActualText)) return item.ActualText;
                        if (item.MarkedContentId is not int mcid) return "";
                        if (!pageText.TryGetValue(item.PageIndex, out var letters))
                            pageText[item.PageIndex] = letters = new PdfPageContentReader(document)
                                .Read(item.PageIndex).Letters;
                        return string.Concat(letters.Where(letter => letter.MarkedContentId == mcid).Select(letter => letter.Value));
                    }));
                    if (!string.IsNullOrWhiteSpace(excerpt)) label += ": " + excerpt[..Math.Min(160, excerpt.Length)];
                }
                foreach (string key in new[] { "T", "ActualText", "Alt" })
                    if (dictionary.TryGetValue(new PdfName(System.Text.Encoding.ASCII.GetBytes(key)), out var text)
                        && text is PdfString pdfText)
                    {
                        var bytes = pdfText.Bytes.Span;
                        string decoded = bytes.StartsWith(new byte[] { 0xfe, 0xff })
                            ? System.Text.Encoding.BigEndianUnicode.GetString(bytes[2..])
                            : System.Text.Encoding.Latin1.GetString(bytes);
                        if (!string.IsNullOrWhiteSpace(decoded)) { label += ": " + decoded; break; }
                    }
                int[] childNumbers = children is PdfArray direct && direct.All(item =>
                    item is PdfIndirectReference { Generation: 0 })
                    ? direct.Cast<PdfIndirectReference>().Select(item => item.ObjectNumber).ToArray() : [];
                nodes.Add(new(reference.ObjectNumber, role, label, table, childNumbers));
            }
            if (children is not null) Visit(children, table, depth + 1);
        }
    }

    private void RepairReadingOrder() => WithRepairDocument(document =>
    {
        var nodes = ReadRepairStructure(document);
        var containers = nodes.Where(item => item.Children.Length > 1
            && item.Children.Distinct().Count() == item.Children.Length
            && item.Children.All(number => nodes.Any(node => node.Number == number))).ToArray();
        int selected = SelectRepairTarget("Str_Repair_ReadingOrder", containers.Select(item => item.Label));
        if (selected < 0) return;
        var parent = containers[selected];
        var reorder = new DocumentListDialog(this, Loc("Str_Repair_ReadingOrder"), parent.Children.Select(number =>
            nodes.First(item => item.Number == number).Label), reorder: true);
        if (reorder.ShowDialog() != true || reorder.Order.SequenceEqual(Enumerable.Range(0, parent.Children.Length))) return;
        int[] order = reorder.Order.Select(index => parent.Children[index]).ToArray();
        string description = Loc("Str_Repair_ReadingOrder") + ": " + parent.Label + "\n"
            + string.Join("\n", order.Select((number, index) => (index + 1) + ". " + nodes.First(item => item.Number == number).Label));
        ReviewDocumentRepair(description, doc =>
            PdfAccessibilityRepair.ApplyReadingOrder(doc,
                PdfAccessibilityRepair.PreviewReadingOrder(doc, parent.Number, order)).Document.ToArray(), accessibility: true);
    });

    private void RepairTableHeader() => WithRepairDocument(document =>
    {
        var missing = PdfAccessibilityInspector.Inspect(document).Findings.Where(item =>
            item.Code == PdfAccessibilityFindingCode.MissingTableHeader).Select(item => item.ObjectNumber).ToHashSet();
        var cells = ReadRepairStructure(document).Where(item => item.Role == "TD" && item.Table.HasValue
            && missing.Contains(item.Table)).ToArray();
        int selected = SelectRepairTarget("Str_Repair_TableHeader", cells.Select(item => item.Label));
        if (selected < 0) return;
        var cell = cells[selected];
        var scopes = new[] { PdfAccessibilityTableHeaderScope.Row, PdfAccessibilityTableHeaderScope.Column,
            PdfAccessibilityTableHeaderScope.Both };
        int scope = SelectRepairTarget("Str_Repair_TableHeader", new[] { Loc("Str_Repair_Row"),
            Loc("Str_Repair_Column"), Loc("Str_Repair_Both") });
        if (scope < 0) return;
        string scopeLabel = Loc(new[] { "Str_Repair_Row", "Str_Repair_Column", "Str_Repair_Both" }[scope]);
        ReviewDocumentRepair(Loc("Str_Repair_TableHeader") + ": " + cell.Label + " (" + scopeLabel + ")", doc =>
            PdfAccessibilityRepair.ApplyTableHeader(doc, PdfAccessibilityRepair.PreviewTableHeader(doc,
                cell.Table!.Value, cell.Number, scopes[scope])).Document.ToArray(), accessibility: true);
    });
}
