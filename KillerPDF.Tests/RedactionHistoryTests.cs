using KillerPDF.Controls;
using System.Windows.Media;
using Xunit;

namespace KillerPDF.Tests;

public sealed class RedactionHistoryTests
{
    [Fact]
    public void DocumentHistoryKeepsAnImmutableCopyOfPendingMarkProperties()
    {
        var mark = new RedactionMark(2, 10, 20, 30, 40, "Private", Colors.Red);
        var pending = new List<RedactionMark> { mark };
        var undo = new UndoEntry(UndoKind.Document, RedactionSnapshot: new(pending));
        pending[0] = mark with { PageIndex = 0, OverlayText = "Changed", Fill = Colors.Blue };
        pending.Clear();

        Assert.Equal(mark, Assert.Single(undo.RedactionSnapshot!.Marks));
        Assert.Throws<NotSupportedException>(() => ((IList<RedactionMark>)undo.RedactionSnapshot.Marks).Clear());
    }

    [Fact]
    public void UndoAndRedoMarkCopiesRemainIndependentAfterApplyClearsLiveMarks()
    {
        var pending = new List<RedactionMark> { new(0, 10, 20, 30, 40), new(2, 50, 60, 70, 80, "Code") };
        var before = new UndoEntry(UndoKind.Document, RedactionSnapshot: new(pending));
        pending.Clear();
        var inverse = new UndoEntry(UndoKind.Document, RedactionSnapshot: new(pending));
        pending.AddRange(before.RedactionSnapshot!.Marks);
        Assert.Equal(2, pending.Count);
        pending[0] = pending[0] with { Left = 0 };
        Assert.Equal(10, before.RedactionSnapshot.Marks[0].Left);
        pending.Clear();
        pending.AddRange(inverse.RedactionSnapshot!.Marks);
        Assert.Empty(pending);
        Assert.Equal(2, before.RedactionSnapshot.Marks.Count);
    }
}
