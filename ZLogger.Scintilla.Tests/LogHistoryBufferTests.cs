using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;

namespace ZLogger.Scintilla.Tests;

public sealed class LogHistoryBufferTests
{
    // ── IsEnabled ─────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_FalseWhenMaxEntriesIsZero()
    {
        var buf = MakeBuffer(maxEntries: 0);
        Assert.False(buf.IsEnabled);
    }

    [Fact]
    public void IsEnabled_TrueWhenMaxEntriesPositive()
    {
        var buf = MakeBuffer(maxEntries: 100);
        Assert.True(buf.IsEnabled);
    }

    // ── Enqueue / EnqueueBatch ────────────────────────────────────────────

    [Fact]
    public void Enqueue_IncreasesHistoryCount()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 100, metrics);
        buf.Enqueue(MakeEntry("a", LogLevel.Information));
        Assert.Equal(1, metrics.HistoryEntries);
    }

    [Fact]
    public void EnqueueBatch_AddsAllEntries()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 100, metrics);
        var entries = new[]
        {
            MakeEntry("a", LogLevel.Information),
            MakeEntry("b", LogLevel.Warning),
            MakeEntry("c", LogLevel.Error)
        };
        buf.EnqueueBatch(entries);
        Assert.Equal(3, metrics.HistoryEntries);
    }

    // ── CaptureSnapshot — All ─────────────────────────────────────────────

    [Fact]
    public void CaptureSnapshot_All_ReturnsAllEntries()
    {
        var buf = MakeBuffer(maxEntries: 100);
        buf.Enqueue(MakeEntry("a", LogLevel.Information));
        buf.Enqueue(MakeEntry("b", LogLevel.Warning));
        buf.Enqueue(MakeEntry("c", LogLevel.Error));

        var snapshot = buf.CaptureSnapshot(LogLevel.Trace);

        Assert.Equal(3, snapshot.Count);
    }

    // ── CaptureSnapshot — level-only ──────────────────────────────────────

    [Fact]
    public void CaptureSnapshot_LevelOnly_FiltersCorrectly()
    {
        var buf = MakeBuffer(maxEntries: 100);
        buf.Enqueue(MakeEntry("info", LogLevel.Information));
        buf.Enqueue(MakeEntry("warn", LogLevel.Warning));
        buf.Enqueue(MakeEntry("err", LogLevel.Error));

        var snapshot = buf.CaptureSnapshot(LogLevel.Warning);

        Assert.Equal(2, snapshot.Count);
        Assert.Collection(
            snapshot,
            entry => Assert.Equal(LogLevel.Warning, entry.Level),
            entry => Assert.Equal(LogLevel.Error, entry.Level));
    }

    // ── CaptureSnapshot — cancellation ────────────────────────────────────

    [Fact]
    public void CaptureSnapshot_CancelledToken_ReturnsPartialOrEmpty()
    {
        var buf = MakeBuffer(maxEntries: 10000);
        for (var i = 0; i < 512; i++)
            buf.Enqueue(MakeEntry($"entry {i}", LogLevel.Information));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var snapshot = buf.CaptureSnapshot(LogLevel.Trace, cts.Token);

        // Cancellation is checked every 256 items; may return 0 or 256 entries
        Assert.True(snapshot.Count <= 256);
    }

    // ── TrimIfNeeded ──────────────────────────────────────────────────────

    [Fact]
    public void TrimIfNeeded_RemovesOldestEntriesWhenOverCapacity()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 3, metrics);
        buf.Enqueue(MakeEntry("a", LogLevel.Information));
        buf.Enqueue(MakeEntry("b", LogLevel.Information));
        buf.Enqueue(MakeEntry("c", LogLevel.Information));
        buf.Enqueue(MakeEntry("d", LogLevel.Information));

        buf.TrimIfNeeded();

        Assert.Equal(3, buf.Count);
        Assert.Equal(3, metrics.HistoryEntries);
    }

    // ── Local count ────────────────────────────────────────────────────────

    [Fact]
    public void Count_TracksLocalEntries_IndependentOfMetrics()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 100, metrics);
        buf.Enqueue(MakeEntry("a", LogLevel.Information));
        buf.EnqueueBatch(new[] { MakeEntry("b", LogLevel.Warning), MakeEntry("c", LogLevel.Error) });

        Assert.Equal(3, buf.Count);
        Assert.Equal(3, metrics.HistoryEntries);

        metrics.ResetOnClear();
        Assert.Equal(0, metrics.HistoryEntries);
        Assert.Equal(3, buf.Count);
    }

    [Fact]
    public void TrimIfNeeded_UsesLocalCount_NotSharedMetrics()
    {
        var metrics = new LogIngestMetrics();
        var buf = MakeBuffer(maxEntries: 2, metrics);
        buf.Enqueue(MakeEntry("a", LogLevel.Information));
        buf.Enqueue(MakeEntry("b", LogLevel.Information));
        buf.Enqueue(MakeEntry("c", LogLevel.Information));

        metrics.ResetOnClear();
        Assert.Equal(0, metrics.HistoryEntries);

        buf.TrimIfNeeded();
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void DrainAll_DecrementsLocalCount()
    {
        var buf = MakeBuffer(maxEntries: 100);
        buf.Enqueue(MakeEntry("a", LogLevel.Information));
        buf.Enqueue(MakeEntry("b", LogLevel.Warning));
        Assert.Equal(2, buf.Count);

        buf.DrainAll(_ => { });
        Assert.Equal(0, buf.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static LogHistoryBuffer MakeBuffer(int maxEntries, LogIngestMetrics? metrics = null)
        => new(metrics ?? new LogIngestMetrics(), maxEntries);

    private static LogEntry MakeEntry(string text, LogLevel level)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new LogEntry
        {
            Level = level,
            Message = new ArraySegment<byte>(bytes),
            Properties = LogEntry.EmptyProperties
        };
    }
}
