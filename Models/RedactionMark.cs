using System.Windows.Media;

namespace KillerPDF.Controls;

internal sealed record RedactionMark(int PageIndex, double Left, double Bottom, double Right, double Top,
    string? OverlayText = null, Color? Fill = null);

internal sealed class RedactionMarkSnapshot(IEnumerable<RedactionMark> marks)
{
    // Records contain only immutable values; the collection itself must also be copied and sealed.
    internal IReadOnlyList<RedactionMark> Marks { get; } = Array.AsReadOnly(marks.ToArray());
}
