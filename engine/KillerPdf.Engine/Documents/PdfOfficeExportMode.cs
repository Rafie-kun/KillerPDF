namespace KillerPdf.Engine.Documents;

/// <summary>Selects editable Office content or a rendered picture of each PDF page.</summary>
public enum PdfOfficeExportMode
{
    /// <summary>Reconstructs editable text, supported images, and complete ruled tables.</summary>
    Editable,
    /// <summary>Preserves page appearance as pictures; text and cells cannot be edited separately.</summary>
    PageImages
}
