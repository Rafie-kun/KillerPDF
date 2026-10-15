using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NTwain;
using NTwain.Data;

namespace KillerPDF.Services;

/// <summary>
/// Scan to PDF (#73): one TWAIN session on the window's message loop. Each page the scanner
/// hands over is saved as an image, gray and black-and-white pages as PNG and color pages as
/// JPEG, at the resolution the scanner reports, ready to be merged into a PDF.
/// </summary>
internal sealed class TwainScan : IDisposable
{
    private readonly TwainSession _session;
    private readonly IntPtr _window;

    /// <summary>False when the 64-bit TWAIN Data Source Manager (TWAINDSM.dll) is not installed.</summary>
    internal static bool ManagerInstalled => PlatformInfo.Current.IsSupported;

    private TwainScan(TwainSession session, IntPtr window)
    {
        _session = session;
        _window = window;
    }

    internal static TwainScan Open(IntPtr window)
    {
        var session = new TwainSession(TWIdentity.CreateFromAssembly(DataGroups.Image, typeof(TwainScan).Assembly));
        ReturnCode code = session.Open(new WpfMessageLoopHook(window));
        if (code != ReturnCode.Success)
            throw new InvalidOperationException($"The TWAIN manager did not open ({code}).");
        return new TwainScan(session, window);
    }

    internal IReadOnlyList<DataSource> Sources => [.. _session.GetSources()];

    /// <summary>
    /// Scans from <paramref name="source"/> until the scanner is done or the user closes its
    /// window, and returns the saved page images in order.
    /// </summary>
    internal Task<IReadOnlyList<string>> ScanAsync(DataSource source, bool showSettings, string folder)
    {
        Directory.CreateDirectory(folder);
        var pages = new List<string>();
        var done = new TaskCompletionSource<IReadOnlyList<string>>();
        Exception? failure = null;

        void Transferred(object? sender, DataTransferredEventArgs e)
        {
            try
            {
                using Stream? image = e.GetNativeImageStream();
                if (image is not null) pages.Add(SavePage(image, folder, pages.Count + 1));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
                or ArgumentException or FileFormatException)
            {
                failure ??= ex;
            }
        }
        void Failed(object? sender, TransferErrorEventArgs e) =>
            failure ??= e.Exception ?? new InvalidOperationException($"The scanner reported an error ({e.ReturnCode}).");
        void Disabled(object? sender, EventArgs e)
        {
            _session.DataTransferred -= Transferred;
            _session.TransferError -= Failed;
            _session.SourceDisabled -= Disabled;
            source.Close();
            // Pages that did arrive are kept even when a later page failed.
            if (failure is not null && pages.Count == 0) done.TrySetException(failure);
            else done.TrySetResult(pages);
        }

        _session.DataTransferred += Transferred;
        _session.TransferError += Failed;
        _session.SourceDisabled += Disabled;
        ReturnCode code = source.Open();
        if (code == ReturnCode.Success)
            code = source.Enable(showSettings ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI, false, _window);
        if (code != ReturnCode.Success)
        {
            _session.DataTransferred -= Transferred;
            _session.TransferError -= Failed;
            _session.SourceDisabled -= Disabled;
            source.Close();
            done.TrySetException(new InvalidOperationException($"The scanner did not start ({code})."));
        }
        return done.Task;
    }

    // Gray and black-and-white pages compress best and stay exact as PNG; color pages are JPEG.
    private static string SavePage(Stream image, string folder, int number)
    {
        BitmapFrame frame = BitmapDecoder.Create(image, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        PixelFormat format = frame.Format;
        bool gray = format == PixelFormats.BlackWhite || format == PixelFormats.Gray2
            || format == PixelFormats.Gray4 || format == PixelFormats.Gray8 || format == PixelFormats.Gray16
            || format == PixelFormats.Indexed1 && IsGrayPalette(frame.Palette)
            || format == PixelFormats.Indexed8 && IsGrayPalette(frame.Palette);
        BitmapEncoder encoder = gray ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(frame);
        string path = Path.Combine(folder, $"scan-{number:000}{(gray ? ".png" : ".jpg")}");
        using FileStream output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    private static bool IsGrayPalette(BitmapPalette? palette) =>
        palette is not null && palette.Colors.All(color => color.R == color.G && color.G == color.B);

    public void Dispose()
    {
        try { _session.Close(); }
        catch (Exception ex) when (ex is InvalidOperationException or TwainStateException) { }
    }
}
