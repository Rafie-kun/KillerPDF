using Xunit;

namespace KillerPdf.Engine.Tests;

/// <summary>
/// Zero-allocation checks for hot paths. The code under test either allocates on every pass or
/// never does, so a few measured passes with one clean result prove it does not, while a one-off
/// allocation by the runtime itself on the test thread cannot fail the check. A full-suite run
/// produced exactly that once on an ICC table transform whose source has no heap allocations.
/// </summary>
internal static class AllocationAssert
{
    internal static void None(Action action, int attempts = 3)
    {
        ArgumentNullException.ThrowIfNull(action);
        var allocated = new long[attempts];
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            allocated[attempt] = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated[attempt] == 0) return;
        }
        Assert.Fail($"Every measured pass allocated: {string.Join(", ", allocated)} bytes.");
    }
}
