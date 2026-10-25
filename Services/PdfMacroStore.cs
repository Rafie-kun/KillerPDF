using System.IO;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Stores validated PDF macros without allowing executable content.</summary>
internal sealed class PdfMacroStore
{
    private readonly string _directory;

    internal PdfMacroStore() : this(AppDataPaths.MacrosDirectory) { }

    internal PdfMacroStore(string directory) => _directory = directory;

    internal IReadOnlyList<PdfMacro> LoadAll()
    {
        if (!Directory.Exists(_directory)) return [];
        var macros = new List<PdfMacro>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                macros.Add(PdfMacro.FromJson(File.ReadAllText(file)));
            }
            catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
            {
                // Leave the file in place so it can be inspected or repaired.
            }
        }
        return [.. macros.OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal void Save(PdfMacro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, FileName(macro.Name));
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, macro.ToJson(indented: true));
        File.Move(temporary, file, overwrite: true);
    }

    internal void Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string file = Path.Combine(_directory, FileName(name));
        if (File.Exists(file)) File.Delete(file);
    }

    private static string FileName(string name) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(name)) + ".json";
}
