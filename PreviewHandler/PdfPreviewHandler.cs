using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KillerPDF.PreviewHandler;

/// <summary>
/// PDF previews for File Explorer and Outlook (#410). The shell hands the file over as a stream,
/// so attachments preview the same as files on disk, and the pane renders it with the KillerPDF
/// engine. It runs in its own prevhost.exe surrogate, registered by the installed edition.
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("KillerPDF.PreviewHandler")]
public sealed partial class PdfPreviewHandler : IPreviewHandler, IPreviewHandlerVisuals, IInitializeWithStream,
    IObjectWithSite, IOleWindow
{
    /// <summary>Matches PreviewHandlerRegistration.ClassId in the app.</summary>
    public const string ClassId = "0EB8F20D-6E18-4B80-9CDF-FF663C52B5BD";

    private const int Ok = 0;
    private const int False = 1;
    private const int Fail = unchecked((int)0x80004005);
    private const int NotImplemented = unchecked((int)0x80004001);

    // Larger files are not read into the preview; they still open in KillerPDF.
    private const long MaximumBytes = 512L * 1024 * 1024;

    private byte[]? _data;
    private string _name = "Preview.pdf";
    private IntPtr _parent;
    private NativeRect _bounds;
    private object? _site;
    private IPreviewHandlerFrame? _frame;
    private PreviewPane? _pane;
    private uint? _background;
    private uint? _text;

    public void Initialize(IStream stream, uint mode)
    {
        _data = null;
        try
        {
            stream.Stat(out STATSTG stat, 0);
            if (!string.IsNullOrWhiteSpace(stat.pwcsName)) _name = Path.GetFileName(stat.pwcsName);
            if (stat.cbSize > MaximumBytes) throw new InvalidDataException("The file is too large to preview.");
            using var buffer = new MemoryStream(stat.cbSize > 0 ? (int)stat.cbSize : 1 << 20);
            byte[] chunk = new byte[1 << 20];
            IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(chunk, chunk.Length, read);
                    int count = Marshal.ReadInt32(read);
                    if (count <= 0) break;
                    if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("The file is too large to preview.");
                    buffer.Write(chunk, 0, count);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(read);
            }
            _data = buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            // Too large: the pane says it cannot preview the file.
            _data = null;
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    public void SetWindow(IntPtr window, ref NativeRect rect)
    {
        _parent = window;
        _bounds = rect;
        _pane?.Reparent(window, rect);
    }

    public void SetRect(ref NativeRect rect)
    {
        _bounds = rect;
        _pane?.Resize(rect);
    }

    public void DoPreview()
    {
        _pane?.Dispose();
        _pane = new PreviewPane(_parent, _bounds, _data ?? [], _name, _background, _text);
    }

    public void Unload()
    {
        _pane?.Dispose();
        _pane = null;
        _data = null;
    }

    public void SetFocus() => _pane?.FocusFirst();

    public void QueryFocus(out IntPtr window) => window = GetFocus();

    public int TranslateAccelerator(ref NativeMessage message) =>
        _frame?.TranslateAccelerator(ref message) ?? False;

    public void SetBackgroundColor(uint color) => _background = color;

    public void SetFont(IntPtr font)
    {
    }

    public void SetTextColor(uint color) => _text = color;

    public void SetSite(object? site)
    {
        _site = site;
        _frame = site as IPreviewHandlerFrame;
    }

    public int GetSite(ref Guid interfaceId, out IntPtr site)
    {
        site = IntPtr.Zero;
        if (_site is null) return Fail;
        IntPtr unknown = Marshal.GetIUnknownForObject(_site);
        try { return Marshal.QueryInterface(unknown, in interfaceId, out site); }
        finally { Marshal.Release(unknown); }
    }

    public int GetWindow(out IntPtr window)
    {
        window = _pane?.Handle ?? _parent;
        return window == IntPtr.Zero ? Fail : Ok;
    }

    public int ContextSensitiveHelp(bool enterMode) => NotImplemented;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetFocus();
}
