using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;
using ZLogger.Scintilla.Services;
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable LoopCanBeConvertedToQuery
namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// Prepares log entries into a <see cref="PreparedBatch"/> containing pre-assembled
/// text bytes, style ranges, and token ranges. This class owns all scratch buffers
/// and must only be called from a single thread at a time.
/// </summary>
internal sealed class BatchPreparer(LogRenderer renderer, bool enableTokenLinks) : IDisposable
{
    private readonly List<RenderSegment> _segments = new(LogConstants.SegmentsCapacity);
    private readonly List<RenderSegment> _jsonSegments = new(LogConstants.SegmentsCapacity);
    private readonly List<StyleRange> _styleRangeList = new(LogConstants.StyleRangesCapacity);
    private readonly List<TokenRange> _tokenRangeList = new(LogConstants.TokenRangesCapacity);
    private byte[] _scratchBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

    public PreparedBatch Prepare(
        IReadOnlyList<LogEntry> entries,
        long epoch,
        List<LogEntry>? entriesToRelease)
    {
        _styleRangeList.Clear();
        _tokenRangeList.Clear();
        _segments.Clear();

        var written = 0;
        for (var i = 0; i < entries.Count; i++)
            written = PrepareEntry(entries[i], written);

        return SnapshotResult(written, epoch, entries.Count, entriesToRelease);
    }

    private int PrepareEntry(LogEntry entry, int written)
    {
        written = AppendMessageBytes(entry.Message, written);

        renderer.BuildSegments(entry, _segments);
        var styleStartIndex = _styleRangeList.Count;
        var lineTextOffset = written - entry.Message.Count;
        AppendStyleRangesFromSegments(_segments, entry.Level, entry.Message.Count);

        if (enableTokenLinks)
            CollectTokenRangesFromStyles(styleStartIndex, lineTextOffset);

        written = AppendNewLine(written);

        if (renderer.IsPrettyJsonEnabled)
            written = AppendJsonLines(entry, written);

        return written;
    }

    private int AppendMessageBytes(ArraySegment<byte> message, int written)
    {
        var lineByteCount = message.Count;
        EnsureScratchCapacity(written + lineByteCount + LogConstants.NewLineBytes.Length, written);

        if (lineByteCount > 0 && message.Array is not null)
            Buffer.BlockCopy(message.Array, message.Offset, _scratchBuffer, written, lineByteCount);

        return written + lineByteCount;
    }

    private int AppendNewLine(int written)
    {
        Buffer.BlockCopy(LogConstants.NewLineBytes, 0, _scratchBuffer, written, LogConstants.NewLineBytes.Length);
        _styleRangeList.Add(new StyleRange(LogConstants.NewLineBytes.Length, SemanticStyle.SecondaryText.ToStyleId(), null, false));
        return written + LogConstants.NewLineBytes.Length;
    }

    private PreparedBatch SnapshotResult(
        int written, long epoch, int entryCount, List<LogEntry>? entriesToRelease)
    {
        var textBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(written, 1));
        Buffer.BlockCopy(_scratchBuffer, 0, textBuffer, 0, written);

        var styleRanges = ArrayPool<StyleRange>.Shared.Rent(Math.Max(_styleRangeList.Count, 1));
        for (var i = 0; i < _styleRangeList.Count; i++)
            styleRanges[i] = _styleRangeList[i];

        var tokenRanges = _tokenRangeList.Count > 0
            ? ArrayPool<TokenRange>.Shared.Rent(_tokenRangeList.Count)
            : Array.Empty<TokenRange>();
        for (var i = 0; i < _tokenRangeList.Count; i++)
            tokenRanges[i] = _tokenRangeList[i];

        ShrinkScratchIfNeeded();

        return new PreparedBatch(
            new PooledSegment<byte>(textBuffer, written),
            new PooledSegment<StyleRange>(styleRanges, _styleRangeList.Count),
            new PooledSegment<TokenRange>(tokenRanges, _tokenRangeList.Count),
            epoch, entryCount, entriesToRelease);
    }

    private int AppendJsonLines(LogEntry entry, int written)
    {
        if (!entry.Properties.TryGetValue(LogConstants.JsonBytes, out var raw) || raw is not byte[] jsonBytes || jsonBytes.Length == 0)
            return written;

        var jsonText = Encoding.UTF8.GetString(jsonBytes);
        var jsonUtf8Count = jsonBytes.Length;
        var totalNeeded = jsonUtf8Count + LogConstants.NewLineBytes.Length;
        EnsureScratchCapacity(written + totalNeeded, written);

        Buffer.BlockCopy(jsonBytes, 0, _scratchBuffer, written, jsonUtf8Count);
        written += jsonUtf8Count;

        renderer.BuildJsonSegments(jsonText, _jsonSegments);
        AppendStyleRangesFromSegments(_jsonSegments, entry.Level, jsonUtf8Count);

        Buffer.BlockCopy(LogConstants.NewLineBytes, 0, _scratchBuffer, written, LogConstants.NewLineBytes.Length);
        written += LogConstants.NewLineBytes.Length;
        _styleRangeList.Add(new StyleRange(LogConstants.NewLineBytes.Length, SemanticStyle.SecondaryText.ToStyleId(), null, false));

        return written;
    }

    public void Dispose()
    {
        if (_scratchBuffer.Length <= 0) return;
        ArrayPool<byte>.Shared.Return(_scratchBuffer);
        _scratchBuffer = [];
    }

    private void AppendStyleRangesFromSegments(
        IReadOnlyList<RenderSegment> segments,
        Microsoft.Extensions.Logging.LogLevel level,
        int expectedLineBytes)
    {
        if (segments.Count == 0)
        {
            _styleRangeList.Add(new StyleRange(expectedLineBytes, level.ToStyleId(), null, false));
            return;
        }

        var total = 0;
        for (var i = 0; i < segments.Count; i++)
            total += segments[i].Utf8Length;

        Debug.Assert(total == expectedLineBytes,
            $"Segment UTF-8 sum ({total}) != expected ({expectedLineBytes}). Falling back to single-style line.");
        if (total != expectedLineBytes)
        {
            _styleRangeList.Add(new StyleRange(expectedLineBytes, level.ToStyleId(), null, false));
            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Utf8Length <= 0) continue;
            _styleRangeList.Add(new StyleRange(
                segment.Utf8Length,
                segment.SemanticStyle.ToStyleId(),
                segment.TokenPayload,
                segment.IsLink));
        }
    }

    private void CollectTokenRangesFromStyles(int styleStartIndex, int lineTextOffset)
    {
        var cursor = lineTextOffset;
        for (var i = styleStartIndex; i < _styleRangeList.Count; i++)
        {
            var range = _styleRangeList[i];
            if (range is { TokenPayload: { } tp, IsLink: true })
                _tokenRangeList.Add(new TokenRange(cursor, range.Length, tp));
            cursor += range.Length;
        }
    }

    private void EnsureScratchCapacity(int requiredSize, int bytesUsed)
    {
        if (requiredSize <= _scratchBuffer.Length)
            return;

        var newSize = _scratchBuffer.Length;
        while (newSize < requiredSize)
            newSize *= 2;

        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        Buffer.BlockCopy(_scratchBuffer, 0, newBuffer, 0, bytesUsed);
        ArrayPool<byte>.Shared.Return(_scratchBuffer);
        _scratchBuffer = newBuffer;
    }

    private void ShrinkScratchIfNeeded(int maxRetainedSize = 256 * 1024, int resetSize = 128 * 1024)
    {
        if (_scratchBuffer.Length <= maxRetainedSize)
            return;

        ArrayPool<byte>.Shared.Return(_scratchBuffer);
        _scratchBuffer = ArrayPool<byte>.Shared.Rent(resetSize);
    }
}
