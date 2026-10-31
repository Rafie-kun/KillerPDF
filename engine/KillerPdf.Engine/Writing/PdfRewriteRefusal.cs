namespace KillerPdf.Engine.Writing;

/// <summary>
/// Recognizes the writer's refusal to rewrite a document whose signatures or Reader usage rights the
/// rewrite would invalidate, so a host can restate how it lets the caller go ahead.
/// </summary>
public static class PdfRewriteRefusal
{
    internal const string SignedMessage =
        "A full rewrite invalidates existing signatures. Set AllowSignatureInvalidation explicitly to proceed.";
    internal const string UsageRightsMessage =
        "A full rewrite invalidates existing signatures and Reader usage rights, so this PDF was not rewritten.";

    private const string Prefix = "A full rewrite invalidates existing signatures";
    private const string EngineSentence = " Set AllowSignatureInvalidation explicitly to proceed.";

    /// <summary>Gets whether an exception is the writer's refusal to invalidate signatures or usage rights.</summary>
    public static bool Matches(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is InvalidOperationException
            && error.Message.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the refusal with the engine's own instruction replaced by one the calling host can act
    /// on, such as the name of a command-line flag or macro setting. The original is kept as the cause.
    /// </summary>
    public static InvalidOperationException Restate(InvalidOperationException error, string howToProceed)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(howToProceed);
        string message = error.Message.EndsWith(EngineSentence, StringComparison.Ordinal)
            ? error.Message[..^EngineSentence.Length] : error.Message;
        return new InvalidOperationException(message + " " + howToProceed, error);
    }
}
