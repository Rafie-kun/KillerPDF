using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>One change made in the Comments window (#288), applied when the user saves.</summary>
internal abstract record CommentEdit;

/// <summary>New review text for a comment already in the PDF. Empty text clears it.</summary>
internal sealed record CommentTextEdit(PdfCommentInfo Comment, string Contents) : CommentEdit;

/// <summary>Removes a comment already in the PDF, with the annotation that carries it.</summary>
internal sealed record CommentRemoval(PdfCommentInfo Comment) : CommentEdit;

/// <summary>
/// A new note. <paramref name="ReplyTo"/> is the /NM name of the comment it answers, which may be
/// another new note added earlier in the same save. <paramref name="X"/> and <paramref name="Y"/>
/// are PDF points; null puts the note at the page's top left.
/// </summary>
internal sealed record CommentAddition(int PageIndex, string Contents, string Name, string? ReplyTo,
    double? X = null, double? Y = null) : CommentEdit;
