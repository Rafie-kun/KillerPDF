using System.IO;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfMacroStoreTests
{
    [Fact]
    public void SaveLoadAndRemove_RoundTripsOnlyValidatedMacros()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-macros-{Guid.NewGuid():N}");
        try
        {
            var store = new PdfMacroStore(root);
            store.Save(new PdfMacro("Sharing copy", [
                new PdfMacroStep(PdfMacroOperation.Optimize),
                new PdfMacroStep(PdfMacroOperation.Save)]));

            PdfMacro loaded = Assert.Single(store.LoadAll());
            Assert.Equal("Sharing copy", loaded.Name);
            Assert.Equal([PdfMacroOperation.Optimize, PdfMacroOperation.Save],
                loaded.Steps.Select(step => step.Operation));
            store.Remove(loaded.Name);
            Assert.Empty(store.LoadAll());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_SkipsMalformedFileAndReturnsValidMacrosInNameOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-macros-{Guid.NewGuid():N}");
        const string corruptContents = "{ not valid macro json";
        try
        {
            var store = new PdfMacroStore(root);
            store.Save(new PdfMacro("Zebra", [new PdfMacroStep(PdfMacroOperation.Save)]));
            store.Save(new PdfMacro("Alpha", [new PdfMacroStep(PdfMacroOperation.Optimize)]));
            Directory.CreateDirectory(root);
            string corruptFile = Path.Combine(root, "corrupt.json");
            File.WriteAllText(corruptFile, corruptContents);

            IReadOnlyList<PdfMacro> loaded = store.LoadAll();

            Assert.Equal(["Alpha", "Zebra"], loaded.Select(macro => macro.Name));
            Assert.Equal(corruptContents, File.ReadAllText(corruptFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
