using System.IO;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WatchedFolderReadinessTests
{
    [Fact]
    public void UnavailableModelsRejectConfigurationWithoutReplacingTheSavedSetup()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        service.Configure(fixture.Options);
        byte[] saved = File.ReadAllBytes(fixture.Journal);
        File.Delete(fixture.Model);
        var issue = service.InspectReadiness(fixture.Macro);
        Assert.NotNull(issue);
        Assert.Equal(0, issue.StepIndex);
        Assert.Equal(["deu"], issue.MissingOcrLanguages);
        Assert.Equal("OcrModelsMissing", Assert.Throws<InvalidOperationException>(() => service.Configure(fixture.Options)).Message);
        Assert.True(service.GetSnapshot().IsPaused);
        Assert.Equal(saved, File.ReadAllBytes(fixture.Journal));
        Assert.Empty(service.GetSnapshot().Jobs);
    }

    [Fact]
    public void ResumeRechecksModelsAndCanResumeAfterTheyAreRestored()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        service.Configure(fixture.Options);
        File.Delete(fixture.Model);
        Assert.Equal("OcrModelsMissing", Assert.Throws<InvalidOperationException>(service.Resume).Message);
        Assert.True(service.GetSnapshot().IsPaused);
        File.WriteAllBytes(fixture.Model, [1]);
        service.Resume();
        Assert.False(service.GetSnapshot().IsPaused);
        service.Pause();
    }

    [Fact]
    public async Task RestartedRunningSetupPausesBeforeScanningWhenModelsDisappear()
    {
        using var fixture = new Fixture();
        using (var initial = fixture.Create())
        {
            initial.Configure(fixture.Options);
            initial.Resume();
        }
        File.Delete(fixture.Model);
        using (var restarted = fixture.Create())
        {
            await restarted.ReconcileAsync();
            Assert.True(restarted.GetSnapshot().IsPaused);
            Assert.Equal("OcrModelsMissing", restarted.GetSnapshot().ErrorCode);
            Assert.Empty(restarted.GetSnapshot().Jobs);
            Assert.Empty(Directory.GetFiles(fixture.Output));
            Assert.Equal("OcrModelsMissing", Assert.Throws<InvalidOperationException>(restarted.RetryFailed).Message);
        }
        using var recovered = fixture.Create();
        Assert.Null(recovered.GetSnapshot().ErrorCode);
        Assert.True(recovered.GetSnapshot().IsPaused);
        Assert.NotNull(recovered.GetSnapshot().Options);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "killerpdf-watch-readiness-" + Guid.NewGuid().ToString("N"));
        private string Input => Path.Combine(_root, "input");
        public string Output => Path.Combine(_root, "output");
        private string Models => Path.Combine(_root, "models");
        public string Model => Path.Combine(Models, "deu.traineddata");
        private string Storage => Path.Combine(_root, "state");
        public string Journal => Path.Combine(Storage, "queue.json");
        public PdfMacro Macro { get; } = new("OCR readiness", [new(PdfMacroOperation.Ocr,
            Settings: new Dictionary<string, string> { ["language"] = "deu" })]);
        public WatchedFolderOptions Options => new(Input, Output, "*.pdf", Macro.ToJson());

        public Fixture()
        {
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Models);
            File.WriteAllBytes(Model, [1]);
        }

        public WatchedFolderService Create() => new(Storage,
            new PdfMacroDispatcher([new(PdfMacroOperation.Ocr, (_, _, _, _) =>
                throw new InvalidOperationException("The readiness fixture must never execute OCR."))]),
            readiness: macro =>
            {
                string language = macro.Steps[0].Settings!["language"];
                var missing = OcrModelFiles.MissingForCommonBackend(Models, language.Split('+'));
                return missing.Count == 0 ? null : new(0, missing);
            });

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
