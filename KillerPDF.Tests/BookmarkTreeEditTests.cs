using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #391: dragging a bookmark moves it, with its children, anywhere in the outline.
public sealed class BookmarkTreeEditTests
{
    private static PdfBookmarkInfo Mark(int id, params PdfBookmarkInfo[] children) => new()
    {
        ObjectNumber = id,
        Generation = 0,
        Title = $"B{id}",
        IsOpen = false,
        Style = PdfBookmarkStyle.Regular,
        Children = children
    };

    // A flat picture of the outline: "1(2,3),4".
    private static string Shape(IReadOnlyList<PdfBookmarkInfo> items) => string.Join(",",
        items.Select(item => item.ObjectNumber + (item.Children.Count > 0 ? $"({Shape(item.Children)})" : "")));

    private static readonly PdfBookmarkInfo[] Outline = [Mark(1, Mark(2), Mark(3)), Mark(4), Mark(5)];

    [Theory]
    [InlineData(5, 1, (int)BookmarkDropPlace.Before, "5,1(2,3),4")]
    [InlineData(1, 5, (int)BookmarkDropPlace.After, "4,5,1(2,3)")]
    [InlineData(4, 1, (int)BookmarkDropPlace.Inside, "1(2,3,4),5")]
    [InlineData(4, 1, (int)BookmarkDropPlace.InsideFirst, "1(4,2,3),5")]
    [InlineData(3, 5, (int)BookmarkDropPlace.Before, "1(2),4,3,5")]
    [InlineData(2, 4, (int)BookmarkDropPlace.Inside, "1(3),4(2),5")]
    public void Move_PlacesTheBookmarkWithItsChildren(int moving, int target, int place, string expected)
    {
        var moved = BookmarkTreeEdit.Move(Outline, (moving, 0), (target, 0), (BookmarkDropPlace)place);

        Assert.Equal(expected, Shape(moved));
    }

    [Fact]
    public void Move_OpensTheBranchItDropsInto()
    {
        var moved = BookmarkTreeEdit.Move(Outline, (5, 0), (4, 0), BookmarkDropPlace.Inside);

        Assert.True(moved.Single(item => item.ObjectNumber == 4).IsOpen);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(9, 4)]
    [InlineData(4, 9)]
    public void Move_RefusesItselfItsOwnBranchAndUnknownBookmarks(int moving, int target)
    {
        Assert.False(BookmarkTreeEdit.CanMove(Outline, (moving, 0), (target, 0)));
        Assert.Same(Outline, BookmarkTreeEdit.Move(Outline, (moving, 0), (target, 0), BookmarkDropPlace.Inside));
    }
}
