using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Measures what the logging call allocates on the application's thread, and checks PreparedBatch's
/// lifecycle of pooled buffers.
/// </summary>
[TestClass]
public sealed class AllocationBudgetTests
{
    private const string Ip = "10.0.0.1";
    private const int Id = 42;
    private const int Ms = 17;

    // Budgets are for what the processor adds to ZLogger's own cost, which differs between runtimes.

    [TestMethod]
    public void InterpolatedLogCall_StaysWithinItsAllocationBudget()
    {
        var bytes = MeasureAddedPerCall(logger => logger.ZLogInformation($"Request {Id} processed in {Ms}ms from {Ip}"));

        // The entry object, two number strings, their spans, and the text itself.
        Assert.IsLessThanOrEqualTo(280, bytes);
    }

    [TestMethod]
    public void TemplateLogCall_StaysWithinItsAllocationBudget()
    {
        var bytes = MeasureAddedPerCall(logger => logger.LogInformation("Request {Id} processed in {Ms}ms from {Ip}", Id, Ms, Ip));
        Assert.IsLessThanOrEqualTo(280, bytes);
    }

    private static double MeasureAddedPerCall(Action<ILogger> log)
    {
        var zloggerAlone = MeasurePerCall(log, new DiscardProcessor());
        var withProcessor = MeasurePerCall(log);
        Console.WriteLine($"ZLogger alone: {zloggerAlone:F0} B. With the processor: {withProcessor:F0} B");
        return withProcessor - zloggerAlone;
    }

    [TestMethod]
    public void DeferredInterpolatedLogCall_AllocatesNothingOnTheLoggingThread()
    {
        Action<ILogger> log = static logger => logger.ZLogInformation($"Request {Id} processed in {Ms}ms from {Ip}");

        var zloggerAlone = MeasurePerCall(log, new DiscardProcessor());
        var deferred = MeasurePerCall(log, processor =>
            processor.SetDeferredSink(static entry =>
            {
                entry.Return();
                return true;
            }, System.Globalization.CultureInfo.CurrentCulture));
        Console.WriteLine($"ZLogger alone: {zloggerAlone:F0} B. Deferred log call: {deferred:F0} B");

        // Compared with ZLogger in the same conditions, because a short test runs before the JIT has fully
        // optimized the code. BenchmarkDotNet, which waits for that, measures 0 B for both on .NET 8. The
        // tolerance allows one-off allocations, not one per call.
        Assert.AreEqual(zloggerAlone, deferred, 0.1);
    }

    private sealed class DiscardProcessor : IAsyncLogProcessor
    {
        public void Post(IZLoggerEntry log) => log.Return();
        public ValueTask DisposeAsync() => default;
    }

    private static double MeasurePerCall(Action<ILogger> log, Action<ScintillaLogProcessor>? configure = null)
    {
        var processor = new ScintillaLogProcessor(_ => true);
        processor.SetCaptureAllParameters(false); // What a viewer with no linkifier uses.
        configure?.Invoke(processor);
        return MeasurePerCall(log, processor);
    }

    private static double MeasurePerCall(Action<ILogger> log, IAsyncLogProcessor processor)
    {
        const int warmup = 2000;
        const int calls = 20_000;
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddZLoggerLogProcessor(processor);
        });
        var logger = factory.CreateLogger("Bench");

        for (var i = 0; i < warmup; i++)
            log(logger);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
            log(logger);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;
    }

    [TestMethod]
    public void PreparedBatch_ForAction_OwnsNothing()
    {
        var batch = PreparedBatch.ForAction(DocumentAction.ResetForReplay, epoch: 3);

        Assert.AreEqual(DocumentAction.ResetForReplay, batch.Action);
        Assert.AreEqual(3, batch.Epoch);
        Assert.AreEqual(0, batch.EntryCount);
        batch.ReturnBuffers();
    }
}
