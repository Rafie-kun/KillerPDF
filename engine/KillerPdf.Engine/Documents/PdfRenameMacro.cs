using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Plans PDF output names before a macro writes any files.</summary>
public static class PdfRenameMacro
{
    private const string FileNameValue = "macro.fileName";
    private const string IndexValue = "macro.inputIndex";

    /// <summary>Creates the original file-name and one-based input-index context.</summary>
    public static IReadOnlyDictionary<string, string> CreateContext(string inputName, int oneBasedIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oneBasedIndex);
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>
            {
                [FileNameValue] = inputName.Replace('\\', '/').Split('/')[^1],
                [IndexValue] = oneBasedIndex.ToString(CultureInfo.InvariantCulture)
            });
    }

    /// <summary>Returns a validated output name while preserving the PDF data.</summary>
    public static PdfMacroOperationResult Execute(PdfMacroStep step, ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string template = ReadTemplate(step);
        ArgumentNullException.ThrowIfNull(values);
        if (!values.TryGetValue(FileNameValue, out string? currentName)
            || string.IsNullOrWhiteSpace(currentName)
            || !values.TryGetValue(IndexValue, out string? indexText)
            || !int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1)
            throw new ArgumentException("Rename requires a file output context.", nameof(values));
        string name = Format(template, currentName, index);
        return new(source, new Dictionary<string, string> { [FileNameValue] = name })
        {
            OutputFileName = name
        };
    }

    /// <summary>Creates a rename step using {name} and a one-based {index}.</summary>
    public static PdfMacroStep RenameStep(string template)
    {
        ValidateTemplate(template);
        return new(PdfMacroOperation.Rename,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["template"] = template }));
    }

    /// <summary>Resolves every final file name and rejects duplicate targets.</summary>
    public static IReadOnlyList<string> PlanFileNames(PdfMacro macro, IReadOnlyList<string> inputs)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(inputs);
        string[] templates = [.. macro.Steps.Where(step => step.Operation == PdfMacroOperation.Rename)
            .Select(ReadTemplate)];
        string[] outputs = new string[inputs.Count];
        for (int index = 0; index < inputs.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputs[index]);
            string name = inputs[index].Replace('\\', '/').Split('/')[^1];
            foreach (string template in templates) name = Format(template, name, index + 1);
            outputs[index] = name;
        }
        if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length)
            throw new ArgumentException("Macro output names must be unique.", nameof(inputs));
        return Array.AsReadOnly(outputs);
    }

    /// <summary>Validates and reads the file-name template from a rename step.</summary>
    public static string ReadTemplate(PdfMacroStep step)
    {
        if (step.Operation != PdfMacroOperation.Rename || step.Settings is not { Count: 1 }
            || !step.Settings.TryGetValue("template", out string? template))
            throw new ArgumentException("Rename requires only a file name template.", nameof(step));
        ValidateTemplate(template);
        return template;
    }

    private static string Format(string template, string currentName, int index)
    {
        string stem = template.Replace("{name}", Path.GetFileNameWithoutExtension(currentName), StringComparison.Ordinal)
            .Replace("{index}", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        ValidateStem(stem);
        return stem + ".pdf";
    }

    private static void ValidateTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (template.Length > 4096) throw new ArgumentException("The rename template is too long.", nameof(template));
        ValidateStem(template.Replace("{name}", "name", StringComparison.Ordinal)
            .Replace("{index}", "1", StringComparison.Ordinal));
    }

    private static void ValidateStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem) || stem.Length > 251 || stem.EndsWith('.') || stem.EndsWith(' ')
            || stem.Any(character => character < 32 || "<>:\"/\\|?*{}".Contains(character)))
            throw new ArgumentException("The rename template must produce a valid PDF file name.");
        string device = stem.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(device, StringComparer.OrdinalIgnoreCase)
            || (device.Length == 4 && (device[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')
                && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("The rename template produces a reserved device name.");
    }
}
