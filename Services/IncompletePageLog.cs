namespace KillerPDF.Services;

/// <summary>Pages a print or image export kept although some content could not be drawn exactly.
/// The shell names them once the job finishes; the command line refuses such pages unless
/// --allow-incomplete is passed.</summary>
internal sealed class IncompletePageLog
{
    private readonly SortedSet<int> _pages = [];

    internal void Note(int pageIndex, PdfRenderedPage page)
    {
        if (!page.RenderDiagnostics.Any(item => item.BlocksPublication)) return;
        lock (_pages) _pages.Add(pageIndex);
    }

    internal IReadOnlyList<int> Pages
    {
        get { lock (_pages) return [.. _pages]; }
    }

    /// <summary>One-based page numbers with consecutive pages collapsed, such as "2, 5-7, 10".</summary>
    internal static string Describe(IReadOnlyList<int> pageIndexes)
    {
        var parts = new List<string>();
        for (int start = 0; start < pageIndexes.Count;)
        {
            int end = start;
            while (end + 1 < pageIndexes.Count && pageIndexes[end + 1] == pageIndexes[end] + 1) end++;
            string first = (pageIndexes[start] + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            parts.Add(end == start ? first
                : first + "-" + (pageIndexes[end] + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            start = end + 1;
        }
        return string.Join(", ", parts);
    }
}
