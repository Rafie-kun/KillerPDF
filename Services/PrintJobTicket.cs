using System.Printing;

namespace KillerPDF.Services;

/// <summary>Combines the print window's own settings with a ticket from the printer driver's
/// dialog.</summary>
internal static class PrintJobTicket
{
    /// <summary>The settings the print window has controls for.</summary>
    internal readonly record struct Settings(
        bool Landscape, bool TwoSided, bool Grayscale, PageMediaSize? Media, InputBin? Source);

    /// <summary>What a driver ticket says about those same settings; null where it is silent.</summary>
    internal readonly record struct DriverChoices(
        bool? Landscape, bool? TwoSided, bool? Grayscale, int? Copies, PageMediaSize? Media);

    /// <summary>Writes the print window's settings onto a ticket. A ticket that came from the
    /// driver dialog keeps the driver's exact variant (reverse landscape, short-edge binding,
    /// monochrome) wherever it already agrees with the window.</summary>
    internal static void Apply(PrintTicket ticket, Settings settings, int copies, bool fromDriverDialog)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ticket.CopyCount = copies;
        if (!fromDriverDialog || IsLandscape(ticket.PageOrientation) != settings.Landscape)
            ticket.PageOrientation = settings.Landscape
                ? PageOrientation.Landscape : PageOrientation.Portrait;
        if (!fromDriverDialog || IsTwoSided(ticket.Duplexing) != settings.TwoSided)
            ticket.Duplexing = settings.TwoSided ? Duplexing.TwoSidedLongEdge : Duplexing.OneSided;
        if (!fromDriverDialog || IsGrayscale(ticket.OutputColor) != settings.Grayscale)
            ticket.OutputColor = settings.Grayscale ? OutputColor.Grayscale : OutputColor.Color;
        if (settings.Media is not null) ticket.PageMediaSize = settings.Media;
        if (settings.Source is { } source) ticket.InputBin = source;
    }

    /// <summary>Reads the settings a driver ticket implies for the print window's controls.</summary>
    internal static DriverChoices Read(PrintTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return new DriverChoices(
            ticket.PageOrientation is { } orientation && orientation != PageOrientation.Unknown
                ? IsLandscape(orientation) : null,
            ticket.Duplexing is { } duplexing && duplexing != Duplexing.Unknown
                ? IsTwoSided(duplexing) : null,
            ticket.OutputColor is { } color && color != OutputColor.Unknown
                ? IsGrayscale(color) : null,
            ticket.CopyCount is { } copies && copies >= 1 ? copies : null,
            ticket.PageMediaSize);
    }

    private static bool IsLandscape(PageOrientation? orientation) =>
        orientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;

    private static bool IsTwoSided(Duplexing? duplexing) =>
        duplexing is Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge;

    private static bool IsGrayscale(OutputColor? color) =>
        color is OutputColor.Grayscale or OutputColor.Monochrome;
}
