using System;
using System.Collections.Generic;

namespace KillerPDF.Services;

internal static class PageAnnotationInsertion
{
    internal static void Shift(
        Dictionary<int, List<PageAnnotation>> annotations, int insertionIndex, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentOutOfRangeException.ThrowIfNegative(insertionIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        if (pageCount == 0) return;

        var shifted = new Dictionary<int, List<PageAnnotation>>();
        foreach (var pair in annotations)
        {
            int page = pair.Key >= insertionIndex ? pair.Key + pageCount : pair.Key;
            foreach (PageAnnotation annotation in pair.Value) annotation.PageIndex = page;
            shifted[page] = pair.Value;
        }
        annotations.Clear();
        foreach (var pair in shifted) annotations[pair.Key] = pair.Value;
    }
}
