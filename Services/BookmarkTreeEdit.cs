using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Where a dragged bookmark lands relative to the bookmark it is dropped on.</summary>
internal enum BookmarkDropPlace
{
    /// <summary>Just above the target, as its previous sibling.</summary>
    Before,
    /// <summary>Just below the target and its children, as its next sibling.</summary>
    After,
    /// <summary>Inside the target, as its last child.</summary>
    Inside,
    /// <summary>Inside the target, as its first child.</summary>
    InsideFirst
}

/// <summary>
/// Moves a bookmark, with its children, anywhere in the outline (#391). Pure tree edits on the
/// engine's bookmark records, so the outline panel's drag and drop and the tests share them.
/// </summary>
internal static class BookmarkTreeEdit
{
    /// <summary>
    /// Returns the outline with <paramref name="moving"/> placed relative to
    /// <paramref name="target"/>. A drop on itself or into its own branch returns the outline
    /// unchanged.
    /// </summary>
    internal static IReadOnlyList<PdfBookmarkInfo> Move(IReadOnlyList<PdfBookmarkInfo> items,
        (int ObjectNumber, int Generation) moving, (int ObjectNumber, int Generation) target,
        BookmarkDropPlace place)
    {
        if (!CanMove(items, moving, target)) return items;
        PdfBookmarkInfo node = Find(items, moving)!;
        return Insert(Remove(items, moving), node, target, place);
    }

    /// <summary>True when <paramref name="moving"/> exists and <paramref name="target"/> is
    /// neither the same bookmark nor one of its descendants.</summary>
    internal static bool CanMove(IReadOnlyList<PdfBookmarkInfo> items,
        (int ObjectNumber, int Generation) moving, (int ObjectNumber, int Generation) target)
    {
        if (moving == target) return false;
        PdfBookmarkInfo? node = Find(items, moving);
        return node is not null && Find(items, target) is not null && Find(node.Children, target) is null;
    }

    private static PdfBookmarkInfo? Find(IReadOnlyList<PdfBookmarkInfo> items, (int ObjectNumber, int Generation) identity)
    {
        foreach (PdfBookmarkInfo item in items)
        {
            if ((item.ObjectNumber, item.Generation) == identity) return item;
            if (Find(item.Children, identity) is { } nested) return nested;
        }
        return null;
    }

    private static IReadOnlyList<PdfBookmarkInfo> Remove(IReadOnlyList<PdfBookmarkInfo> items,
        (int ObjectNumber, int Generation) identity) =>
        [.. items.Where(item => (item.ObjectNumber, item.Generation) != identity)
            .Select(item => item with { Children = Remove(item.Children, identity) })];

    private static IReadOnlyList<PdfBookmarkInfo> Insert(IReadOnlyList<PdfBookmarkInfo> items,
        PdfBookmarkInfo node, (int ObjectNumber, int Generation) target, BookmarkDropPlace place)
    {
        var result = new List<PdfBookmarkInfo>(items.Count + 1);
        foreach (PdfBookmarkInfo item in items)
        {
            if ((item.ObjectNumber, item.Generation) != target)
            {
                result.Add(item with { Children = Insert(item.Children, node, target, place) });
                continue;
            }
            switch (place)
            {
                case BookmarkDropPlace.Before:
                    result.Add(node);
                    result.Add(item);
                    break;
                case BookmarkDropPlace.After:
                    result.Add(item);
                    result.Add(node);
                    break;
                case BookmarkDropPlace.Inside:
                    result.Add(item with { Children = [.. item.Children, node], IsOpen = true });
                    break;
                default:
                    result.Add(item with { Children = [node, .. item.Children], IsOpen = true });
                    break;
            }
        }
        return result;
    }
}
