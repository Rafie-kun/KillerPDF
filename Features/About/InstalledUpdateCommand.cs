using System.IO;
using System.Text;

namespace KillerPDF.Features;

internal static class InstalledUpdateCommand
{
    public static string Create(string installer, string installedExecutable, bool machine)
    {
        string installArgument = machine ? "/silent" : "/install-user";
        string directory = Path.GetDirectoryName(Path.GetFullPath(installedExecutable))
            ?? throw new ArgumentException("The installation directory is unavailable.", nameof(installedExecutable));
        string destination = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
        return $"start /wait \"\" \"{installer}\" {installArgument} /install-dir64:{destination}";
    }
}
