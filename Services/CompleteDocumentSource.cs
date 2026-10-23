using System.IO;

namespace KillerPDF.Services;

/// <summary>Owns complete document bytes independently of editable copies and external files.</summary>
internal sealed class CompleteDocumentSource
{
    private CompleteDocumentSource(string path) => Path = path;

    internal string Path { get; }

    internal static CompleteDocumentSource Capture(PdfWorkingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Capture(document.Save);
    }

    internal static CompleteDocumentSource Capture(string finishedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finishedPath);
        return Capture(path => File.Copy(finishedPath, path));
    }

    private static CompleteDocumentSource Capture(Action<string> write)
    {
        string path = App.MakeTempFile("complete");
        try
        {
            write(path);
            return new CompleteDocumentSource(path);
        }
        catch
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
