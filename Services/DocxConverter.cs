using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;

namespace KillerPDF.Services;

/// <summary>
/// Document to PDF (#74): reads a Word .docx package into sections of paragraphs, lists, tables,
/// pictures, headers, footers and notes, then lays it out with installed fonts. Elements are
/// matched by local name so strict and transitional packages read the same way. Floating
/// objects are placed in the text flow and multiple columns flow as one.
/// </summary>
internal static class DocxConverter
{
    internal static byte[] Convert(byte[] package)
    {
        using var archive = OfficePackage.Open(package);
        var reader = new Reader(archive);
        IReadOnlyList<OfficeSection> sections = reader.Read();
        var layout = new OfficeFlowLayout(new OfficeFonts(), reader.DefaultTab);
        return OfficeDraw.Write(layout.Layout(sections));
    }

    // ── Properties ───────────────────────────────────────────────────────────────────────

    private sealed class RunProps
    {
        internal string? Font;
        internal double? Size;
        internal bool? Bold, Italic, Underline, Strike, Caps, Hidden;
        internal OfficeColor? Color;
        internal OfficeColor? Highlight;
        internal OfficeVerticalPosition? Position;
        internal bool ColorAuto;

        internal RunProps Clone() => (RunProps)MemberwiseClone();

        internal void Apply(XElement? rPr, OfficeTheme theme)
        {
            if (rPr is null) return;
            foreach (XElement element in rPr.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "rFonts":
                        string? font = Attr(element, "ascii") ?? Attr(element, "hAnsi");
                        string? themeFont = Attr(element, "asciiTheme") ?? Attr(element, "hAnsiTheme");
                        if (themeFont is not null)
                            font = themeFont.StartsWith("major", StringComparison.OrdinalIgnoreCase) ? theme.MajorFont : theme.MinorFont;
                        if (!string.IsNullOrWhiteSpace(font)) Font = font;
                        break;
                    case "sz":
                        if (Number(Attr(element, "val")) is double halfPoints && halfPoints > 0) Size = halfPoints / 2;
                        break;
                    case "b": Bold = Toggle(element); break;
                    case "i": Italic = Toggle(element); break;
                    case "strike":
                    case "dstrike":
                        Strike = Toggle(element);
                        break;
                    case "caps":
                    case "smallCaps":
                        Caps = Toggle(element);
                        break;
                    case "vanish":
                    case "specVanish":
                        Hidden = Toggle(element);
                        break;
                    case "u":
                        Underline = Attr(element, "val") is not "none";
                        break;
                    case "color":
                        string? value = Attr(element, "val");
                        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
                        {
                            Color = null;
                            ColorAuto = true;
                        }
                        else if (OfficeColor.Parse(value) is OfficeColor color)
                        {
                            Color = color;
                            ColorAuto = false;
                        }
                        break;
                    case "highlight":
                        Highlight = HighlightColor(Attr(element, "val"));
                        break;
                    case "shd":
                        if (OfficeColor.Parse(Attr(element, "fill")) is OfficeColor fill) Highlight = fill;
                        break;
                    case "vertAlign":
                        Position = Attr(element, "val") switch
                        {
                            "superscript" => OfficeVerticalPosition.Superscript,
                            "subscript" => OfficeVerticalPosition.Subscript,
                            _ => OfficeVerticalPosition.Baseline
                        };
                        break;
                }
            }
        }

        internal OfficeRunStyle ToStyle() => new(
            Font ?? "Times New Roman", Size ?? 10, Bold == true, Italic == true, Underline == true, Strike == true,
            Color ?? OfficeColor.Black, Highlight, Position ?? OfficeVerticalPosition.Baseline);
    }

    private sealed class ParaProps
    {
        internal OfficeAlignment? Alignment;
        internal double? IndentLeft, IndentRight, FirstLine;
        internal double? Before, After, Line;
        internal OfficeLineRule? Rule;
        internal bool? PageBreakBefore, Contextual;
        internal int? NumId, Level;
        internal OfficeColor? Shading;
        internal List<OfficeTabStop> Tabs = [];

        internal ParaProps Clone()
        {
            var copy = (ParaProps)MemberwiseClone();
            copy.Tabs = [.. Tabs];
            return copy;
        }

        internal void Apply(XElement? pPr)
        {
            if (pPr is null) return;
            foreach (XElement element in pPr.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "jc":
                        Alignment = Attr(element, "val") switch
                        {
                            "center" => OfficeAlignment.Center,
                            "right" or "end" => OfficeAlignment.Right,
                            "both" or "distribute" or "mediumKashida" or "highKashida" or "lowKashida" => OfficeAlignment.Justify,
                            _ => OfficeAlignment.Left
                        };
                        break;
                    case "ind":
                        if (Twips(Attr(element, "left") ?? Attr(element, "start")) is double left) IndentLeft = left;
                        if (Twips(Attr(element, "right") ?? Attr(element, "end")) is double right) IndentRight = right;
                        if (Twips(Attr(element, "firstLine")) is double first) FirstLine = first;
                        if (Twips(Attr(element, "hanging")) is double hanging) FirstLine = -hanging;
                        break;
                    case "spacing":
                        if (Twips(Attr(element, "before")) is double before) Before = before;
                        if (Twips(Attr(element, "after")) is double after) After = after;
                        if (Toggle(element, "beforeAutospacing")) Before = 14;
                        if (Toggle(element, "afterAutospacing")) After = 14;
                        if (Number(Attr(element, "line")) is double line)
                        {
                            switch (Attr(element, "lineRule"))
                            {
                                case "exact": Rule = OfficeLineRule.Exact; Line = line / 20; break;
                                case "atLeast": Rule = OfficeLineRule.AtLeast; Line = line / 20; break;
                                default: Rule = OfficeLineRule.Multiple; Line = line / 240; break;
                            }
                        }
                        break;
                    case "pageBreakBefore": PageBreakBefore = Toggle(element); break;
                    case "contextualSpacing": Contextual = Toggle(element); break;
                    case "numPr":
                        XElement? numId = Child(element, "numId");
                        XElement? level = Child(element, "ilvl");
                        if (numId is not null && int.TryParse(Attr(numId, "val"), out int id)) NumId = id;
                        if (level is not null && int.TryParse(Attr(level, "val"), out int ilvl)) Level = ilvl;
                        break;
                    case "shd":
                        Shading = OfficeColor.Parse(Attr(element, "fill"));
                        break;
                    case "tabs":
                        foreach (XElement tab in element.Elements().Where(child => child.Name.LocalName == "tab"))
                        {
                            if (Twips(Attr(tab, "pos")) is not double position) continue;
                            string kind = Attr(tab, "val") ?? "left";
                            Tabs.RemoveAll(stop => Math.Abs(stop.Position - position) < 0.5);
                            if (kind == "clear") continue;
                            OfficeTabKind tabKind = kind switch
                            {
                                "center" => OfficeTabKind.Center,
                                "right" or "end" => OfficeTabKind.Right,
                                "decimal" => OfficeTabKind.Decimal,
                                _ => OfficeTabKind.Left
                            };
                            if (kind == "bar") continue;
                            char leader = Attr(tab, "leader") switch
                            {
                                "dot" or "middleDot" => '.',
                                "hyphen" => '-',
                                "underscore" or "heavy" => '_',
                                _ => '\0'
                            };
                            Tabs.Add(new OfficeTabStop(position, tabKind, leader));
                        }
                        break;
                }
            }
        }
    }

    private sealed record StyleDef(string Id, string Type, string? BasedOn, XElement? PPr, XElement? RPr, XElement? TblPr, XElement? TcPr);

    private sealed class NumberingLevel
    {
        internal int Start = 1;
        internal string Format = "decimal";
        internal string Text = "%1.";
        internal XElement? PPr;
        internal XElement? RPr;
        internal string Suffix = "tab";
    }

    // ── Reader ───────────────────────────────────────────────────────────────────────────

    private sealed class Reader
    {
        private readonly ZipArchive _archive;
        private readonly OfficeTheme _theme;
        private readonly Dictionary<string, StyleDef> _styles = new(StringComparer.Ordinal);
        private readonly string? _defaultParagraphStyle;
        private readonly XElement? _defaultRPr, _defaultPPr;
        private readonly Dictionary<int, Dictionary<int, NumberingLevel>> _abstractLevels = [];
        private readonly Dictionary<int, (int Abstract, Dictionary<int, int> StartOverrides)> _nums = [];
        private readonly Dictionary<int, int[]> _counters = [];
        private readonly Dictionary<(string? Style, string? Table), (ParaProps Para, RunProps Run)> _resolved = [];
        private readonly Dictionary<string, PdfImage?> _images = new(StringComparer.Ordinal);
        private readonly List<(string Kind, string Id, int Number)> _notes = [];
        private readonly Stack<FieldFrame> _fields = new();
        private string _part = "word/document.xml";
        private Dictionary<string, string> _rels = [];
        private List<OfficeBlock>? _pending;

        internal double DefaultTab { get; } = 36;

        private sealed class FieldFrame
        {
            internal StringBuilder Instruction { get; } = new();
            internal bool InResult;
            internal bool Suppress;
        }

        internal Reader(ZipArchive archive)
        {
            _archive = archive;
            string main = OfficePackage.MainPart(archive, "word/document.xml");
            _part = main;
            _rels = OfficePackage.Relationships(archive, main);
            _theme = OfficeTheme.Read(PartByType(main, "theme"));

            XDocument? styles = PartByType(main, "styles");
            if (styles?.Root is XElement stylesRoot)
            {
                XElement? defaults = Child(stylesRoot, "docDefaults");
                _defaultRPr = Child(Child(defaults, "rPrDefault"), "rPr");
                _defaultPPr = Child(Child(defaults, "pPrDefault"), "pPr");
                foreach (XElement style in stylesRoot.Elements().Where(element => element.Name.LocalName == "style"))
                {
                    string? id = Attr(style, "styleId");
                    if (id is null) continue;
                    string type = Attr(style, "type") ?? "paragraph";
                    _styles[id] = new StyleDef(id, type, Attr(Child(style, "basedOn"), "val"),
                        Child(style, "pPr"), Child(style, "rPr"), Child(style, "tblPr"), Child(style, "tcPr"));
                    if (type == "paragraph" && Toggle(style, "default")) _defaultParagraphStyle = id;
                }
            }

            XDocument? numbering = PartByType(main, "numbering");
            if (numbering?.Root is XElement numberingRoot)
            {
                foreach (XElement abstractNum in numberingRoot.Elements().Where(element => element.Name.LocalName == "abstractNum"))
                {
                    if (!int.TryParse(Attr(abstractNum, "abstractNumId"), out int abstractId)) continue;
                    var levels = new Dictionary<int, NumberingLevel>();
                    foreach (XElement level in abstractNum.Elements().Where(element => element.Name.LocalName == "lvl"))
                        if (int.TryParse(Attr(level, "ilvl"), out int ilvl)) levels[ilvl] = ReadLevel(level);
                    _abstractLevels[abstractId] = levels;
                }
                foreach (XElement num in numberingRoot.Elements().Where(element => element.Name.LocalName == "num"))
                {
                    if (!int.TryParse(Attr(num, "numId"), out int numId)) continue;
                    if (!int.TryParse(Attr(Child(num, "abstractNumId"), "val"), out int abstractId)) continue;
                    var overrides = new Dictionary<int, int>();
                    foreach (XElement levelOverride in num.Elements().Where(element => element.Name.LocalName == "lvlOverride"))
                    {
                        if (!int.TryParse(Attr(levelOverride, "ilvl"), out int ilvl)) continue;
                        if (int.TryParse(Attr(Child(levelOverride, "startOverride"), "val"), out int start)) overrides[ilvl] = start;
                    }
                    _nums[numId] = (abstractId, overrides);
                }
            }

            XDocument? settings = PartByType(main, "settings");
            if (settings?.Root is XElement settingsRoot && Twips(Attr(Child(settingsRoot, "defaultTabStop"), "val")) is double tab && tab > 1)
                DefaultTab = tab;
        }

        private static NumberingLevel ReadLevel(XElement level)
        {
            var result = new NumberingLevel();
            if (int.TryParse(Attr(Child(level, "start"), "val"), out int start)) result.Start = start;
            result.Format = Attr(Child(level, "numFmt"), "val") ?? "decimal";
            result.Text = Attr(Child(level, "lvlText"), "val") ?? "";
            result.PPr = Child(level, "pPr");
            result.RPr = Child(level, "rPr");
            result.Suffix = Attr(Child(level, "suff"), "val") ?? "tab";
            return result;
        }

        private XDocument? PartByType(string source, string type)
        {
            Dictionary<string, string> rels = OfficePackage.RelationshipsByType(_archive, source);
            return rels.TryGetValue(type, out string? target) ? OfficePackage.Xml(_archive, target) : null;
        }

        internal IReadOnlyList<OfficeSection> Read()
        {
            XDocument document = OfficePackage.Xml(_archive, _part)
                ?? throw new InvalidDataException("The Word document part is missing.");
            XElement body = Child(document.Root, "body") ?? throw new InvalidDataException("The Word document has no body.");
            var sections = new List<OfficeSection>();
            var current = new OfficeSection();
            var mainRels = _rels;
            OfficeSection? previous = null;

            void EndSection(XElement? sectPr)
            {
                ApplySection(current, sectPr, previous);
                sections.Add(current);
                previous = current;
                current = new OfficeSection();
            }

            foreach (XElement element in Flatten(body.Elements()))
            {
                if (element.Name.LocalName == "sectPr") continue;
                AddBlock(current.Blocks, element, null);
                XElement? sectPr = element.Name.LocalName == "p" ? Child(Child(element, "pPr"), "sectPr") : null;
                if (sectPr is not null)
                {
                    _rels = mainRels;
                    EndSection(sectPr);
                }
            }
            _rels = mainRels;
            AddNotes(current.Blocks);
            EndSection(Child(body, "sectPr"));
            if (sections.Count > 1 && sections[^1].Blocks.Count == 0 && sections[^2].Blocks.Count > 0)
                sections.RemoveAt(sections.Count - 1);
            return sections;
        }

        // Content controls and custom XML wrap blocks; their content is the content.
        private static IEnumerable<XElement> Flatten(IEnumerable<XElement> elements)
        {
            foreach (XElement element in elements)
            {
                switch (element.Name.LocalName)
                {
                    case "sdt":
                        foreach (XElement inner in Flatten(Child(element, "sdtContent")?.Elements() ?? []))
                            yield return inner;
                        break;
                    case "customXml":
                    case "smartTag":
                        foreach (XElement inner in Flatten(element.Elements())) yield return inner;
                        break;
                    case "AlternateContent":
                        XElement? choice = Child(element, "Choice") ?? Child(element, "Fallback");
                        foreach (XElement inner in Flatten(choice?.Elements() ?? [])) yield return inner;
                        break;
                    default:
                        yield return element;
                        break;
                }
            }
        }

        private void AddBlock(List<OfficeBlock> blocks, XElement element, string? tableStyle)
        {
            switch (element.Name.LocalName)
            {
                case "p":
                    _pending = null;
                    blocks.Add(ReadParagraph(element, tableStyle));
                    if (_pending is { Count: > 0 }) blocks.AddRange(_pending);
                    _pending = null;
                    break;
                case "tbl":
                    blocks.Add(ReadTable(element));
                    break;
            }
        }

        private List<OfficeBlock> ReadBlocks(IEnumerable<XElement> elements, string? tableStyle)
        {
            var blocks = new List<OfficeBlock>();
            foreach (XElement element in Flatten(elements)) AddBlock(blocks, element, tableStyle);
            return blocks;
        }

        // ── Sections ─────────────────────────────────────────────────────────────────────

        private void ApplySection(OfficeSection section, XElement? sectPr, OfficeSection? previous)
        {
            var page = new OfficePageSetup();
            if (previous is not null)
            {
                section.Header = previous.Header;
                section.Footer = previous.Footer;
                section.FirstHeader = previous.FirstHeader;
                section.FirstFooter = previous.FirstFooter;
            }
            if (sectPr is not null)
            {
                XElement? size = Child(sectPr, "pgSz");
                if (Twips(Attr(size, "w")) is double width && width > 36) page.Width = width;
                if (Twips(Attr(size, "h")) is double height && height > 36) page.Height = height;
                XElement? margins = Child(sectPr, "pgMar");
                if (Twips(Attr(margins, "top")) is double top) page.MarginTop = Math.Abs(top);
                if (Twips(Attr(margins, "bottom")) is double bottom) page.MarginBottom = Math.Abs(bottom);
                if (Twips(Attr(margins, "left") ?? Attr(margins, "start")) is double left) page.MarginLeft = left;
                if (Twips(Attr(margins, "right") ?? Attr(margins, "end")) is double right) page.MarginRight = right;
                if (Twips(Attr(margins, "header")) is double headerDistance) page.HeaderDistance = headerDistance;
                if (Twips(Attr(margins, "footer")) is double footerDistance) page.FooterDistance = footerDistance;
                section.Continuous = Attr(Child(sectPr, "type"), "val") == "continuous";
                section.TitlePage = Child(sectPr, "titlePg") is XElement titlePage && Toggle(titlePage);
                foreach (XElement reference in sectPr.Elements())
                {
                    string name = reference.Name.LocalName;
                    if (name is not ("headerReference" or "footerReference")) continue;
                    string? id = Attr(reference, "id");
                    if (id is null || !_rels.TryGetValue(id, out string? target)) continue;
                    List<OfficeBlock> blocks = ReadHeaderFooter(target);
                    string type = Attr(reference, "type") ?? "default";
                    bool header = name == "headerReference";
                    if (type == "default")
                    {
                        if (header) section.Header = blocks;
                        else section.Footer = blocks;
                    }
                    else if (type == "first")
                    {
                        if (header) section.FirstHeader = blocks;
                        else section.FirstFooter = blocks;
                    }
                }
            }
            if (page.MarginLeft + page.MarginRight > page.Width - 36)
                page.MarginLeft = page.MarginRight = 36;
            if (page.MarginTop + page.MarginBottom > page.Height - 36)
                page.MarginTop = page.MarginBottom = 36;
            section.Page = page;
        }

        private List<OfficeBlock> ReadHeaderFooter(string part)
        {
            string savedPart = _part;
            Dictionary<string, string> savedRels = _rels;
            try
            {
                XDocument? document = OfficePackage.Xml(_archive, part);
                if (document?.Root is null) return [];
                _part = part;
                _rels = OfficePackage.Relationships(_archive, part);
                return ReadBlocks(document.Root.Elements(), null);
            }
            finally
            {
                _part = savedPart;
                _rels = savedRels;
            }
        }

        // Footnotes and endnotes follow the text, numbered in the order they were referenced.
        private void AddNotes(List<OfficeBlock> blocks)
        {
            if (_notes.Count == 0) return;
            Dictionary<string, string> types = OfficePackage.RelationshipsByType(_archive, _part);
            string savedPart = _part;
            Dictionary<string, string> savedRels = _rels;
            var notes = new Dictionary<string, (string Part, Dictionary<string, XElement> ById)>(StringComparer.Ordinal);
            foreach ((string kind, string relationship) in new[] { ("footnote", "footnotes"), ("endnote", "endnotes") })
            {
                if (!types.TryGetValue(relationship, out string? target)) continue;
                XDocument? document = OfficePackage.Xml(_archive, target);
                if (document?.Root is null) continue;
                var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (XElement note in document.Root.Elements())
                    if (Attr(note, "id") is string id) byId[id] = note;
                notes[kind] = (target, byId);
            }
            bool first = true;
            foreach ((string kind, string id, int number) in _notes.ToList())
            {
                if (!notes.TryGetValue(kind, out var source) || !source.ById.TryGetValue(id, out XElement? note)) continue;
                _part = source.Part;
                _rels = OfficePackage.Relationships(_archive, _part);
                List<OfficeBlock> noteBlocks = ReadBlocks(note.Elements(), null);
                if (noteBlocks.FirstOrDefault() is OfficeParagraph paragraph)
                {
                    OfficeRunStyle style = paragraph.MarkStyle with { Position = OfficeVerticalPosition.Superscript };
                    paragraph.Inlines.Insert(0, new OfficeText(number.ToString(CultureInfo.CurrentCulture) + " ", style));
                    if (first) paragraph.SpaceBefore = Math.Max(paragraph.SpaceBefore, 18);
                }
                first = false;
                blocks.AddRange(noteBlocks);
            }
            _part = savedPart;
            _rels = savedRels;
        }

        // ── Styles ───────────────────────────────────────────────────────────────────────

        private IEnumerable<StyleDef> Chain(string? id)
        {
            var chain = new List<StyleDef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (id is not null && seen.Add(id) && _styles.TryGetValue(id, out StyleDef? style))
            {
                chain.Add(style);
                id = style.BasedOn;
            }
            chain.Reverse();
            return chain;
        }

        private (ParaProps Para, RunProps Run) Resolve(string? styleId, string? tableStyle)
        {
            styleId ??= _defaultParagraphStyle;
            if (_resolved.TryGetValue((styleId, tableStyle), out var cached)) return (cached.Para.Clone(), cached.Run.Clone());
            var para = new ParaProps();
            var run = new RunProps();
            para.Apply(_defaultPPr);
            run.Apply(_defaultRPr, _theme);
            foreach (StyleDef style in Chain(tableStyle))
            {
                para.Apply(style.PPr);
                run.Apply(style.RPr, _theme);
            }
            foreach (StyleDef style in Chain(styleId))
            {
                para.Apply(style.PPr);
                run.Apply(style.RPr, _theme);
            }
            _resolved[(styleId, tableStyle)] = (para, run);
            return (para.Clone(), run.Clone());
        }

        // ── Paragraphs ───────────────────────────────────────────────────────────────────

        private OfficeParagraph ReadParagraph(XElement element, string? tableStyle)
        {
            XElement? pPr = Child(element, "pPr");
            string? styleId = Attr(Child(pPr, "pStyle"), "val");
            (ParaProps para, RunProps paraRun) = Resolve(styleId, tableStyle);
            var direct = new ParaProps();
            direct.Apply(pPr);
            int? numId = direct.NumId ?? para.NumId;
            int level = direct.Level ?? para.Level ?? 0;
            NumberingLevel? numbering = numId is int id && id != 0 ? Level(id, level) : null;
            if (numbering is not null) para.Apply(numbering.PPr);
            para.Apply(pPr);

            var markRun = paraRun.Clone();
            markRun.Apply(Child(pPr, "rPr"), _theme);
            var paragraph = new OfficeParagraph
            {
                StyleId = styleId ?? _defaultParagraphStyle,
                MarkStyle = markRun.ToStyle() with { Underline = false, Strike = false, Highlight = null },
                Alignment = para.Alignment ?? OfficeAlignment.Left,
                IndentLeft = para.IndentLeft ?? 0,
                IndentRight = para.IndentRight ?? 0,
                FirstLine = para.FirstLine ?? 0,
                SpaceBefore = para.Before ?? 0,
                SpaceAfter = para.After ?? 0,
                LineValue = para.Line ?? 1,
                LineRule = para.Rule ?? OfficeLineRule.Multiple,
                PageBreakBefore = para.PageBreakBefore == true,
                ContextualSpacing = para.Contextual == true,
                Shading = para.Shading
            };
            paragraph.Tabs.AddRange(para.Tabs);

            if (numbering is not null && numId is int listId)
            {
                string label = Label(listId, level, numbering);
                if (label.Length > 0)
                {
                    var labelRun = markRun.Clone();
                    labelRun.Underline = false;
                    labelRun.Highlight = null;
                    string? bulletFont = null;
                    if (numbering.RPr is not null)
                    {
                        var levelRun = new RunProps();
                        levelRun.Apply(numbering.RPr, _theme);
                        bulletFont = levelRun.Font;
                        labelRun.Apply(numbering.RPr, _theme);
                    }
                    if (numbering.Format == "bullet")
                    {
                        label = Bullet(label, bulletFont);
                        labelRun.Font = markRun.Font;
                    }
                    paragraph.Label = numbering.Suffix == "space" ? label + " " : label;
                    paragraph.LabelStyle = labelRun.ToStyle();
                    if (numbering.Suffix != "tab")
                    {
                        paragraph.Inlines.Add(new OfficeText(paragraph.Label, paragraph.LabelStyle));
                        paragraph.Label = null;
                    }
                }
            }

            CollectInlines(element.Elements(), paragraph, paraRun);
            return paragraph;
        }

        private NumberingLevel? Level(int numId, int level)
        {
            if (!_nums.TryGetValue(numId, out var num)) return null;
            if (!_abstractLevels.TryGetValue(num.Abstract, out var levels)) return null;
            return levels.TryGetValue(level, out NumberingLevel? found) ? found : null;
        }

        private string Label(int numId, int level, NumberingLevel numbering)
        {
            if (!_nums.TryGetValue(numId, out var num) || !_abstractLevels.TryGetValue(num.Abstract, out var levels))
                return "";
            if (!_counters.TryGetValue(numId, out int[]? counts))
                _counters[numId] = counts = new int[9];
            level = Math.Clamp(level, 0, 8);
            int Start(int ilvl) => num.StartOverrides.TryGetValue(ilvl, out int start) ? start
                : levels.TryGetValue(ilvl, out NumberingLevel? found) ? found.Start : 1;
            counts[level] = counts[level] == 0 ? Start(level) : counts[level] + 1;
            for (int deeper = level + 1; deeper < counts.Length; deeper++) counts[deeper] = 0;
            if (numbering.Format == "none") return "";
            string text = numbering.Text;
            var result = new StringBuilder();
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '%' && index + 1 < text.Length && char.IsAsciiDigit(text[index + 1]))
                {
                    int ilvl = Math.Clamp(text[index + 1] - '1', 0, 8);
                    int value = counts[ilvl] == 0 ? Start(ilvl) : counts[ilvl];
                    string format = levels.TryGetValue(ilvl, out NumberingLevel? other) ? other.Format : "decimal";
                    result.Append(FormatNumber(value, format));
                    index++;
                }
                else result.Append(text[index]);
            }
            return result.ToString();
        }

        private static string FormatNumber(int value, string format) => format switch
        {
            "upperRoman" => Roman(value).ToUpperInvariant(),
            "lowerRoman" => Roman(value),
            "upperLetter" => Letters(value).ToUpperInvariant(),
            "lowerLetter" => Letters(value),
            "decimalZero" => value.ToString("00", CultureInfo.InvariantCulture),
            "ordinal" => value.ToString(CultureInfo.InvariantCulture) + Ordinal(value),
            "bullet" => "",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };

        private static string Ordinal(int value) =>
            (value % 100) is 11 or 12 or 13 ? "th" : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

        private static string Roman(int value)
        {
            if (value <= 0 || value > 3999) return value.ToString(CultureInfo.InvariantCulture);
            (int, string)[] parts = [(1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
                (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")];
            var result = new StringBuilder();
            foreach ((int amount, string text) in parts)
                while (value >= amount) { result.Append(text); value -= amount; }
            return result.ToString();
        }

        private static string Letters(int value)
        {
            if (value <= 0) return value.ToString(CultureInfo.InvariantCulture);
            char letter = (char)('a' + (value - 1) % 26);
            return new string(letter, (value - 1) / 26 + 1);
        }

        // Symbol and Wingdings bullets live in the private use area; draw them as Unicode bullets.
        private static string Bullet(string label, string? font)
        {
            var result = new StringBuilder();
            foreach (char character in label)
            {
                int code = character >= 0xF000 && character <= 0xF0FF ? character - 0xF000 : character;
                bool symbolFont = font is not null && (font.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                    || font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase));
                if (character >= 0xE000 && character <= 0xF8FF || symbolFont)
                {
                    result.Append(code switch
                    {
                        0xB7 => '\u2022',
                        0xA7 => '\u25AA',
                        0xD8 => '\u27A2',
                        0xFC => '\u2713',
                        0x76 => '\u2756',
                        0x6E => '\u25A0',
                        0x71 => '\u25A1',
                        0x6F => character == 'o' ? 'o' : '\u25A1',
                        _ => code is >= 0x20 and < 0x7F && !symbolFont ? (char)code : '\u2022'
                    });
                }
                else result.Append(character);
            }
            return result.ToString();
        }

        // ── Runs ─────────────────────────────────────────────────────────────────────────

        private bool Showing => _fields.All(frame => frame.InResult && !frame.Suppress);

        private void CollectInlines(IEnumerable<XElement> elements, OfficeParagraph paragraph, RunProps paraRun)
        {
            foreach (XElement element in elements)
            {
                switch (element.Name.LocalName)
                {
                    case "r":
                        ReadRun(element, paragraph, paraRun);
                        break;
                    case "hyperlink":
                    case "smartTag":
                    case "ins":
                    case "moveTo":
                    case "customXml":
                    case "bdo":
                    case "dir":
                        CollectInlines(element.Elements(), paragraph, paraRun);
                        break;
                    case "sdt":
                        CollectInlines(Child(element, "sdtContent")?.Elements() ?? [], paragraph, paraRun);
                        break;
                    case "fldSimple":
                        string instruction = (Attr(element, "instr") ?? "").Trim();
                        if (FieldKind(instruction) is OfficeFieldKind simpleKind)
                        {
                            var style = paraRun.Clone();
                            style.Apply(Child(element.Descendants().FirstOrDefault(child => child.Name.LocalName == "r"), "rPr"), _theme);
                            paragraph.Inlines.Add(new OfficeField(simpleKind, style.ToStyle()));
                        }
                        else CollectInlines(element.Elements(), paragraph, paraRun);
                        break;
                    case "oMathPara":
                    case "oMath":
                        string math = string.Concat(element.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value));
                        if (math.Length > 0 && Showing) paragraph.Inlines.Add(new OfficeText(math, paraRun.ToStyle()));
                        break;
                    case "AlternateContent":
                        XElement? choice = Child(element, "Choice") ?? Child(element, "Fallback");
                        CollectInlines(choice?.Elements() ?? [], paragraph, paraRun);
                        break;
                }
            }
        }

        private static OfficeFieldKind? FieldKind(string instruction)
        {
            string word = instruction.TrimStart().Split(' ', 2)[0].ToUpperInvariant();
            return word switch
            {
                "PAGE" => OfficeFieldKind.Page,
                "NUMPAGES" or "SECTIONPAGES" => OfficeFieldKind.NumPages,
                _ => null
            };
        }

        private void ReadRun(XElement run, OfficeParagraph paragraph, RunProps paraRun)
        {
            XElement? rPr = Child(run, "rPr");
            RunProps props = paraRun.Clone();
            string? characterStyle = Attr(Child(rPr, "rStyle"), "val");
            foreach (StyleDef style in Chain(characterStyle)) props.Apply(style.RPr, _theme);
            props.Apply(rPr, _theme);
            bool hidden = props.Hidden == true;
            OfficeRunStyle runStyle = props.ToStyle();

            void Text(string text)
            {
                if (hidden || !Showing || text.Length == 0) return;
                if (props.Caps == true) text = text.ToUpper(CultureInfo.CurrentCulture);
                if (paragraph.Inlines.Count > 0 && paragraph.Inlines[^1] is OfficeText previous && previous.Style == runStyle)
                    paragraph.Inlines[^1] = previous with { Text = previous.Text + text };
                else paragraph.Inlines.Add(new OfficeText(text, runStyle));
            }

            foreach (XElement element in run.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "t":
                        Text(element.Value);
                        break;
                    case "tab":
                        if (!hidden && Showing) paragraph.Inlines.Add(new OfficeTab(runStyle));
                        break;
                    case "ptab":
                        if (!hidden && Showing)
                            paragraph.Inlines.Add(new OfficeTab(runStyle, Attr(element, "alignment") switch
                            {
                                "center" => OfficeTabKind.Center,
                                "right" => OfficeTabKind.Right,
                                _ => OfficeTabKind.Left
                            }));
                        break;
                    case "br":
                        if (hidden || !Showing) break;
                        paragraph.Inlines.Add(new OfficeLineBreak(runStyle, Attr(element, "type") == "page"));
                        break;
                    case "cr":
                        if (!hidden && Showing) paragraph.Inlines.Add(new OfficeLineBreak(runStyle, false));
                        break;
                    case "noBreakHyphen":
                        Text("-");
                        break;
                    case "sym":
                        if (int.TryParse(Attr(element, "char"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            string symbol = ((char)code).ToString();
                            string? symbolFont = Attr(element, "font");
                            Text(symbolFont is not null && (symbolFont.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                                || symbolFont.Contains("Wingdings", StringComparison.OrdinalIgnoreCase))
                                ? Bullet(symbol, symbolFont) : symbol);
                        }
                        break;
                    case "fldChar":
                        switch (Attr(element, "fldCharType"))
                        {
                            case "begin":
                                _fields.Push(new FieldFrame());
                                break;
                            case "separate":
                                if (_fields.Count > 0)
                                {
                                    FieldFrame frame = _fields.Peek();
                                    if (FieldKind(frame.Instruction.ToString()) is OfficeFieldKind kind)
                                    {
                                        bool outerShowing = _fields.Skip(1).All(outer => outer.InResult && !outer.Suppress);
                                        if (outerShowing && !hidden) paragraph.Inlines.Add(new OfficeField(kind, runStyle));
                                        frame.Suppress = true;
                                    }
                                    frame.InResult = true;
                                }
                                break;
                            case "end":
                                if (_fields.Count > 0)
                                {
                                    FieldFrame frame = _fields.Pop();
                                    // A field without a separate result shows nothing but PAGE fields.
                                    if (!frame.InResult && FieldKind(frame.Instruction.ToString()) is OfficeFieldKind kind && Showing && !hidden)
                                        paragraph.Inlines.Add(new OfficeField(kind, runStyle));
                                }
                                break;
                        }
                        break;
                    case "instrText":
                        if (_fields.Count > 0) _fields.Peek().Instruction.Append(element.Value);
                        break;
                    case "footnoteReference":
                    case "endnoteReference":
                        if (hidden || !Showing || Attr(element, "id") is not string noteId) break;
                        int number = _notes.Count + 1;
                        _notes.Add((element.Name.LocalName == "footnoteReference" ? "footnote" : "endnote", noteId, number));
                        paragraph.Inlines.Add(new OfficeText(number.ToString(CultureInfo.CurrentCulture),
                            runStyle with { Position = OfficeVerticalPosition.Superscript }));
                        break;
                    case "drawing":
                        if (!hidden && Showing) ReadDrawing(element, paragraph);
                        break;
                    case "pict":
                    case "object":
                        if (!hidden && Showing) ReadVml(element, paragraph);
                        break;
                    case "AlternateContent":
                        XElement? choice = Child(element, "Choice") ?? Child(element, "Fallback");
                        if (choice is not null)
                            ReadRun(new XElement(run.Name, rPr, choice.Elements()), paragraph, paraRun);
                        break;
                }
            }
        }

        // ── Pictures and text boxes ──────────────────────────────────────────────────────

        private void ReadDrawing(XElement drawing, OfficeParagraph paragraph)
        {
            XElement? extent = drawing.Descendants().FirstOrDefault(element => element.Name.LocalName == "extent");
            double width = (Number(Attr(extent, "cx")) ?? 0) / 12700;
            double height = (Number(Attr(extent, "cy")) ?? 0) / 12700;
            XElement? blip = drawing.Descendants().FirstOrDefault(element => element.Name.LocalName == "blip");
            string? id = Attr(blip, "embed");
            if (id is not null && Image(id) is PdfImage image)
            {
                if (width <= 0 || height <= 0)
                {
                    width = image.Width * 0.75;
                    height = image.Height * 0.75;
                }
                paragraph.Inlines.Add(new OfficePicture(image, width, height));
            }
            XElement? textBox = drawing.Descendants().FirstOrDefault(element => element.Name.LocalName == "txbxContent");
            if (textBox is not null) QueueTextBox(textBox);
        }

        private void ReadVml(XElement pict, OfficeParagraph paragraph)
        {
            XElement? data = pict.Descendants().FirstOrDefault(element => element.Name.LocalName == "imagedata");
            string? id = Attr(data, "id");
            if (id is not null && Image(id) is PdfImage image)
            {
                XElement? shape = data!.Parent;
                (double width, double height) = VmlSize(Attr(shape, "style"));
                if (width <= 0 || height <= 0)
                {
                    width = image.Width * 0.75;
                    height = image.Height * 0.75;
                }
                paragraph.Inlines.Add(new OfficePicture(image, width, height));
            }
            XElement? textBox = pict.Descendants().FirstOrDefault(element => element.Name.LocalName == "txbxContent");
            if (textBox is not null) QueueTextBox(textBox);
        }

        private void QueueTextBox(XElement content)
        {
            List<OfficeBlock>? saved = _pending;
            _pending = null;
            List<OfficeBlock> blocks = ReadBlocks(content.Elements(), null);
            _pending = saved ?? [];
            _pending.AddRange(blocks);
        }

        private static (double Width, double Height) VmlSize(string? style)
        {
            double width = 0, height = 0;
            if (style is null) return (0, 0);
            foreach (string part in style.Split(';'))
            {
                string[] pair = part.Split(':', 2);
                if (pair.Length != 2) continue;
                double value = CssLength(pair[1].Trim());
                switch (pair[0].Trim().ToLowerInvariant())
                {
                    case "width": width = value; break;
                    case "height": height = value; break;
                }
            }
            return (width, height);
        }

        private static double CssLength(string value)
        {
            string[] units = ["pt", "in", "cm", "mm", "px"];
            double factor = 1;
            foreach (string unit in units)
            {
                if (!value.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) continue;
                factor = unit switch { "in" => 72, "cm" => 72 / 2.54, "mm" => 72 / 25.4, "px" => 0.75, _ => 1 };
                value = value[..^unit.Length];
                break;
            }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number * factor : 0;
        }

        private PdfImage? Image(string relationshipId)
        {
            string key = _part + "#" + relationshipId;
            if (_images.TryGetValue(key, out PdfImage? cached)) return cached;
            PdfImage? image = null;
            if (_rels.TryGetValue(relationshipId, out string? target) && OfficePackage.Bytes(_archive, target) is byte[] bytes)
                image = OfficeImages.Decode(bytes);
            _images[key] = image;
            return image;
        }

        // ── Tables ───────────────────────────────────────────────────────────────────────

        private OfficeTable ReadTable(XElement element)
        {
            var table = new OfficeTable();
            XElement? tblPr = Child(element, "tblPr");
            string? tableStyle = Attr(Child(tblPr, "tblStyle"), "val");
            foreach (StyleDef style in Chain(tableStyle)) ApplyTableProps(table, style.TblPr);
            ApplyTableProps(table, tblPr);

            foreach (XElement column in Child(element, "tblGrid")?.Elements().Where(child => child.Name.LocalName == "gridCol") ?? [])
                table.Columns.Add(Twips(Attr(column, "w")) ?? 0);

            foreach (XElement rowElement in Flatten(element.Elements()).Where(child => child.Name.LocalName == "tr"))
            {
                var row = new OfficeTableRow();
                XElement? trPr = Child(rowElement, "trPr");
                row.Header = Child(trPr, "tblHeader") is XElement header && Toggle(header);
                if (Child(trPr, "trHeight") is XElement trHeight && Twips(Attr(trHeight, "val")) is double rowHeight)
                {
                    row.Height = rowHeight;
                    row.ExactHeight = Attr(trHeight, "hRule") == "exact";
                }
                int column = int.TryParse(Attr(Child(trPr, "gridBefore"), "val"), out int before) ? before : 0;
                foreach (XElement cellElement in Flatten(rowElement.Elements()).Where(child => child.Name.LocalName == "tc"))
                {
                    var cell = new OfficeTableCell { Column = column };
                    XElement? tcPr = Child(cellElement, "tcPr");
                    if (int.TryParse(Attr(Child(tcPr, "gridSpan"), "val"), out int span) && span > 1) cell.Span = span;
                    if (Child(tcPr, "vMerge") is XElement vMerge && Attr(vMerge, "val") is not "restart")
                        cell.MergedFromAbove = true;
                    cell.Shading = OfficeColor.Parse(Attr(Child(tcPr, "shd"), "fill"));
                    cell.VerticalAlignment = Attr(Child(tcPr, "vAlign"), "val") switch
                    {
                        "center" => OfficeCellAlignment.Center,
                        "bottom" => OfficeCellAlignment.Bottom,
                        _ => OfficeCellAlignment.Top
                    };
                    if (Child(tcPr, "tcBorders") is XElement borders)
                    {
                        cell.Top = Override(Child(borders, "top"));
                        cell.Bottom = Override(Child(borders, "bottom"));
                        cell.Left = Override(Child(borders, "left") ?? Child(borders, "start"));
                        cell.Right = Override(Child(borders, "right") ?? Child(borders, "end"));
                    }
                    cell.Blocks.AddRange(ReadBlocks(cellElement.Elements().Where(child => child.Name.LocalName != "tcPr"), tableStyle));
                    row.Cells.Add(cell);
                    column += cell.Span;
                }
                table.Rows.Add(row);
            }

            for (int rowIndex = 0; rowIndex + 1 < table.Rows.Count; rowIndex++)
                foreach (OfficeTableCell cell in table.Rows[rowIndex].Cells)
                    cell.MergesBelow = table.Rows[rowIndex + 1].Cells.Any(below => below.Column == cell.Column && below.MergedFromAbove);
            return table;
        }

        private static void ApplyTableProps(OfficeTable table, XElement? tblPr)
        {
            if (tblPr is null) return;
            if (Child(tblPr, "tblBorders") is XElement borders)
            {
                OfficeBorderOverride? Edge(params string[] names)
                {
                    foreach (string name in names)
                        if (Child(borders, name) is XElement edge) return Override(edge);
                    return null;
                }
                if (Edge("top") is { } top) table.Top = top.Border;
                if (Edge("bottom") is { } bottom) table.Bottom = bottom.Border;
                if (Edge("left", "start") is { } left) table.Left = left.Border;
                if (Edge("right", "end") is { } right) table.Right = right.Border;
                if (Edge("insideH") is { } insideH) table.InsideHorizontal = insideH.Border;
                if (Edge("insideV") is { } insideV) table.InsideVertical = insideV.Border;
            }
            if (Child(tblPr, "tblInd") is XElement indent && Attr(indent, "type") is null or "dxa"
                && Twips(Attr(indent, "w")) is double indentValue)
                table.Indent = indentValue;
            if (Child(tblPr, "jc") is XElement jc)
                table.Alignment = Attr(jc, "val") switch
                {
                    "center" => OfficeAlignment.Center,
                    "right" or "end" => OfficeAlignment.Right,
                    _ => OfficeAlignment.Left
                };
            if (Child(tblPr, "tblCellMar") is XElement margins)
            {
                double? Margin(params string[] names)
                {
                    foreach (string name in names)
                        if (Child(margins, name) is XElement edge && Attr(edge, "type") is null or "dxa" && Twips(Attr(edge, "w")) is double value)
                            return value;
                    return null;
                }
                if (Margin("left", "start") is double left) table.PaddingLeft = left;
                if (Margin("right", "end") is double right) table.PaddingRight = right;
                if (Margin("top") is double top) table.PaddingTop = top;
                if (Margin("bottom") is double bottom) table.PaddingBottom = bottom;
            }
        }

        private static OfficeBorderOverride? Override(XElement? edge)
        {
            if (edge is null) return null;
            string value = Attr(edge, "val") ?? "single";
            if (value is "nil" or "none") return new OfficeBorderOverride(null);
            double width = Math.Max(0.25, (Number(Attr(edge, "sz")) ?? 4) / 8);
            if (value is "double") width = Math.Max(width, 1.5);
            OfficeColor color = OfficeColor.Parse(Attr(edge, "color")) ?? OfficeColor.Black;
            return new OfficeBorderOverride(new OfficeBorder(width, color));
        }
    }

    // ── XML helpers ──────────────────────────────────────────────────────────────────────

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string? Attr(XElement? element, string localName)
    {
        if (element is null) return null;
        XAttribute? match = null;
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.Name.LocalName != localName) continue;
            if (attribute.Name.Namespace != XNamespace.None || match is null) match = attribute;
        }
        return match?.Value;
    }

    private static bool Toggle(XElement element) =>
        Attr(element, "val") is not ("0" or "false" or "off");

    private static bool Toggle(XElement element, string attribute) =>
        Attr(element, attribute) is "1" or "true" or "on";

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : null;

    private static double? Twips(string? value) => Number(value) is double twips ? twips / 20 : null;

    private static OfficeColor? HighlightColor(string? name) => name switch
    {
        "yellow" => new OfficeColor(255, 255, 0),
        "green" => new OfficeColor(0, 255, 0),
        "cyan" => new OfficeColor(0, 255, 255),
        "magenta" => new OfficeColor(255, 0, 255),
        "blue" => new OfficeColor(0, 0, 255),
        "red" => new OfficeColor(255, 0, 0),
        "darkBlue" => new OfficeColor(0, 0, 128),
        "darkCyan" => new OfficeColor(0, 128, 128),
        "darkGreen" => new OfficeColor(0, 128, 0),
        "darkMagenta" => new OfficeColor(128, 0, 128),
        "darkRed" => new OfficeColor(128, 0, 0),
        "darkYellow" => new OfficeColor(128, 128, 0),
        "darkGray" => new OfficeColor(128, 128, 128),
        "lightGray" => new OfficeColor(192, 192, 192),
        "black" => new OfficeColor(0, 0, 0),
        "white" => new OfficeColor(255, 255, 255),
        _ => null
    };
}

/// <summary>Bounded reads from an Office zip package.</summary>
internal static class OfficePackage
{
    private const long MaximumPartBytes = 256L * 1024 * 1024;
    private const int MaximumEntries = 20000;

    internal static ZipArchive Open(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ZipArchive archive;
        try { archive = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException ex) { throw new InvalidDataException("The file is not a valid Office package.", ex); }
        if (archive.Entries.Count > MaximumEntries)
        {
            archive.Dispose();
            throw new InvalidDataException("The Office package contains too many parts.");
        }
        return archive;
    }

    internal static byte[]? Bytes(ZipArchive archive, string path)
    {
        ZipArchiveEntry? entry = archive.GetEntry(path)
            ?? archive.Entries.FirstOrDefault(candidate => string.Equals(candidate.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > MaximumPartBytes) throw new InvalidDataException($"The package part {path} is too large.");
        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaximumPartBytes) throw new InvalidDataException($"The package part {path} is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    internal static XDocument? Xml(ZipArchive archive, string path)
    {
        byte[]? bytes = Bytes(archive, path);
        if (bytes is null) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    internal static string Folder(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? "" : part[..slash];
    }

    /// <summary>The main part named by the package relationships, or the usual path.</summary>
    internal static string MainPart(ZipArchive archive, string fallback)
    {
        XDocument? rels = Xml(archive, "_rels/.rels");
        string? target = rels?.Root?.Elements()
            .FirstOrDefault(element => element.Attribute("Type")?.Value.EndsWith("/officeDocument", StringComparison.Ordinal) == true)
            ?.Attribute("Target")?.Value;
        return target is null ? fallback : Resolve("", target);
    }

    private static string RelationshipsPath(string part)
    {
        string folder = Folder(part);
        string name = part[(part.LastIndexOf('/') + 1)..];
        return (folder.Length > 0 ? folder + "/" : "") + "_rels/" + name + ".rels";
    }

    /// <summary>Relationship id to resolved part path, internal targets only.</summary>
    internal static Dictionary<string, string> Relationships(ZipArchive archive, string part)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument? rels = Xml(archive, RelationshipsPath(part));
        if (rels?.Root is null) return result;
        foreach (XElement relationship in rels.Root.Elements())
        {
            if (relationship.Attribute("TargetMode")?.Value == "External") continue;
            string? id = relationship.Attribute("Id")?.Value;
            string? target = relationship.Attribute("Target")?.Value;
            if (id is not null && target is not null) result[id] = Resolve(Folder(part), target);
        }
        return result;
    }

    /// <summary>Relationship type name (the last path segment) to resolved part path.</summary>
    internal static Dictionary<string, string> RelationshipsByType(ZipArchive archive, string part)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XDocument? rels = Xml(archive, RelationshipsPath(part));
        if (rels?.Root is null) return result;
        foreach (XElement relationship in rels.Root.Elements())
        {
            if (relationship.Attribute("TargetMode")?.Value == "External") continue;
            string? type = relationship.Attribute("Type")?.Value;
            string? target = relationship.Attribute("Target")?.Value;
            if (type is null || target is null) continue;
            string name = type[(type.LastIndexOf('/') + 1)..];
            result.TryAdd(name, Resolve(Folder(part), target));
        }
        return result;
    }

    internal static string Resolve(string folder, string target)
    {
        target = target.Replace('\\', '/');
        var segments = new List<string>();
        if (!target.StartsWith('/') && folder.Length > 0) segments.AddRange(folder.Split('/'));
        foreach (string segment in target.TrimStart('/').Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(Uri.UnescapeDataString(segment));
        }
        return string.Join('/', segments);
    }
}

/// <summary>Decodes pictures from Office packages into PDF images.</summary>
internal static class OfficeImages
{
    private const long MaximumPixels = 40_000_000;

    internal static PdfImage? Decode(byte[] bytes)
    {
        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            try { return PdfImage.FromJpeg(bytes); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException
                or InvalidDataException or InvalidOperationException) { }
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat
                | System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            System.Windows.Media.Imaging.BitmapSource frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) return null;
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            bool opaque = true;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
                if (pixels[index + 3] != 255) opaque = false;
            }
            if (!opaque) return PdfImage.FromRgba(width, height, pixels);
            byte[] rgb = new byte[width * height * 3];
            for (int pixel = 0, source = 0; source < pixels.Length; pixel += 3, source += 4)
            {
                rgb[pixel] = pixels[source];
                rgb[pixel + 1] = pixels[source + 1];
                rgb[pixel + 2] = pixels[source + 2];
            }
            return PdfImage.FromRgb(width, height, rgb);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException
            or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }
}
