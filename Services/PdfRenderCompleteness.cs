namespace KillerPDF.Services;

internal enum PdfRenderDiagnosticKind { Information, Recovery, Degraded, OmittedContent }

internal sealed record PdfRenderDiagnostic(PdfRenderDiagnosticKind Kind, string Message)
{
    internal bool BlocksPublication => Kind is PdfRenderDiagnosticKind.Degraded or PdfRenderDiagnosticKind.OmittedContent;
}

internal static class PdfRenderCompleteness
{
    // Adapt the engine's compatibility messages at one boundary. Unknown diagnostics
    // remain blocking until their rendering consequences have been reviewed.
    internal static IReadOnlyList<PdfRenderDiagnostic> Classify(IReadOnlyList<string> messages) =>
        Array.AsReadOnly(messages.Select(message => new PdfRenderDiagnostic(Kind(message), message)).ToArray());

    private static PdfRenderDiagnosticKind Kind(string message)
    {
        if (message.Contains("omitted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("skipped", StringComparison.OrdinalIgnoreCase)
            || message.Contains("truncated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase)
            || message.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
            || message.Contains("treated as empty", StringComparison.OrdinalIgnoreCase))
            return PdfRenderDiagnosticKind.OmittedContent;
        if (message == "The supplied password authenticated a legacy unnormalized Unicode password hash.")
            return PdfRenderDiagnosticKind.Information;
        if (message == "Font selection without a name used the only page font resource."
            || message == "Unclosed graphics states were restored at the end of the content stream."
            || message == "A color operator with an invalid component count was recovered."
            || message.StartsWith("Missing standard font resource /", StringComparison.Ordinal)
                && message.EndsWith(" was recovered.", StringComparison.Ordinal))
            return PdfRenderDiagnosticKind.Recovery;
        return PdfRenderDiagnosticKind.Degraded;
    }

    internal static void RequireComplete(int pageIndex, IReadOnlyList<PdfRenderDiagnostic> diagnostics)
    {
        string[] failures = [.. diagnostics.Where(item => item.BlocksPublication).Select(item => item.Message)];
        if (failures.Length > 0)
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be completely rendered: {string.Join(" ", failures)}");
    }
}
