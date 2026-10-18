using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>Generates separate editable PDFs for the records supplied by a macro host.</summary>
public static class PdfDataMergeMacro
{
    /// <summary>Fills each included record and leaves subsequent macro steps to the runner.</summary>
    public static PdfMacroOperationResult Execute(PdfMacroStep step, ReadOnlyMemory<byte> source,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records,
        CancellationToken cancellationToken = default, Func<string, PdfImage>? imageResolver = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        PdfDataMergeProfile profile = PdfDataMergeProfile.FromMacroStep(step);
        PdfDocument template = PdfDocument.Open(source);
        IReadOnlyList<PdfDataMergeDocumentResult> generated = imageResolver is null
            ? PdfDataMerge.RunFormBatch(template, records, profile, cancellationToken: cancellationToken)
            : PdfDataMerge.RunFormBatch(template, records, profile, imageResolver,
                cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(source)
        {
            Outputs = Array.AsReadOnly(generated.Where(result => !result.Skipped)
                .Select(result => new PdfMacroOutput(result.Data, result.OutputFileName,
                    result.Error, result.RecordIndex)).ToArray())
        };
    }
}
