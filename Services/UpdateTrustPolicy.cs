namespace KillerPDF.Services;

/// <summary>
/// Decides whether a downloaded update may replace the running application. A signed
/// application accepts only an update signed by the same certificate, or by a renewed
/// certificate issued to the same subject. An unsigned development build accepts any
/// valid signature, as before, so local testing of the update flow keeps working.
/// </summary>
internal static class UpdateTrustPolicy
{
    internal static bool Accepts(
        (bool Valid, string Subject, string Thumbprint) update,
        (bool Valid, string Subject, string Thumbprint) running)
    {
        if (!update.Valid) return false;
        if (!running.Valid) return true;
        if (!string.IsNullOrEmpty(running.Thumbprint)
            && string.Equals(update.Thumbprint, running.Thumbprint, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(running.Subject)
            && string.Equals(update.Subject, running.Subject, StringComparison.Ordinal);
    }
}
