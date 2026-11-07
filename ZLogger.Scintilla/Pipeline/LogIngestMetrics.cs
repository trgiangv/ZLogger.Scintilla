namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// Viewer counters. <see cref="DroppedMessages"/> and <see cref="RenderedMessages"/> are lifetime totals.
/// A filter replay renders its entries again and counts them again.
/// </summary>
internal sealed class LogIngestMetrics
{
    private long _droppedMessages;
    private long _ingestBacklog;
    private long _renderedMessages;
    private long _historyEntries;

    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);
    public long PendingEntryCount => Interlocked.Read(ref _ingestBacklog);
    public long RenderedMessages => Interlocked.Read(ref _renderedMessages);
    public long HistoryEntries => Interlocked.Read(ref _historyEntries);

    /// <summary>Counted before the channel write, so that a concurrent read can never take the count below zero.</summary>
    public void RecordAccepted()
    {
        Interlocked.Increment(ref _ingestBacklog);
    }

    /// <summary>An accepted entry was discarded while still waiting in the channel.</summary>
    public void RecordDrop()
    {
        Interlocked.Increment(ref _droppedMessages);
        Interlocked.Decrement(ref _ingestBacklog);
    }

    public void RecordRendered(int count)
    {
        Interlocked.Add(ref _renderedMessages, count);
    }

    public void AddHistory(int delta)
    {
        Interlocked.Add(ref _historyEntries, delta);
    }

    public void DecrementBacklog(int count)
    {
        Interlocked.Add(ref _ingestBacklog, -count);
    }
}
