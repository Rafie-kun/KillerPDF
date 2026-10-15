using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KillerPDF.PreviewHandler;

// Shell preview handler contracts (shobjidl.h, ocidl.h, oleidl.h). They are public because the
// runtime only exposes COM-visible interfaces through the handler's COM wrapper.

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMessage
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int X;
    public int Y;
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler
{
    void SetWindow(IntPtr window, ref NativeRect rect);
    void SetRect(ref NativeRect rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr window);
    [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
}

[ComImport, Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrame
{
    void GetWindowContext(IntPtr info);
    [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
}

[ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerVisuals
{
    void SetBackgroundColor(uint color);
    void SetFont(IntPtr font);
    void SetTextColor(uint color);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    void Initialize(IStream stream, uint mode);
}

[ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
    [PreserveSig] int GetSite(ref Guid interfaceId, out IntPtr site);
}

[ComImport, Guid("00000114-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindow
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
}
