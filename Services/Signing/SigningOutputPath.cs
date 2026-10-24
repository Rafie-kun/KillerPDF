using System.IO;

namespace KillerPDF.Services.Signing;

internal static class SigningOutputPath
{
    internal static string Create(string suggestedSource, bool cleanupOwned,
        string downloadsDirectory, string? suggestedName)
    {
        string directory = cleanupOwned || !Path.IsPathFullyQualified(suggestedSource)
            ? downloadsDirectory : Path.GetDirectoryName(suggestedSource) ?? downloadsDirectory;
        string name = Path.GetFileNameWithoutExtension(cleanupOwned ? suggestedName ?? "document.pdf" : suggestedSource);
        return Path.Combine(directory, name + "-signed.pdf");
    }
}
