namespace KillerPDF.Services;

internal enum PdfRenderDiagnosticKind { Information, Recovery, Degraded, OmittedContent }

internal sealed record PdfRenderDiagnostic(PdfRenderDiagnosticKind Kind, string Message)
{
    internal bool BlocksPublication => Kind is PdfRenderDiagnosticKind.Degraded or PdfRenderDiagnosticKind.OmittedContent;
}

internal static class PdfRenderCompleteness
{
    // Engine messages whose rendering was reviewed against the renderer source on 2026-10-23.
    // Each draws what the file specifies or the fallback ISO 32000 defines, or affects only text
    // extraction. They are matched exactly before the wording rules below, so a repair whose
    // message mentions an omission still publishes.
    private static readonly Dictionary<string, PdfRenderDiagnosticKind> Reviewed = new(StringComparer.Ordinal)
    {
        ["An unreadable ToUnicode map was ignored; embedded font mappings were used."] = PdfRenderDiagnosticKind.Information,
        ["A cyclic page-tree reference was omitted."] = PdfRenderDiagnosticKind.Recovery,
        ["An invalid line-join operation was ignored."] = PdfRenderDiagnosticKind.Recovery,
        ["An invalid text-position operation was ignored."] = PdfRenderDiagnosticKind.Recovery,
        ["An invalid graphics-state miter limit was clamped to one."] = PdfRenderDiagnosticKind.Recovery,
        ["A source ICC profile could not be used; its alternate color space was used."] = PdfRenderDiagnosticKind.Recovery,
        ["The document output profile could not be used for DeviceCMYK color conversion."] = PdfRenderDiagnosticKind.Recovery,
        ["The group ICC profile could not be used; its alternate color space was used."] = PdfRenderDiagnosticKind.Recovery,
        ["The default color profile could not be used for group blending."] = PdfRenderDiagnosticKind.Recovery,
        ["The calibrated group matrix could not be used for blending."] = PdfRenderDiagnosticKind.Recovery,
        ["The group rendering intent could not be used; its blend-profile conversion was used."] = PdfRenderDiagnosticKind.Recovery,
        ["The destination group rendering intent could not be used; its blend-profile conversion was used."] = PdfRenderDiagnosticKind.Recovery,
        ["Black-point compensation could not be applied to this group conversion."] = PdfRenderDiagnosticKind.Recovery,
        ["Black-point compensation could not be applied to this color conversion."] = PdfRenderDiagnosticKind.Recovery,
        ["The black-point compensation policy is invalid; the default policy was used."] = PdfRenderDiagnosticKind.Recovery,
        ["A transfer function array was invalid and was ignored."] = PdfRenderDiagnosticKind.Recovery,
        ["A transfer function could not be read and was ignored."] = PdfRenderDiagnosticKind.Recovery,
        ["Invalid Type 1 font lengths were recovered from the embedded program."] = PdfRenderDiagnosticKind.Recovery,
        ["A comb field value exceeded MaxLen; only the declared cells were rendered."] = PdfRenderDiagnosticKind.Recovery,
    };

    // The renderer names only these graphics-state stroke keys when it drops a malformed one.
    private static readonly HashSet<string> IgnoredStrokeSettings = new(StringComparer.Ordinal)
    {
        "LW", "LC", "LJ", "ML", "D", "SA"
    };

    // Adapt the engine's compatibility messages at one boundary. Unknown diagnostics
    // remain blocking until their rendering consequences have been reviewed.
    internal static IReadOnlyList<PdfRenderDiagnostic> Classify(IReadOnlyList<string> messages) =>
        Array.AsReadOnly(messages.Select(message => new PdfRenderDiagnostic(Kind(message), message)).ToArray());

    private static PdfRenderDiagnosticKind Kind(string message)
    {
        if (Reviewed.TryGetValue(message, out PdfRenderDiagnosticKind reviewed)) return reviewed;
        const string strokePrefix = "An invalid graphics-state /", strokeSuffix = " stroke setting was ignored.";
        if (message.StartsWith(strokePrefix, StringComparison.Ordinal)
            && message.EndsWith(strokeSuffix, StringComparison.Ordinal)
            && message.Length > strokePrefix.Length + strokeSuffix.Length
            && IgnoredStrokeSettings.Contains(message[strokePrefix.Length..^strokeSuffix.Length]))
            return PdfRenderDiagnosticKind.Recovery;
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
            || message == "A supported plain-text form-field appearance was regenerated."
            || message == "Invalid catalog or page-tree links were recovered from intact indirect page objects."
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
