using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace KillerPDF.Services;

/// <summary>Raised when an older .doc or .xls file needs Word or Excel and it is not installed.</summary>
internal sealed class OfficeNotInstalledException(string application)
    : InvalidOperationException($"Microsoft {application} is not installed.")
{
    internal string Application { get; } = application;
}

/// <summary>
/// Document to PDF (#74). Word .docx and Excel .xlsx files are converted by KillerPDF itself.
/// The older binary .doc and .xls formats are exported through Word or Excel when installed,
/// in a hidden instance that is closed afterwards. The file's content decides the route, so a
/// .doc that is really a .docx package converts internally.
/// </summary>
internal static class OfficeConversion
{
    internal static readonly string[] Extensions = [".docx", ".doc", ".xlsx", ".xls"];

    internal static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static byte[] ToPdf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        bool spreadsheet = Path.GetExtension(path).StartsWith(".xl", StringComparison.OrdinalIgnoreCase);
        if (bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            using var archive = OfficePackage.Open(bytes);
            string main = OfficePackage.MainPart(archive, spreadsheet ? "xl/workbook.xml" : "word/document.xml");
            if (main.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return XlsxConverter.Convert(bytes);
            if (main.StartsWith("word/", StringComparison.OrdinalIgnoreCase)) return DocxConverter.Convert(bytes);
            throw new InvalidDataException("The file is not a Word or Excel document.");
        }
        return InstalledOffice(path, word: !spreadsheet);
    }

    private static byte[] InstalledOffice(string path, bool word)
    {
        string output = Path.Combine(Path.GetTempPath(), $"KillerPDF-convert-{Guid.NewGuid():N}.pdf");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Export(Path.GetFullPath(path), output, word); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "KillerPDF Office export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try
        {
            if (failure is TargetInvocationException { InnerException: Exception inner }) failure = inner;
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (!File.Exists(output)) throw new IOException("The export did not produce a PDF.");
            return File.ReadAllBytes(output);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Late-bound so KillerPDF needs no Office interop assemblies. An instance that was already
    // open and visible is the user's own: it is left running with its documents.
    private static void Export(string path, string output, bool word)
    {
        string application = word ? "Word" : "Excel";
        Type type = Type.GetTypeFromProgID(word ? "Word.Application" : "Excel.Application")
            ?? throw new OfficeNotInstalledException(application);
        object? app = null, documents = null, document = null;
        bool ours = true;
        try
        {
            try { app = Activator.CreateInstance(type); }
            catch (COMException) { throw new OfficeNotInstalledException(application); }
            if (app is null) throw new OfficeNotInstalledException(application);
            ours = !(Get(app, "Visible") is true);
            if (ours)
            {
                Set(app, "Visible", false);
                Set(app, "DisplayAlerts", word ? (object)0 : false);
            }
            if (word)
            {
                documents = Get(app, "Documents");
                ours &= Get(documents!, "Count") is 0;
                // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument.
                document = Call(documents!, "Open", path, false, true, false, "");
                Call(document!, "ExportAsFixedFormat", output, 17);
                Call(document!, "Close", 0);
            }
            else
            {
                documents = Get(app, "Workbooks");
                ours &= Get(documents!, "Count") is 0;
                // Filename, UpdateLinks, ReadOnly, Format, Password.
                document = Call(documents!, "Open", path, 0, true, Type.Missing, "");
                Call(document!, "ExportAsFixedFormat", 0, output);
                Call(document!, "Close", false);
            }
        }
        finally
        {
            Release(document);
            Release(documents);
            if (app is not null)
            {
                if (ours)
                {
                    try { Call(app, "Quit"); }
                    catch (Exception ex) when (ex is COMException or TargetInvocationException) { }
                }
                Release(app);
            }
        }
    }

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
