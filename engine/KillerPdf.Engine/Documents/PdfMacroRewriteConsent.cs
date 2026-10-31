using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

// A macro step that rewrites the whole file refuses a signed document or one with Reader usage rights,
// because nothing in a macro run can ask. The step's one setting says the rewrite is wanted.
internal static class PdfMacroRewriteConsent
{
    internal const string Setting = "invalidateSignatures";

    internal static bool Read(PdfMacroStep step)
    {
        if (step.Settings is not { Count: > 0 }) return false;
        if (step.Settings.Count != 1 || !step.Settings.TryGetValue(Setting, out string? value))
            throw new ArgumentException(
                $"The {step.Operation} step accepts only the '{Setting}' setting.", nameof(step));
        if (!bool.TryParse(value, out bool allowed))
            throw new ArgumentException(
                $"The '{Setting}' setting must be true or false.", nameof(step));
        return allowed;
    }

    internal static InvalidOperationException Name(InvalidOperationException error) =>
        PdfRewriteRefusal.Matches(error)
            ? PdfRewriteRefusal.Restate(error, $"Set {Setting} to true on this step to go ahead.")
            : error;
}
