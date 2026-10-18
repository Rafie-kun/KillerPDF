using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

internal static class PdfMacroExportFiles
{
    internal static string[] Plan(PdfMacro macro, IEnumerable<string> pdfPaths,
        IEnumerable<string>? protectedSources = null)
    {
        PdfStructuredExportFormat[] formats = [.. macro.Steps
            .Where(step => step.Operation == PdfMacroOperation.Export).Select(PdfStructuredExportMacro.ReadFormat)];
        string[] paths = [.. pdfPaths.SelectMany(path => new[] { Path.GetFullPath(path) }
            .Concat(formats.Select((format, index) => ExportPath(path, format, index, formats.Length))))];
        var protectedPaths = (protectedSources ?? []).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
            || paths.Any(protectedPaths.Contains))
            throw new ArgumentException("Macro output filenames must be unique and cannot replace a source file.");
        return paths;
    }

    internal static IReadOnlyList<PdfMacroOutput> Expand(PdfMacroOutput pdf)
    {
        if (!pdf.Succeeded) return [pdf];
        string name = pdf.OutputFileName ?? throw new InvalidOperationException("A macro output filename is required.");
        return new[] { pdf }.Concat(pdf.Exports.Select((export, index) => new PdfMacroOutput(
            export.Data, Path.GetFileName(ExportPath(name, export.Format, index, pdf.Exports.Count)),
            RecordIndex: pdf.RecordIndex))).ToArray();
    }

    private static string ExportPath(string pdf, PdfStructuredExportFormat format, int index, int count)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(pdf))!;
        string stem = Path.GetFileNameWithoutExtension(pdf);
        return Path.Combine(directory, stem + (count > 1 ? "-export-" + (index + 1) : "")
            + PdfStructuredExportMacro.Extension(format));
    }
}
