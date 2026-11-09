using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class LogHistoryBufferTests
{
    [TestMethod]
    public void IsEnabled_FalseWhenMaxEntriesIsZero()
    {
        Assert.IsFalse(MakeBuffer(maxEntries: 0).IsEnabled);
    }

    [TestMethod]
    public void IsEnabled_TrueWhenMaxEntriesPositive()
    {
        Assert.IsTrue(MakeBuffer(maxEntries: 100).IsEnabled);
    }

    [TestMethod]
    public void EnqueueBatch_UpdatesCountAndMetrics()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 100, metrics);

        buf.EnqueueBatch([Entry(LogLevel.Information), Entry(LogLevel.Warning), Entry(LogLevel.Error)]);

        Assert.AreEqual(3, buf.Count);
        Assert.AreEqual(3, metrics.HistoryEntries);
    }

    [TestMethod]
    public void Snapshot_AtTrace_ReturnsEveryEntry()
    {
        var buf = MakeBuffer(maxEntries: 100);
        buf.EnqueueBatch([Entry(LogLevel.Trace), Entry(LogLevel.Warning), Entry(LogLevel.Error)]);

        Assert.AreEqual(3, Levels(buf, LogLevel.Trace).Count);
    }

    [TestMethod]
    public void Snapshot_FiltersByLevel_InOrder()
    {
        var buf = MakeBuffer(maxEntries: 100);
        buf.EnqueueBatch([Entry(LogLevel.Information), Entry(LogLevel.Warning), Entry(LogLevel.Error)]);

        CollectionAssert.AreEqual(
            new[] { LogLevel.Warning, LogLevel.Error },
            Levels(buf, LogLevel.Warning));
    }

    [TestMethod]
    public void Snapshot_SplitsIntoChunks()
    {
        var buf = MakeBuffer(maxEntries: 100);
        buf.EnqueueBatch([.. Enumerable.Range(0, 25).Select(_ => Entry(LogLevel.Information))]);

        var sizes = buf.CaptureChunkedSnapshot(LogLevel.Trace, chunkSize: 10).Select(chunk => chunk.Count).ToArray();

        CollectionAssert.AreEqual(new[] { 10, 10, 5 }, sizes);
    }

    [TestMethod]
    public void TrimIfNeeded_RemovesTheOldest()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 2, metrics);
        buf.EnqueueBatch([Entry(LogLevel.Trace), Entry(LogLevel.Warning), Entry(LogLevel.Error)]);

        buf.TrimIfNeeded();

        Assert.AreEqual(2, metrics.HistoryEntries);
        CollectionAssert.AreEqual(new[] { LogLevel.Warning, LogLevel.Error }, Levels(buf, LogLevel.Trace));
    }

    [TestMethod]
    public void TrimIfNeeded_LeavesAFullBufferAlone()
    {
        var buf = MakeBuffer(maxEntries: 2);
        buf.EnqueueBatch([Entry(LogLevel.Trace), Entry(LogLevel.Warning)]);

        buf.TrimIfNeeded();

        Assert.AreEqual(2, buf.Count);
    }

    [TestMethod]
    public void Clear_LeavesCountAndMetricsAtZero()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 100, metrics);
        buf.EnqueueBatch([Entry(LogLevel.Trace), Entry(LogLevel.Warning)]);

        buf.Clear();

        Assert.AreEqual(0, buf.Count);
        Assert.AreEqual(0, metrics.HistoryEntries);
    }

    private static List<LogLevel> Levels(LogHistoryBuffer buffer, LogLevel minimum) =>
        [.. buffer.CaptureChunkedSnapshot(minimum).SelectMany(chunk => chunk.Select(entry => entry.Level))];

    private static LogHistoryBuffer MakeBuffer(int maxEntries, LogIngestMetrics? metrics = null)
        => new(metrics ?? new LogIngestMetrics(), maxEntries);

    private static LogEntry Entry(LogLevel level) => TestHelper.MakeEntry("entry", level);
}
