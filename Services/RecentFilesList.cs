using System;
using System.Collections.Generic;
using System.Globalization;

namespace KillerPDF.Services;

/// <summary>The recent files list's ordering rule, kept apart from settings storage so it can be tested.</summary>
internal static class RecentFilesList
{
    /// <summary>Returns the list with <paramref name="path"/> moved to the front and capped at <paramref name="max"/>.</summary>
    internal static List<string> With(IEnumerable<string> existing, string path, int max)
    {
        // Compare full paths, not text: C:/a/b.pdf from a script or Git Bash is the same file as C:\a\b.pdf.
        string added = Normalize(path);
        var list = new List<string>(existing);
        list.RemoveAll(p => string.Equals(Normalize(p), added, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, added);
        while (list.Count > max) list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>The date shown next to a recent file, in the current culture's short date order.</summary>
    internal static string DateLabel(DateTime written) => written.ToString("d", CultureInfo.CurrentCulture);

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }   // an unreadable entry still compares as text
    }
}
