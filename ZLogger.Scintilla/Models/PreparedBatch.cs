using System.Buffers;
using System.Collections.Generic;
namespace ZLogger.Scintilla.Models;

/// <summary>
/// Immutable batch of pre-rendered log data ready for Scintilla API calls.
/// Created on the pump thread, consumed on the UI thread.
/// </summary>
internal readonly struct PreparedBatch(
    PooledSegment<byte> text, 
    PooledSegment<StyleRange> styles, 
    PooledSegment<TokenRange> tokens, 
    long epoch, 
    int entryCount, 
    List<LogEntry>? entriesToRelease)
{
    public readonly PooledSegment<byte> Text = text;
    public readonly PooledSegment<StyleRange> Styles = styles;
    public readonly PooledSegment<TokenRange> Tokens = tokens;

    public readonly long Epoch = epoch;
    public readonly int EntryCount = entryCount;

    /// <summary>
    /// Entries whose buffers must be released after rendering (only when history is disabled).
    /// Null when history owns the buffers.
    /// </summary>
    private readonly List<LogEntry>? _entriesToRelease = entriesToRelease;

    public void ReturnBuffers()
    {
        Text.Return();
        Styles.Return();
        Tokens.Return();
    }

    public void ReleaseEntryBuffers()
    {
        if (_entriesToRelease is not { Count: > 0 })
            return;

        foreach (var entry in _entriesToRelease)
            entry.ReleaseBuffer();
    }
}

/// <summary>
/// A pooled array segment: array rented from <see cref="ArrayPool{T}"/> plus its logical length.
/// </summary>
internal readonly struct PooledSegment<T>(T[] array, int count)
{
    public readonly T[] Array = array;
    public readonly int Count = count;

    public void Return()
    {
        if (Array.Length > 0)
            ArrayPool<T>.Shared.Return(Array);
    }
}
