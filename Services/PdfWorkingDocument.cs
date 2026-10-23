using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Engine-validated serialized working state for one open desktop document.</summary>
internal sealed class PdfWorkingDocument : IDisposable
{
    private readonly byte[] _source;
    private PdfDocument? _parsedDocument;

    private PdfWorkingDocument(byte[] source, bool isReadOnly)
    {
        PdfDocument parsed = PdfDocument.OpenWithCompatibilityRecovery(source);
        _parsedDocument = parsed;
        _source = source;
        PageCount = PdfPageInformation.Read(parsed).Count;
        IsReadOnly = isReadOnly;
    }

    internal int PageCount { get; }
    internal bool IsReadOnly { get; }
    internal PdfDocument? ParsedDocument => _parsedDocument;

    internal void ReleaseParsedDocument(PdfDocument document) =>
        Interlocked.CompareExchange(ref _parsedDocument, null, document);

    internal static PdfWorkingDocument Open(string path, bool isReadOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo? info;
        long ticks = 0, size = -1;
        try
        {
            info = new FileInfo(path);
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            info = null;
        }
        byte[] source = File.ReadAllBytes(path);
        var working = new PdfWorkingDocument(source, isReadOnly);
        bool registered = false;
        if (info is not null && working._parsedDocument is { CanReadPageContent: true } parsed
            && size == source.LongLength)
        {
            try
            {
                info.Refresh();
                if (info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == ticks)
                {
                    PdfPageRenderSession.RegisterDocument(info.FullName, ticks, size, working, parsed);
                    registered = true;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The working document is still usable if the file changes during registration.
            }
        }
        if (!registered) working.Close();
        return working;
    }

    internal void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, _source);
    }

    internal void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(_source);
    }

    /// <summary>Registers a byte-identical copy written by <see cref="Save(string)"/> with the parse
    /// this document already holds, so rendering from the copy does not parse the file again.</summary>
    internal void RegisterCopy(string path)
    {
        if (_parsedDocument is not { CanReadPageContent: true } parsed) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length == _source.LongLength)
                PdfPageRenderSession.RegisterDocument(info.FullName, info.LastWriteTimeUtc.Ticks, info.Length,
                    this, parsed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unregistered copy still renders; it is parsed on first use instead.
        }
    }

    internal void Close() => Interlocked.Exchange(ref _parsedDocument, null);
    public void Dispose() => Close();
}
