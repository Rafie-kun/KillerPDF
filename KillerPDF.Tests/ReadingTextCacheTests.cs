using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReadingTextCacheTests
{
    // Every edit used to make the screen-reader text model read and parse the whole file again on
    // the UI thread. The structure order, comments and crop boxes depend only on the working file.
    [Fact]
    public void DocumentFactsAreReadAgainOnlyWhenTheWorkingFileChanges()
    {
        string source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Controls", "Viewer", "PdfViewer.Reading.cs"));
        string facts = source[source.IndexOf("private void ReadDocumentFacts(", StringComparison.Ordinal)..];
        Assert.Contains("if (Equals(facts, _factsVersion)) return;", facts[..facts.IndexOf("PdfDocument.Open", StringComparison.Ordinal)]);
        string invalidate = source[source.IndexOf("internal void InvalidateText()", StringComparison.Ordinal)..];
        invalidate = invalidate[..invalidate.IndexOf("TextChanged?.Invoke();", StringComparison.Ordinal)];
        Assert.DoesNotContain("_structureOrder", invalidate);
        Assert.DoesNotContain("_factsVersion", invalidate);
    }

    // Screen readers ask for the visible ranges often; mapping each character to the screen walked
    // the visual tree twice per character.
    [Fact]
    public void VisibleRangesMapCharactersWithOneTransformPerPage()
    {
        string source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Controls", "Viewer", "PdfViewer.Reading.cs"));
        string method = source[source.IndexOf("public IReadOnlyList<(int Page, int Start, int End)> VisibleCaretRanges()", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("public (int Page, int Caret)? CaretFromScreenPoint(", StringComparison.Ordinal)];
        string loop = method[method.IndexOf("for (int caret = 0;", StringComparison.Ordinal)..];
        Assert.DoesNotContain("PointToScreen(", loop);
        Assert.Contains("Rect.Transform(rect, toScreen)", loop);
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
