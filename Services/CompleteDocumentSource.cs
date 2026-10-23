using System.IO;

namespace KillerPDF.Services;

/// <summary>The working copy of an opened PDF could not be written, usually because the temp folder is
/// full or unavailable. Opening reports that instead of offering to repair an undamaged file.</summary>
internal sealed class WorkingCopyUnavailableException(Exception innerException)
    : IOException(innerException.Message, innerException);

/// <summary>Owns complete document bytes independently of editable copies and external files.</summary>
internal sealed class CompleteDocumentSource
{
    private CompleteDocumentSource(string path) => Path = path;

    internal string Path { get; }

    internal static CompleteDocumentSource Capture(PdfWorkingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CompleteDocumentSource source = Capture(document.Save);
        // The copy holds the same bytes, so the viewer shares the parse made when the file opened.
        document.RegisterCopy(source.Path);
        return source;
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
