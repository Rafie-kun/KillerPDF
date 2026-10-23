using System.IO;

namespace KillerPDF.Features;

/// <summary>
/// A command-line run promises never to show a window. While one owns the process, a crash is
/// written to its console instead of opening the modal crash dialog, which would stall the script
/// that started the run until someone clicked it away.
/// </summary>
internal static class CommandLineCrash
{
    /// <summary>The console of the command-line run that owns the process; null in a desktop session.</summary>
    internal static TextWriter? Console { get; set; }

    /// <summary>Writes the failure for a command-line run. False means a desktop session shows its dialog.</summary>
    internal static bool TryReport(Exception exception, string logPath)
    {
        TextWriter? console = Console;
        if (console is null) return false;
        string message = exception.Message.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        try { console.WriteLine($"KillerPDF stopped unexpectedly: {message} Details: {logPath}"); }
        catch { /* the crash log is still written */ }
        return true;
    }
}
