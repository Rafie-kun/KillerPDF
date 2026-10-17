using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services;
using ContentDoc = KillerPDF.Services.PdfContentDocument;
using EngineDocs = KillerPdf.Engine.Documents;

namespace KillerPDF.Controls
{
    /// <summary>What a Find and Replace pass changed.</summary>
    internal readonly record struct TextReplaceResult(int Occurrences, int Lines, int SkippedTurnedLines);

    // Find and Replace (#422). Each PDF line that contains the search text gets the same edit the
    // Text tool makes when a line is double-clicked: the line is rewritten in the page content, in
    // its own font when that font can show the new text. A line the page content cannot take, such
    // as recognized text over a scan, falls back to a cover with replacement text on top. Text
    // already placed as an annotation is rewritten in place. The whole pass is one undo.
    public partial class PdfViewer
    {
        internal TextReplaceResult ReplaceTextExt(string find, string replace, bool matchCase, bool wholeWord,
            (int Page, double Left, double Bottom, double Right, double Top)? onlyMatch = null)
            => ReplaceText(find, replace, matchCase, wholeWord, onlyMatch);

        /// <summary>True when an existing cover already hides this part of the page, so the text
        /// under it is no longer what the reader sees. Rects are unrotated page points.</summary>
        internal bool IsPdfRectCovered(int pageIndex, double left, double bottom, double right, double top)
        {
            if (!_annotations.TryGetValue(pageIndex, out var list) || !list.OfType<CoverAnnotation>().Any()) return false;
            if (!TryCanvasFrame(pageIndex, out var frame)) return false;
            Rect canvas = RedactionGeometry.PageToCanvas(left, bottom, right, top,
                frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
            return list.OfType<CoverAnnotation>().Any(cover => cover.Bounds.Contains(canvas)
                || Intersection(cover.Bounds, canvas) > canvas.Width * canvas.Height * .5);
        }

        private TextReplaceResult ReplaceText(string find, string replace, bool matchCase, bool wholeWord,
            (int Page, double Left, double Bottom, double Right, double Top)? onlyMatch)
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile) || string.IsNullOrEmpty(find))
                return default;
            CommitActiveTextBox();
            replace ??= string.Empty;

            int occurrences = 0, lines = 0, skippedTurned = 0;
            var direct = new List<DirectReplacement>();
            var annotationEdits = new List<(int Page, TextAnnotation Original, string Content)>();
            using (var contentDoc = ContentDoc.Open(_currentFile))
            {
                IEnumerable<int> pages = onlyMatch is { } match
                    ? new[] { match.Page }
                    : Enumerable.Range(0, contentDoc.NumberOfPages);
                foreach (int pageIndex in pages)
                {
                    if (pageIndex < 0 || pageIndex >= contentDoc.NumberOfPages) continue;
                    var page = contentDoc.GetPage(pageIndex + 1);
                    EnsureRenderDims(pageIndex, page.Width, page.Height);
                    if (!TryCanvasFrame(pageIndex, out var frame)) continue;

                    Rect? onlyCanvas = onlyMatch is { } only
                        ? RedactionGeometry.PageToCanvas(only.Left, only.Bottom, only.Right, only.Top,
                            frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation)
                        : null;

                    var words = page.GetWords().ToList();
                    var level = new List<ReplaceWord>();
                    var levelSource = new List<KillerPdf.Engine.Documents.PdfExtractedWord>();
                    foreach (var word in words)
                    {
                        var b = word.BoundingBox;
                        Rect rect = RedactionGeometry.PageToCanvas(b.Left, b.Bottom, b.Right, b.Top,
                            frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
                        int turn = 0;
                        if (word.Letters.Count > 0)
                        {
                            var first = word.Letters[0];
                            turn = TextTurn.FromBaseline(frame.ToCanvas(first.StartBaseLine.X, first.StartBaseLine.Y),
                                frame.ToCanvas(first.EndBaseLine.X, first.EndBaseLine.Y));
                        }
                        if (TextTurn.Normalize(turn) != 0)
                        {
                            // Turned text is left alone; it is counted below when it would have matched.
                            if (TextReplacePlanner.FindAll(word.Text, find, matchCase, wholeWord).Count > 0) skippedTurned++;
                            continue;
                        }
                        level.Add(new ReplaceWord(word.Text, rect));
                        levelSource.Add(word);
                    }

                    List<Rect> coversOnPage = _annotations.TryGetValue(pageIndex, out var existing)
                        ? existing.OfType<CoverAnnotation>().Select(c => c.Bounds).ToList()
                        : [];
                    var plans = TextReplacePlanner.Plan(level, find, replace, matchCase, wholeWord,
                        onlyCanvas is Rect target ? bounds => Intersection(bounds, target) > 0 : null);
                    foreach (ReplaceLine plan in plans)
                    {
                        // A line someone already edited with a cover is consumed: its replacement
                        // text is an annotation and gets rewritten below instead.
                        if (coversOnPage.Any(cover => cover.IntersectsWith(plan.Bounds))) continue;
                        var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(plan.Bounds,
                            frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
                        direct.Add(new DirectReplacement(pageIndex, frame, plan, levelSource[plan.WordIndices[0]],
                            new EngineDocs.PdfContentBounds(left - 0.5, bottom - 0.5, right + 0.5, top + 0.5)));
                        occurrences += plan.MatchBounds.Count;
                        lines++;
                    }

                    if (onlyMatch is null && _annotations.TryGetValue(pageIndex, out var annotations))
                    {
                        foreach (TextAnnotation text in annotations.OfType<TextAnnotation>())
                        {
                            var starts = TextReplacePlanner.FindAll(text.Content, find, matchCase, wholeWord);
                            if (starts.Count == 0) continue;
                            annotationEdits.Add((pageIndex, text,
                                TextReplacePlanner.ReplaceAt(text.Content, starts, find.Length, replace)));
                            occurrences += starts.Count;
                            lines++;
                        }
                    }
                }
            }
            var result = new TextReplaceResult(occurrences, lines, skippedTurned);
            if (direct.Count == 0 && annotationEdits.Count == 0) return result;

            if (direct.Count > 0)
            {
                // The page lines change in the page content in one rewrite. Its document undo also
                // holds the annotations as they were, so the annotation changes made after it undo
                // with it in one step.
                var edits = direct.Select(item => new EngineDocs.PdfTextLineEdit(item.Page, item.Region, item.Plan.NewText)
                {
                    Substitute = SubstituteFont(item.First, item.Plan.NewText)
                }).ToList();
                IReadOnlyList<int> failed = [.. Enumerable.Range(0, direct.Count)];
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                        finalizeSavedFile: path => failed = PdfEngineIntegration.ReplaceTextLines(path, edits));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { }
                finally { Mouse.OverrideCursor = null; }
                foreach (int index in failed)
                {
                    DirectReplacement item = direct[index];
                    AddReplacementEdit(item.Page, item.Frame, item.Plan, item.First);
                }
                ApplyAnnotationEdits(annotationEdits);
                MarkDirty();
                ClearSelection();
                foreach (int page in direct.Select(item => item.Page).Concat(annotationEdits.Select(edit => edit.Page)).Distinct())
                    RenderAllAnnotations(page);
                return result;
            }

            var snapshot = new Dictionary<int, List<PageAnnotation>>();
            foreach (int page in annotationEdits.Select(edit => edit.Page).Distinct())
                snapshot[page] = _annotations.TryGetValue(page, out var list) ? [.. list] : [];
            ApplyAnnotationEdits(annotationEdits);
            PushUndo(new UndoEntry(UndoKind.PageSnapshot, WasDirty: _isDirty, AnnotSnapshot: snapshot));
            MarkDirty();
            ClearSelection();
            foreach (int page in snapshot.Keys) RenderAllAnnotations(page);
            return result;
        }

        // A matched line of page text, with what its fallback edit needs if the page content cannot take it.
        private readonly record struct DirectReplacement(int Page, CanvasFrame Frame, ReplaceLine Plan,
            KillerPdf.Engine.Documents.PdfExtractedWord First, EngineDocs.PdfContentBounds Region);

        // A clone carries each new text, so an undo snapshot taken before keeps the original.
        private void ApplyAnnotationEdits(List<(int Page, TextAnnotation Original, string Content)> edits)
        {
            foreach (var (page, original, content) in edits)
            {
                if (!_annotations.TryGetValue(page, out var list)) continue;
                int index = list.IndexOf(original);
                if (index < 0 || CloneAnnotation(original) is not TextAnnotation updated) continue;
                updated.Content = content;
                list[index] = updated;
            }
        }

        // The installed face for a line whose own font cannot show its new text, from the font the
        // line was found in.
        private static KillerPdf.Engine.Fonts.TrueTypeFont? SubstituteFont(
            KillerPdf.Engine.Documents.PdfExtractedWord firstWord, string text)
        {
            string family = "Segoe UI";
            bool bold = false, italic = false;
            if (firstWord.Letters.Count > 0 && !string.IsNullOrEmpty(firstWord.Letters[0].FontName))
            {
                var detected = PdfFontStyle.FromPdfName(firstWord.Letters[0].FontName!);
                family = detected.Family;
                bold = detected.Bold;
                italic = detected.Italic;
            }
            family = PdfFontStyle.ResolveInstalledFamily(family, MainWindow.SystemFontNames);
            return PdfEngineIntegration.LoadTextFont(family, text, bold, italic)?.Font;
        }

        // One replaced line edited the fallback way: a cover over the original text and the paired
        // replacement text, placed and styled the way StartCoverTextEdit and CommitActiveTextBox do it.
        private void AddReplacementEdit(int pageIndex, CanvasFrame frame, ReplaceLine plan,
            KillerPdf.Engine.Documents.PdfExtractedWord firstWord)
        {
            Rect line = plan.Bounds;
            var cover = new CoverAnnotation
            {
                PageIndex = pageIndex,
                PairId = Guid.NewGuid().ToString("N"),
                Bounds = new Rect(line.X - 3, line.Y - 3, line.Width + 6, line.Height + 6)
            };
            Color background = SampleCoverColor(pageIndex, line);
            Color ink = SampleTextColor(pageIndex, line, background);
            cover.SetColor(background);

            double fontSize = line.Height * 0.75;
            string family = "Segoe UI";
            bool bold = false, italic = false;
            double? originLeft = null, baseline = null;
            if (firstWord.Letters.Count > 0)
            {
                var letter = firstWord.Letters[0];
                double points = letter.PointSize > 0 ? letter.PointSize : letter.FontSize;
                if (points > 0) fontSize = points * frame.CanvasUnitsPerPoint;
                Point origin = frame.ToCanvas(letter.StartBaseLine.X, letter.StartBaseLine.Y);
                originLeft = origin.X;
                baseline = origin.Y;
                if (!string.IsNullOrEmpty(letter.FontName))
                {
                    var detected = PdfFontStyle.FromPdfName(letter.FontName!);
                    family = detected.Family;
                    bold = detected.Bold;
                    italic = detected.Italic;
                }
            }
            family = PdfFontStyle.ResolveInstalledFamily(family, MainWindow.SystemFontNames);
            fontSize = Math.Max(fontSize, frame.CanvasUnitsPerPoint);

            double left = originLeft is double x ? PdfTextEditPlacement.LeftFromOrigin(x, 1, 2, line.Left) : line.Left;
            double top = line.Top;
            if (baseline is double y)
            {
                try
                {
                    top = PdfTextEditPlacement.TopFromBaseline(y, new FontFamily(family).Baseline, fontSize, 1, line.Top);
                }
                catch { /* keep the extracted glyph top */ }
            }

            // Wide enough that the replacement stays on one line, like the original.
            double width = Math.Max(Math.Max(line.Width + 20, 80), MeasureLineWidth(plan.NewText, family, bold, italic, fontSize) + 12);
            var text = new TextAnnotation
            {
                PageIndex = pageIndex,
                Position = new Point(left, top),
                Content = plan.NewText,
                FontSize = fontSize,
                FontName = family,
                Bold = bold,
                Italic = italic,
                Width = width,
                PairId = cover.PairId
            };
            text.SetColor(ink);
            text.SetFill(Colors.Transparent);   // the cover behind it is the backdrop
            text.Height = MeasureTextBoxHeight(plan.NewText, width, fontSize);
            text.Position = ClampRectToPage(pageIndex, new Rect(text.Position, text.FootprintSize)).Location;

            if (!_annotations.ContainsKey(pageIndex)) _annotations[pageIndex] = [];
            _annotations[pageIndex].Add(cover);
            _annotations[pageIndex].Add(text);
        }

        private static double MeasureLineWidth(string text, string family, bool bold, bool italic, double fontSize)
        {
            try
            {
                var typeface = new Typeface(new FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal,
                    bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                return new FormattedText(string.IsNullOrEmpty(text) ? " " : text,
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, Math.Max(1, fontSize), Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
            }
            catch { return 0; }
        }

        private static double Intersection(Rect a, Rect b)
        {
            Rect overlap = Rect.Intersect(a, b);
            return overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
        }

        // A page that has never been laid out has no render dims yet. Give it the canonical space
        // every view uses (longest side 2048), so its annotations line up when it is shown.
        private void EnsureRenderDims(int pageIndex, double pdfWidth, double pdfHeight)
        {
            if (_renderDims.TryGetValue(pageIndex, out var existing) && existing.w > 0 && existing.h > 0) return;
            if (pdfWidth <= 0 || pdfHeight <= 0) return;
            bool turned = EffectiveRotation(pageIndex) % 180 != 0;
            double width = turned ? pdfHeight : pdfWidth;
            double height = turned ? pdfWidth : pdfHeight;
            double longest = Math.Max(width, height);
            _renderDims[pageIndex] = (Math.Max(1, (int)Math.Round(2048.0 * width / longest)),
                                      Math.Max(1, (int)Math.Round(2048.0 * height / longest)));
        }

        private readonly record struct CanvasFrame(double PdfWidth, double PdfHeight, int RenderWidth,
            int RenderHeight, int Rotation, double CanvasUnitsPerPoint)
        {
            internal Point ToCanvas(double x, double y) => RedactionGeometry.PageToCanvas(x, y, x, y,
                PdfWidth, PdfHeight, RenderWidth, RenderHeight, Rotation).TopLeft;
        }

        private bool TryCanvasFrame(int pageIndex, out CanvasFrame frame)
        {
            frame = default;
            if (!_renderDims.TryGetValue(pageIndex, out var rd) || rd.w <= 0 || rd.h <= 0) return false;
            var session = EnsureEngineDocumentSession();
            if (pageIndex < 0 || pageIndex >= session.PageCount) return false;
            var info = session.Pages[pageIndex];
            if (info.Width <= 0 || info.Height <= 0) return false;
            int rotation = EffectiveRotation(pageIndex);
            double pointsPerUnit = RedactionGeometry.PointsPerCanvasUnit(info.Width, info.Height, rd.h, rotation);
            if (!(pointsPerUnit > 0)) return false;
            frame = new CanvasFrame(info.Width, info.Height, rd.w, rd.h, rotation, 1.0 / pointsPerUnit);
            return true;
        }
    }
}
