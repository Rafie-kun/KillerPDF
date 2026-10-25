using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KillerPDF.LatencySmoke;

internal static class ForegroundCaptureProbe
{
    internal static int Run()
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("An unlocked interactive test desktop is required.");
        using var measured = new Form
        {
            Text = "KillerPDF foreground capture target",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(40, 40, 640, 400),
            BackColor = Color.White,
            ShowInTaskbar = false
        };
        using var other = new Form
        {
            Text = "KillerPDF owned foreground interruption",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(40, 40, 640, 400),
            BackColor = Color.Blue,
            ShowInTaskbar = false,
            TopMost = true
        };
        measured.Show();
        Application.DoEvents();
        other.Show();
        Native.SetForegroundWindow(other.Handle);
        Application.DoEvents();
        if (Native.GetForegroundWindow() != other.Handle)
            throw new InvalidOperationException("The owned interruption did not acquire foreground.");
        var capture = new FrameCapture(measured.Handle);
        try
        {
            capture.WaitForStable(Stopwatch.StartNew(), 2000, requireChange: false);
            throw new InvalidOperationException("A background window was accepted as a valid capture.");
        }
        catch (TimeoutException error) when (error.Message == "Measured window lost foreground; capture rejected.")
        {
            Console.WriteLine("PASS a background window cannot produce an accepted measurement frame.");
            return 0;
        }
    }
}
