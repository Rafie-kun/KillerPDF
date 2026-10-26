namespace KillerPdf.Engine.Writing;

/// <summary>The source identity condition that permits null-reference normalization.</summary>
public enum PdfReferenceRepairReason
{
    /// <summary>No current cross-reference entry identifies the target.</summary>
    Missing,
    /// <summary>The current target entry is free or null.</summary>
    Free,
    /// <summary>The reference generation differs from the active identity.</summary>
    StaleGeneration
}

/// <summary>The output change made for a source-null reference.</summary>
public enum PdfReferenceRepairAction
{
    /// <summary>An optional dictionary entry was omitted.</summary>
    RemoveDictionaryEntry,
    /// <summary>An array element or indirect body was replaced by PDF null.</summary>
    ReplaceWithNull
}

/// <summary>An immutable reference repair, with a null owner identifying the trailer.</summary>
/// <param name="OwnerObjectNumber">Owning object number, or null for the trailer.</param>
/// <param name="OwnerGeneration">Owning generation, or null for the trailer.</param>
/// <param name="Path">The container path within that owner.</param>
/// <param name="TargetObjectNumber">The reference's target object number.</param>
/// <param name="TargetGeneration">The reference's target generation.</param>
/// <param name="Reason">Why the source reference has null semantics.</param>
/// <param name="Action">How the reference was represented in the output.</param>
public sealed record PdfReferenceRepair(int? OwnerObjectNumber, int? OwnerGeneration,
    string Path, int TargetObjectNumber, int TargetGeneration,
    PdfReferenceRepairReason Reason, PdfReferenceRepairAction Action);

/// <summary>A rewritten PDF and immutable records of reference normalization.</summary>
public sealed class PdfDocumentWriteResult
{
    internal PdfDocumentWriteResult(byte[] data, IEnumerable<PdfReferenceRepair> repairs)
    {
        Bytes = data;
        Data = data;
        ReferenceRepairs = Array.AsReadOnly(repairs.ToArray());
    }

    /// <summary>Gets the serialized output PDF.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the replacements made during this output transaction.</summary>
    public IReadOnlyList<PdfReferenceRepair> ReferenceRepairs { get; }

    internal byte[] Bytes { get; }
}
