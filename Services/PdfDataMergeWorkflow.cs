using System.IO;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;

namespace KillerPDF.Services;

internal sealed record PdfDataMergePlan(
    PdfDataMergeProfile Profile,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Records,
    IReadOnlyList<string> OutputFileNames,
    int MatchedFieldCount);

internal static class PdfDataMergeWorkflow
{
    private const string OutputNameField = "__KillerPDFOutputFileName";

    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> LoadRecords(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csv" => PdfDataRecordReader.FromCsv(File.ReadAllText(path)),
            ".json" => PdfDataRecordReader.FromJson(File.ReadAllText(path)),
            ".xlsx" => PdfDataRecordReader.FromXlsx(File.ReadAllBytes(path)),
            _ => throw new NotSupportedException("Merge data must be CSV, JSON, or XLSX.")
        };

    internal static PdfDataMergePlan CreatePlan(
        PdfDocument template,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records,
        string firstOutputFileName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            throw new ArgumentException("Merge data has no records.", nameof(records));
        if (string.IsNullOrWhiteSpace(firstOutputFileName))
            throw new ArgumentException("An output filename is required.", nameof(firstOutputFileName));

        var sourceNames = new List<string>();
        var knownSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string?> record in records)
            foreach (string sourceName in record.Keys)
                if (knownSourceNames.Add(sourceName)) sourceNames.Add(sourceName);
        if (knownSourceNames.Contains(OutputNameField))
            throw new ArgumentException("Merge data uses a reserved field name.", nameof(records));

        IReadOnlyList<PdfFormDataField> fields = PdfFormDataExporter.Export(
            template, includeAnnotations: false).Fields;
        var mappings = new List<PdfDataMergeFieldMapping>();
        foreach (PdfFormDataField field in fields)
        {
            string? sourceName = sourceNames.FirstOrDefault(name =>
                string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase));
            sourceName ??= sourceNames.FirstOrDefault(name =>
                !string.IsNullOrWhiteSpace(field.MappingName)
                && string.Equals(name, field.MappingName, StringComparison.OrdinalIgnoreCase));
            if (sourceName is not null)
                mappings.Add(new PdfDataMergeFieldMapping(sourceName, field.Name));
        }
        if (mappings.Count == 0)
            throw new InvalidOperationException(
                "No merge-data columns match the PDF form field names or mapping names.");

        string extension = Path.GetExtension(firstOutputFileName);
        if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) extension = ".pdf";
        string stem = Path.GetFileNameWithoutExtension(firstOutputFileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "merged";

        var outputNames = new string[records.Count];
        var preparedRecords = new IReadOnlyDictionary<string, string?>[records.Count];
        for (int index = 0; index < records.Count; index++)
        {
            outputNames[index] = index == 0
                ? stem + extension
                : $"{stem}-{index + 1}{extension}";
            var prepared = new Dictionary<string, string?>(
                records[index], StringComparer.OrdinalIgnoreCase)
            {
                [OutputNameField] = outputNames[index]
            };
            preparedRecords[index] = prepared;
        }

        var profile = new PdfDataMergeProfile(
            "Desktop data merge", mappings, "{{" + OutputNameField + "}}");
        return new PdfDataMergePlan(profile, preparedRecords, outputNames, mappings.Count);
    }

    /// <summary>
    /// Fills one copy of the template per record and writes each into the folder, replacing a
    /// file of the same name only once its new copy is complete. A copy that cannot be written
    /// is reported as failed rather than stopping the rest.
    /// </summary>
    internal static PdfDataMergeBatchReport RunToDirectory(byte[] templateBytes, PdfDataMergePlan plan,
        string outputDirectory, PdfDataMergeOutputMode mode, CancellationToken token)
    {
        IReadOnlyList<PdfDataMergeDocumentResult> generated = PdfDataMerge.RunFormBatch(
            PdfDocument.Open(templateBytes), plan.Records, plan.Profile, mode, token);
        var completed = new List<PdfDataMergeDocumentResult>(generated.Count);
        foreach (PdfDataMergeDocumentResult result in generated)
        {
            if (!result.Succeeded || result.Data is null || result.OutputFileName is null)
            {
                completed.Add(result);
                continue;
            }
            string destination = Path.Combine(outputDirectory, result.OutputFileName);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, result.Data.Value.ToArray());
                File.Move(temporary, destination, overwrite: true);
                completed.Add(result);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
                completed.Add(new PdfDataMergeDocumentResult(
                    result.RecordIndex, result.OutputFileName, null, ex.Message));
            }
        }
        return PdfDataMergeBatchReport.Create(completed);
    }
}
