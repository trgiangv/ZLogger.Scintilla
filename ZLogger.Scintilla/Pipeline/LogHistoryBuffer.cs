using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;

namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// Ring-buffer of <see cref="LogEntry"/> used for filter-reapply.
/// <para>
/// <b>Concurrency contract:</b> <see cref="EnqueueBatch"/> is called from the pump thread;
/// <see cref="CaptureSnapshot"/> is called from a background filter-reapply task.
/// Because <see cref="ConcurrentQueue{T}"/> enumerator returns a point-in-time snapshot,
/// <see cref="CaptureSnapshot"/> is <b>best-effort</b> — it may include a partial batch
/// that was being enqueued concurrently. This is acceptable because the UI will be
/// refreshed again on the next filter-reapply or batch flush.
/// </para>
/// </summary>
internal sealed class LogHistoryBuffer(LogIngestMetrics metrics, int maxEntries)
{
    private readonly ConcurrentQueue<LogEntry> _history = new();
    private long _localCount;

    private const int CancellationCheckInterval = 256;

    public bool IsEnabled => maxEntries > 0;

    /// <summary>Current number of entries tracked by this buffer (local, not shared metrics).</summary>
    public long Count => Interlocked.Read(ref _localCount);

    public void Enqueue(LogEntry entry)
    {
        _history.Enqueue(entry);
        Interlocked.Increment(ref _localCount);
        metrics.IncrementHistory();
    }

    public void EnqueueBatch(IReadOnlyList<LogEntry> entries)
    {
        var count = entries.Count;
        for (var i = 0; i < count; i++)
            _history.Enqueue(entries[i]);

        Interlocked.Add(ref _localCount, count);
        metrics.IncrementHistory(count);
    }

    public void TrimIfNeeded()
    {
        var overflow = Interlocked.Read(ref _localCount) - maxEntries;
        if (overflow <= 0)
            return;

        var toTrim = (int)Math.Min(int.MaxValue, overflow);
        for (var i = 0; i < toTrim; i++)
        {
            if (!_history.TryDequeue(out var removedEntry))
                break;

            Interlocked.Decrement(ref _localCount);
            metrics.DecrementHistory();
            removedEntry.ReleaseBuffer();
        }
    }

    /// <summary>
    /// Takes a best-effort snapshot of history entries matching <paramref name="minimumLevel"/>.
    /// The snapshot may include a partial concurrent batch — see class-level docs.
    /// </summary>
    public List<LogEntry> CaptureSnapshot(LogLevel minimumLevel, CancellationToken cancellationToken = default)
    {
        var showAll = minimumLevel <= LogLevel.Trace;
        var snapshot = new List<LogEntry>(EstimateSnapshotCapacity(minimumLevel));

        foreach (var item in EnumerateHistory(cancellationToken))
        {
            if (showAll || item.Level >= minimumLevel)
                snapshot.Add(item);
        }

        return snapshot;
    }

    /// <summary>
    /// Yields history entries matching <paramref name="minimumLevel"/> in chunks of
    /// <paramref name="chunkSize"/> to avoid large single allocations during replay.
    /// </summary>
    public IEnumerable<List<LogEntry>> CaptureChunkedSnapshot(
        LogLevel minimumLevel,
        int chunkSize = 1000,
        CancellationToken cancellationToken = default)
    {
        var showAll = minimumLevel <= LogLevel.Trace;
        var chunk = new List<LogEntry>(chunkSize);

        foreach (var item in EnumerateHistory(cancellationToken))
        {
            if (!showAll && item.Level < minimumLevel)
                continue;

            chunk.Add(item);
            if (chunk.Count < chunkSize)
                continue;

            yield return chunk;
            chunk = new List<LogEntry>(chunkSize);
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    private IEnumerable<LogEntry> EnumerateHistory(CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var item in _history)
        {
            if ((index++ & (CancellationCheckInterval - 1)) == 0 && cancellationToken.IsCancellationRequested)
                yield break;

            yield return item;
        }
    }

    public void DrainAll(Action<LogEntry> onEntry)
    {
        while (_history.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _localCount);
            onEntry(entry);
        }
    }

    private int EstimateSnapshotCapacity(LogLevel minimumLevel)
    {
        var count = Interlocked.Read(ref _localCount);
        if (count <= 0)
            return 0;

        var bounded = count > int.MaxValue ? int.MaxValue : (int)count;
        return minimumLevel <= LogLevel.Trace ? bounded : Math.Max(128, bounded / 3);
    }
}
