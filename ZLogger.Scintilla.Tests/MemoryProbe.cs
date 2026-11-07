using System.Diagnostics;
using Microsoft.Extensions.Logging;

// live.Ui runs its lambda synchronously on the UI thread.
// ReSharper disable AccessToDisposedClosure
namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Prints what is left after <c>Clear()</c> and how much of it each step gives back, so that
/// "retained" can be told apart from "not collected yet" and "kept by the pool".
/// </summary>
[TestClass]
public sealed class MemoryProbe
{
    [TestMethod]
    public void MemoryAfterClear_IsReclaimable()
    {
        using var live = new LiveViewer();
        var baseline = Settled();
        Report("baseline", baseline, baseline);

        for (var i = 0; i < 50_000; i++)
            live.Logger.LogInformation("Request {I} processed in {Ms}ms from {Ip} ctx={Ctx}", i, i % 1500, "10.0.0.1", "k0 k1 k2 k3 k4 k5");
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel", 30_000);
        live.WaitForIdle();
        Report("holding 50k messages", Current(), baseline);

        live.Ui(() => live.Viewer.Clear());
        live.WaitForIdle();
        Report("after Clear, no GC", Current(), baseline);

        var settled = Settled();
        Report("after Clear and full GC", settled, baseline);

        var managed = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        Console.WriteLine($"GC heap after settling: {managed:F1} MB");

        // Holding 50k messages costs far more than this, so most of it must come back once collected.
        Assert.IsLessThan(40, settled - baseline, "Clear() kept memory that a full GC cannot reclaim.");
    }

    private static double Current()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024.0 * 1024.0);
    }

    private static double Settled()
    {
#if NET7_0_OR_GREATER
        const GCCollectionMode mode = GCCollectionMode.Aggressive;
#else
        const GCCollectionMode mode = GCCollectionMode.Forced;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
#endif
        GC.Collect(GC.MaxGeneration, mode, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, mode, blocking: true, compacting: true);
        Thread.Sleep(100);
        return Current();
    }

    private static void Report(string step, double value, double baseline) =>
        Console.WriteLine($"{step}: private {value:F1} MB, {value - baseline:+0.0;-0.0} MB over baseline");
}
