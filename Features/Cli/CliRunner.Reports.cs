using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services;

namespace KillerPDF.Features
{
    // Document tools reports from the command line. Each reads the file without changing it and
    // prints the same report the Document tools show, or JSON with --json.
    internal static partial class CliRunner
    {
        // --print-production, --signatures, --layers, --portfolio, --comments and --navigation-audit
        // share one shape: <in.pdf> [--json], a text report or JSON. --navigation-audit exits 3
        // when it finds a problem, like --preflight.
        private static int CliDocumentReport(string command, List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine($"Usage: KillerPDF.exe {command} <in.pdf> [--json]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            bool json = options.ContainsKey("--json");
            try
            {
                PdfDocument document = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(inPath));
                switch (command)
                {
                    case "--print-production":
                        PdfPrintProductionReport production = PdfPrintProductionReport.Inspect(document);
                        con.WriteLine(json ? production.ToJson(true) : production.ToText());
                        return 0;
                    case "--signatures":
                        PdfSignatureInspectionReport signatures =
                            new Services.Signing.CertificateCatalog().Inspect(document);
                        con.WriteLine(json ? signatures.ToJson(true) : signatures.ToText());
                        return 0;
                    case "--layers":
                        PdfOptionalContentInfo layers = PdfOptionalContentReader.Read(document);
                        con.WriteLine(json ? layers.ToJson(true) : layers.ToText());
                        return 0;
                    case "--portfolio":
                        con.WriteLine(json ? PdfCollectionReader.ToJson(document, true) : PdfCollectionReader.ToText(document));
                        return 0;
                    case "--comments":
                        con.WriteLine(json ? PdfCommentReader.ExportJson(document) : PdfCommentReader.ExportText(document));
                        return 0;
                    default:
                        con.WriteLine(json ? PdfNavigationAudit.ExportJson(document) : PdfNavigationAudit.ExportText(document));
                        return PdfNavigationAudit.Inspect(document).Count == 0 ? 0 : 3;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException
                or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                con.WriteLine("Could not read the document: " + ex.Message);
                return 1;
            }
        }

        // ============================================================
        // --compare-structure <original.pdf> <changed.pdf> [--json]
        // ============================================================
        // Compares interpreted content, resources and page geometry; exits 3 when they differ.
        private static int CliCompareStructure(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --compare-structure <original.pdf> <changed.pdf> [--json]");
                return 2;
            }
            string original = Path.GetFullPath(pos[0]), changed = Path.GetFullPath(pos[1]);
            foreach (string path in new[] { original, changed })
                if (!File.Exists(path)) { con.WriteLine($"Input not found: {path}"); return 2; }
            PdfStructuralComparison comparison;
            try
            {
                comparison = PdfEngineIntegration.CompareStructure(original, changed);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not compare the documents: " + ex.Message);
                return 1;
            }
            con.WriteLine(options.ContainsKey("--json") ? comparison.ToJson(true) : comparison.ToText());
            return comparison.HasChanges ? 3 : 0;
        }

        // ============================================================
        // --measurements <in.pdf> [--json | --csv]
        // ============================================================
        // Reports the measurement annotations saved in the file, each worked out with its own
        // drawing scale, or at actual size when it carries none.
        private static int CliMeasurements(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine("Usage: KillerPDF.exe --measurements <in.pdf> [--json | --csv]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            List<PdfMeasurementResult> results;
            try
            {
                string name = Path.GetFileNameWithoutExtension(inPath);
                results = [.. PdfEngineIntegration.ReadMeasurements(
                        PdfEngineIntegration.OpenForReading(File.ReadAllBytes(inPath)))
                    .Select(measurement => MeasurementResult(measurement, name))
                    .OfType<PdfMeasurementResult>()];
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not read the document: " + ex.Message);
                return 1;
            }
            con.WriteLine(options.ContainsKey("--json") ? PdfMeasurementReport.ToJson(results)
                : options.ContainsKey("--csv") ? PdfMeasurementReport.ToCsv(results)
                : PdfMeasurementReport.ToText(results));
            return 0;
        }

        // One saved measurement as a report row, or null when its points no longer measure anything.
        private static PdfMeasurementResult? MeasurementResult(PdfEngineIntegration.MeasurementData measurement, string document)
        {
            PdfMeasurementProfile profile = measurement.Profile ?? PdfEngineIntegration.ActualSizeProfile;
            PdfMeasurementPoint[] points = [.. measurement.Points.Select(point => new PdfMeasurementPoint(point.X, point.Y))];
            try
            {
                (string kind, double value, string unit) = measurement.Kind switch
                {
                    PdfMeasurementKind.Perimeter => ("Perimeter",
                        PdfMeasurement.Perimeter(profile, points, measurement.Closed), profile.UnitSymbol),
                    PdfMeasurementKind.Area => ("Area", PdfMeasurement.Area(profile, points), "sq " + profile.UnitSymbol),
                    PdfMeasurementKind.Angle => ("Angle", PdfMeasurement.Angle(points[0], points[1], points[2]), "deg"),
                    _ => ("Distance", PdfMeasurement.Distance(profile, points[0], points[^1]), profile.UnitSymbol),
                };
                bool angle = measurement.Kind == PdfMeasurementKind.Angle;
                return new PdfMeasurementResult
                {
                    Document = document,
                    PageIndex = measurement.PageIndex,
                    Kind = kind,
                    Comment = string.IsNullOrWhiteSpace(measurement.Contents) ? null : measurement.Contents,
                    Value = Math.Round(value, angle ? 2 : profile.Precision),
                    Unit = unit,
                    Profile = angle ? "-" : profile.Name,
                    UnitsPerPoint = angle ? null : profile.UnitsPerPoint,
                    Points = points,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                return null;
            }
        }

        // ============================================================
        // --separations <in.pdf> [<outDir>] [--pages <range>] [--dpi <n>] [--json]
        // ============================================================
        // Lists each process and spot ink and the pages it prints on. With a folder, it also
        // writes each page's plates as images showing that ink's coverage, as Separations shows.
        private static int CliSeparations(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count is < 1 or > 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --separations <in.pdf> [<outputFolder>] [--pages <range>] [--dpi <n>] [--json] [--password <password>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            PdfSeparationPreview preview;
            try
            {
                PdfPrintProductionReport production = PdfEngineIntegration.InspectPrintProduction(inPath);
                preview = PdfEngineIntegration.CreateSeparationPreview(inPath,
                    production.Colorants.Select(colorant => colorant.Name));
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or NotSupportedException or ArgumentException or FormatException)
            {
                con.WriteLine("Could not read the document: " + ex.Message);
                return 1;
            }
            if (pos.Count == 1)
            {
                con.WriteLine(options.ContainsKey("--json") ? preview.ToJson(true) : preview.ToText());
                return 0;
            }

            string outDir = Path.GetFullPath(pos[1]);
            options.TryGetValue("--password", out string? password);
            var (renderPath, rotations, _) = CliPrepareRenderSource(inPath, password, con);
            double dpi = CliParseDpi(options, 150);
            using var session = PdfPageRenderSession.OpenEngineFirst(renderPath, dpi / 72.0);
            if (!CliPagesOption(options, session.PageCount, out List<int> pages, con)) return 2;
            Directory.CreateDirectory(outDir);
            string baseName = Path.GetFileNameWithoutExtension(inPath);
            int digits = Math.Max(3, session.PageCount.ToString().Length);
            int written = 0;
            foreach (int page in pages)
            {
                int rotation = rotations is not null && page < rotations.Length ? rotations[page] : 0;
                foreach (var plate in preview.Plates.Where(plate => plate.PageIndexes.Contains(page)))
                {
                    var (pixels, width, height) = CliDisplayedPage(session.RenderPlate(page, plate.Name), rotation);
                    string safe = new([.. plate.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
                    File.WriteAllBytes(Path.Combine(outDir,
                            $"{baseName}-page-{(page + 1).ToString().PadLeft(digits, '0')}-{safe}.png"),
                        BitmapHelpers.RenderToPng(pixels, width, height, dpi));
                    written++;
                }
            }
            con.WriteLine($"Wrote {written} plate images for {pages.Count} pages at {dpi:0} dpi into {outDir}");
            return 0;
        }
    }
}
