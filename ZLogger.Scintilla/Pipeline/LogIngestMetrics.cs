using System;
using System.Threading;
namespace ZLogger.Scintilla.Pipeline;

internal sealed class LogIngestMetrics
{
    private long _droppedMessages;
    private long _ingestBacklogEstimate;
    private long _renderedMessages;
    private long _historyEntries;

    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);
    public long PendingEntryCount => Interlocked.Read(ref _ingestBacklogEstimate);
    public long RenderedMessages => Interlocked.Read(ref _renderedMessages);
    public long HistoryEntries => Interlocked.Read(ref _historyEntries);

    public void RecordAccepted()
    {
        Interlocked.Increment(ref _ingestBacklogEstimate);
    }

    public void RecordDrop()
    {
        Interlocked.Increment(ref _droppedMessages);
    }

    public void RecordRendered(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _renderedMessages, count);
    }

    public void IncrementHistory() => Interlocked.Increment(ref _historyEntries);

    public void IncrementHistory(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _historyEntries, count);
    }

    public void DecrementHistory() => Interlocked.Decrement(ref _historyEntries);

    public void DecrementBacklog(int count)
    {
        if (count <= 0)
            return;

        while (true)
        {
            var current = Interlocked.Read(ref _ingestBacklogEstimate);
            if (current <= 0)
                return;

            var next = Math.Max(0, current - count);
            if (Interlocked.CompareExchange(ref _ingestBacklogEstimate, next, current) == current)
                return;
        }
    }

    /// <summary>
    /// Resets transient counters (history size, backlog estimate) after a document clear.
    /// <see cref="DroppedMessages"/> and <see cref="RenderedMessages"/> are lifetime counters
    /// and are intentionally preserved — they reflect cumulative throughput since the host started.
    /// </summary>
    public void ResetOnClear()
    {
        Interlocked.Exchange(ref _historyEntries, 0);
        Interlocked.Exchange(ref _ingestBacklogEstimate, 0);
    }
}
