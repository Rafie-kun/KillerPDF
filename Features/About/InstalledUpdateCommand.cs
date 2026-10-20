using System.IO;
using System.Text;

namespace KillerPDF.Features;

internal static class InstalledUpdateCommand
{
    /// <summary>
    /// The arguments for running the downloaded installer directly: the install scope, the
    /// current installation folder, and a request to start the new copy afterward. The
    /// installer closes the running copy itself, so nothing waits on this side.
    /// </summary>
    public static string Arguments(string installedExecutable, bool machine, string? reopenFile = null)
    {
        string installArgument = machine ? "/silent" : "/install-user";
        string directory = Path.GetDirectoryName(Path.GetFullPath(installedExecutable))
            ?? throw new ArgumentException("The installation directory is unavailable.", nameof(installedExecutable));
        string destination = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
        return $"{installArgument} /install-dir64:{destination} " + KillerLauncher.UpdateRelaunchArguments.Encode(reopenFile);
    }
}
