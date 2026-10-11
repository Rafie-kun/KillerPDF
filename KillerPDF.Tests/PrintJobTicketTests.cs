using System.Printing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintJobTicketTests
{
    [Fact]
    public void Apply_WithoutADriverTicket_WritesEveryWindowSetting()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.ReverseLandscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Monochrome,
            CopyCount = 5
        };
        var letter = new PageMediaSize(PageMediaSizeName.NorthAmericaLetter, 816, 1056);

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(true, true, true, letter, InputBin.Manual),
            copies: 1, fromDriverDialog: false);

        Assert.Equal(1, ticket.CopyCount);
        Assert.Equal(PageOrientation.Landscape, ticket.PageOrientation);
        Assert.Equal(Duplexing.TwoSidedLongEdge, ticket.Duplexing);
        Assert.Equal(OutputColor.Grayscale, ticket.OutputColor);
        Assert.Equal(PageMediaSizeName.NorthAmericaLetter, ticket.PageMediaSize.PageMediaSizeName);
        Assert.Equal(InputBin.Manual, ticket.InputBin);
    }

    [Fact]
    public void Apply_WithADriverTicket_KeepsTheDriverVariantWhereTheWindowAgrees()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.ReverseLandscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Monochrome,
            InputBin = InputBin.Tractor
        };

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(true, true, true, null, null),
            copies: 1, fromDriverDialog: true);

        Assert.Equal(PageOrientation.ReverseLandscape, ticket.PageOrientation);
        Assert.Equal(Duplexing.TwoSidedShortEdge, ticket.Duplexing);
        Assert.Equal(OutputColor.Monochrome, ticket.OutputColor);
        Assert.Equal(InputBin.Tractor, ticket.InputBin);
    }

    [Fact]
    public void Apply_WithADriverTicket_FollowsTheWindowWhereItWasChangedAfterward()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.Landscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Grayscale
        };

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(false, false, false, null, null),
            copies: 1, fromDriverDialog: true);

        Assert.Equal(PageOrientation.Portrait, ticket.PageOrientation);
        Assert.Equal(Duplexing.OneSided, ticket.Duplexing);
        Assert.Equal(OutputColor.Color, ticket.OutputColor);
    }

    [Fact]
    public void Read_ReportsTheWindowSettingsADriverTicketImplies()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.ReverseLandscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Monochrome,
            CopyCount = 3
        };

        PrintJobTicket.DriverChoices choices = PrintJobTicket.Read(ticket);

        Assert.True(choices.Landscape);
        Assert.True(choices.TwoSided);
        Assert.True(choices.Grayscale);
        Assert.Equal(3, choices.Copies);
    }

    [Fact]
    public void Read_IsSilentWhereTheTicketSaysNothing()
    {
        PrintJobTicket.DriverChoices choices = PrintJobTicket.Read(new PrintTicket());

        Assert.Null(choices.Landscape);
        Assert.Null(choices.TwoSided);
        Assert.Null(choices.Grayscale);
        Assert.Null(choices.Copies);
        Assert.Null(choices.Media);
    }

    [Fact]
    public void TrimToDevMode_KeepsThePublicPartAndTheDriverData()
    {
        var buffer = new byte[400];
        BitConverter.GetBytes((ushort)220).CopyTo(buffer, 68);
        BitConverter.GetBytes((ushort)100).CopyTo(buffer, 70);

        Assert.Equal(320, PrinterDriverProperties.TrimToDevMode(buffer).Length);
    }

    [Fact]
    public void TrimToDevMode_LeavesABufferAloneWhenItsSizesDoNotFit()
    {
        var oversized = new byte[100];
        BitConverter.GetBytes((ushort)220).CopyTo(oversized, 68);

        Assert.Same(oversized, PrinterDriverProperties.TrimToDevMode(oversized));
    }
}
