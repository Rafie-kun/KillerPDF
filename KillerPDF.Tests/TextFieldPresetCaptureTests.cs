using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TextFieldPresetCaptureTests
{
    [Theory]
    [InlineData("/Helv 12 Tf 0 0 1 rg", 12, 0, 0, 1)]
    [InlineData("/Helv 0 Tf 0.5 g", 0, 0.5, 0.5, 0.5)]
    [InlineData("0 1 0 0 k /Cour 9.5 Tf", 9.5, 1, 0, 1)]
    [InlineData("", 0, 0, 0, 0)]
    public void ParseDefaultAppearance_ReadsFontSizeAndTextColor(
        string appearance, double size, double red, double green, double blue)
    {
        (double fontSize, PdfRgbColor color) = TextFieldPresetCapture.ParseDefaultAppearance(appearance);
        Assert.Equal(size, fontSize, 6);
        Assert.Equal(red, color.Red, 6);
        Assert.Equal(green, color.Green, 6);
        Assert.Equal(blue, color.Blue, 6);
    }

    [Fact]
    public void NextName_SkipsNamesAlreadyUsed() =>
        Assert.Equal("Preset 3", TextFieldPresetCapture.NextName("Preset {0}", ["preset 1", "Preset 2"]));

    [Fact]
    public void ReadTextFieldPreset_ReturnsTheStyleAFieldWasCreatedWith()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-field-preset-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());
            var preset = new PdfTextFieldPreset("Answer", 180, 24, 11, new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(1, 1, 0.8),
                BorderColor = new PdfRgbColor(0, 0, 1),
                TextColor = new PdfRgbColor(0.2, 0.2, 0.2),
                BorderWidth = 2,
                BorderStyle = PdfFormFieldBorderStyle.Dashed,
                DashPattern = [4, 2]
            });
            string name = PdfEngineIntegration.AddTextField(path, 0, 50, 600, preset.Width, preset.Height, preset);

            PdfTextFieldPreset copy = PdfEngineIntegration.ReadTextFieldPreset(path, name, "Copy");

            Assert.Equal("Copy", copy.Name);
            Assert.Equal(180, copy.Width, 3);
            Assert.Equal(24, copy.Height, 3);
            Assert.Equal(11, copy.FontSize, 3);
            Assert.Equal(new PdfRgbColor(1, 1, 0.8), copy.AppearanceStyle.BackgroundColor);
            Assert.Equal(new PdfRgbColor(0, 0, 1), copy.AppearanceStyle.BorderColor);
            Assert.Equal(0.2, copy.AppearanceStyle.TextColor.Red, 3);
            Assert.Equal(2, copy.AppearanceStyle.BorderWidth, 3);
            Assert.Equal(PdfFormFieldBorderStyle.Dashed, copy.AppearanceStyle.BorderStyle);
            Assert.Equal(new[] { 4.0, 2.0 }, copy.AppearanceStyle.DashPattern!);
            Assert.Equal(14, PdfEngineIntegration.ReadTextFieldPreset(path, name, "Bigger", 14).FontSize, 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
