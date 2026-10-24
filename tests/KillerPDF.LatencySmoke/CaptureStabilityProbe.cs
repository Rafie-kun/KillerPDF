using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KillerPDF.LatencySmoke;

internal static class CaptureStabilityProbe
{
    internal static int Run()
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("An unlocked interactive test desktop is required.");

        using var window = new Form
        {
            Text = "KillerPDF capture stability probe",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(40, 40, 640, 400),
            BackColor = Color.White,
            ShowInTaskbar = false,
            TopMost = true
        };
        window.Show();
        Application.DoEvents();
        var capture = new FrameCapture(window.Handle);
        var clock = Stopwatch.StartNew();
        capture.WaitForStable(clock, 2000);
        try
        {
            StableResult result = capture.WaitForStable(clock, 4000);
            throw new InvalidOperationException(
                $"An unchanged window was accepted as a completed action ({result.StableMs - result.FirstContentMs:F0} ms).");
        }
        catch (TimeoutException)
        {
            Console.WriteLine("PASS an unchanged captured window cannot complete a measured action.");
            return 0;
        }
        finally
        {
            window.Close();
        }
    }
}
