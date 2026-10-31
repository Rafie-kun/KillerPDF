namespace KillerPdf.Engine.Syntax;

/// <summary>The file holds an encryption dictionary its trailer no longer names, and its pages cannot be read.</summary>
public sealed class PdfLostEncryptionException()
    : PdfSyntaxException("The file holds an encryption dictionary but its trailer has no /Encrypt entry, and its pages cannot be read", 0);
