using System.Buffers;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Verifies PreparedBatch lifecycle: pooled buffer return and entry release.
/// </summary>
public sealed class AllocationBudgetTests
{
    // ── PreparedBatch lifecycle ────────────────────────────────────────

    [Fact]
    public void PreparedBatch_ReturnBuffers_ReturnsPooledArrays()
    {
        var textBuffer = ArrayPool<byte>.Shared.Rent(64);
        var styleRanges = ArrayPool<StyleRange>.Shared.Rent(4);
        var tokenRanges = ArrayPool<TokenRange>.Shared.Rent(2);

        var batch = new PreparedBatch(
            new PooledSegment<byte>(textBuffer, 10),
            new PooledSegment<StyleRange>(styleRanges, 2),
            new PooledSegment<TokenRange>(tokenRanges, 1),
            epoch: 0, entryCount: 1,
            entriesToRelease: null);

        batch.ReturnBuffers();
    }

    [Fact]
    public void PreparedBatch_ReleaseEntryBuffers_ReleasesBuffers()
    {
        var released = 0;
        var entries = new List<LogEntry>
        {
            MakeEntryWithRelease(() => released++),
            MakeEntryWithRelease(() => released++)
        };

        var batch = new PreparedBatch(
            new PooledSegment<byte>(Array.Empty<byte>(), 0),
            new PooledSegment<StyleRange>(Array.Empty<StyleRange>(), 0),
            new PooledSegment<TokenRange>(Array.Empty<TokenRange>(), 0),
            epoch: 0, entryCount: 2,
            entriesToRelease: entries);

        batch.ReleaseEntryBuffers();

        Assert.Equal(2, released);
    }

    [Fact]
    public void PreparedBatch_NullEntriesToRelease_DoesNotThrow()
    {
        var batch = new PreparedBatch(
            new PooledSegment<byte>(Array.Empty<byte>(), 0),
            new PooledSegment<StyleRange>(Array.Empty<StyleRange>(), 0),
            new PooledSegment<TokenRange>(Array.Empty<TokenRange>(), 0),
            epoch: 0, entryCount: 0,
            entriesToRelease: null);

        batch.ReleaseEntryBuffers();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static LogEntry MakeEntryWithRelease(Action onRelease)
    {
        var entry = TestHelper.MakeEntry("test", LogLevel.Information);
        entry.AttachBufferLease(_ => onRelease());
        return entry;
    }
}
