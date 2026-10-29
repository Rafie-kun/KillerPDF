using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// Every CSV KillerPDF writes can carry text from a PDF or a file name, so a value a spreadsheet would
// run as a formula is written as text instead, including fullwidth signs and invisible prefixes.
public sealed class PdfCsvTests
{
    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+1")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1)")]
    [InlineData("  =HYPERLINK(\"x\")")]
    [InlineData("\t=1")]
    [InlineData("＝1+1")]
    [InlineData("​=1")]
    [InlineData("﻿@x")]
    public void FormulaLookingValuesAreWrittenAsText(string value)
    {
        Assert.True(PdfCsv.StartsFormula(value));
        Assert.StartsWith("\"'", PdfCsv.Field(value, alwaysQuote: true), StringComparison.Ordinal);
        Assert.StartsWith("'", PdfCsv.Field(value, alwaysQuote: false).TrimStart('"'), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "plain", "\"plain\"")]
    [InlineData("a,b", "\"a,b\"", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"", "\"say \"\"hi\"\"\"")]
    [InlineData("", "", "\"\"")]
    public void OrdinaryValuesAreOnlyQuotedWhenNeeded(string value, string minimal, string quoted)
    {
        Assert.False(PdfCsv.StartsFormula(value));
        Assert.Equal(minimal, PdfCsv.Field(value, alwaysQuote: false));
        Assert.Equal(quoted, PdfCsv.Field(value, alwaysQuote: true));
    }

    [Fact]
    public void LoneSurrogatesDoNotBreakTheFormulaCheck()
    {
        // A file name may carry an unpaired surrogate, which Unicode normalization rejects. The values are
        // built here because theory data cannot carry a lone surrogate intact.
        string lone = ((char)0xD800).ToString();

        Assert.False(PdfCsv.StartsFormula(lone + "=x"));
        Assert.True(PdfCsv.StartsFormula("=" + lone));
    }

    [Fact]
    public void MeasurementReportsGuardCommentsAndDocumentNames()
    {
        var result = new PdfMeasurementResult
        {
            Document = "=cmd|' /C calc'!A0.pdf",
            PageIndex = 0,
            Kind = "Distance",
            Comment = "@SUM(1+1)*cmd|' /C calc'!A0",
            Unit = "in",
            Profile = "Default",
            Points = []
        };

        string csv = PdfMeasurementReport.ToCsv([result]);

        Assert.Contains("\"'=cmd|", csv, StringComparison.Ordinal);
        Assert.Contains("\"'@SUM(1+1)", csv, StringComparison.Ordinal);
    }
}
