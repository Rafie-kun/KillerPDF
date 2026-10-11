using System.ComponentModel;
using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;

namespace KillerPDF.Services;

/// <summary>Opens a printer driver's own preferences dialog and returns the settings chosen there.</summary>
internal static partial class PrinterDriverProperties
{
    private const int DmOutBuffer = 2;
    private const int DmInPrompt = 4;
    private const int DmInBuffer = 8;
    private const int IdOk = 1;
    // DEVMODEW keeps its own size and the size of the driver's private data after the
    // 32-character device name and the two version words.
    private const int DmSizeOffset = 68;
    private const int DmDriverExtraOffset = 70;

    /// <summary>Shows the driver dialog seeded with <paramref name="current"/>.</summary>
    /// <returns>The settings chosen in the dialog, or null when it was canceled.</returns>
    internal static PrintTicket? Show(IntPtr ownerWindow, PrintQueue queue, PrintTicket current)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(current);
        string printerName = queue.FullName;
        using var converter = new PrintTicketConverter(printerName, queue.ClientPrintSchemaVersion);
        byte[] input = converter.ConvertPrintTicketToDevMode(current, BaseDevModeType.UserDefault);

        if (!OpenPrinter(printerName, out IntPtr printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        IntPtr inputBuffer = IntPtr.Zero, outputBuffer = IntPtr.Zero;
        try
        {
            int required = DocumentProperties(
                IntPtr.Zero, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (required <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            int size = Math.Max(required, input.Length);
            inputBuffer = Marshal.AllocHGlobal(size);
            outputBuffer = Marshal.AllocHGlobal(size);
            var blank = new byte[size];
            Marshal.Copy(blank, 0, inputBuffer, size);
            Marshal.Copy(blank, 0, outputBuffer, size);
            Marshal.Copy(input, 0, inputBuffer, input.Length);

            int result = DocumentProperties(ownerWindow, printer, printerName,
                outputBuffer, inputBuffer, DmInBuffer | DmInPrompt | DmOutBuffer);
            if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (result != IdOk) return null;

            var output = new byte[size];
            Marshal.Copy(outputBuffer, output, 0, size);
            return converter.ConvertDevModeToPrintTicket(TrimToDevMode(output));
        }
        finally
        {
            if (inputBuffer != IntPtr.Zero) Marshal.FreeHGlobal(inputBuffer);
            if (outputBuffer != IntPtr.Zero) Marshal.FreeHGlobal(outputBuffer);
            ClosePrinter(printer);
        }
    }

    /// <summary>Cuts a buffer down to the DEVMODE it holds: the public part plus the driver's
    /// private data.</summary>
    internal static byte[] TrimToDevMode(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < DmDriverExtraOffset + 2) return buffer;
        int length = BitConverter.ToUInt16(buffer, DmSizeOffset)
            + BitConverter.ToUInt16(buffer, DmDriverExtraOffset);
        return length > 0 && length < buffer.Length ? buffer[..length] : buffer;
    }

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr printer);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int DocumentProperties(IntPtr window, IntPtr printer, string deviceName,
        IntPtr output, IntPtr input, int mode);
}
