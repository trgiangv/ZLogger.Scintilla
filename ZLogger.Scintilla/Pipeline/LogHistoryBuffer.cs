using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;

namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// The most recent entries, kept so that a filter or pretty JSON change can rebuild the document.
/// Not thread-safe: every use holds the viewer's pump lock, and nothing changes it during a replay.
/// </summary>
internal sealed class LogHistoryBuffer(LogIngestMetrics metrics, int maxEntries)
{
    private readonly Queue<LogEntry> _history = new();

    public bool IsEnabled => maxEntries > 0;

    public int Count => _history.Count;

    public void EnqueueBatch(IReadOnlyList<LogEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
            _history.Enqueue(entries[i]);

        metrics.AddHistory(entries.Count);
    }

    public void TrimIfNeeded()
    {
        var overflow = _history.Count - maxEntries;
        if (overflow <= 0)
            return;

        for (var i = 0; i < overflow; i++)
            _history.Dequeue();

        metrics.AddHistory(-overflow);
    }

    /// <summary>
    /// Yields entries at or above <paramref name="minimumLevel"/> in chunks. Each chunk reuses
    /// the same list, so a chunk is only valid until the next one is requested.
    /// </summary>
    public IEnumerable<List<LogEntry>> CaptureChunkedSnapshot(LogLevel minimumLevel, int chunkSize = 1000)
    {
        var chunk = new List<LogEntry>(chunkSize);
        foreach (var item in _history)
        {
            if (item.Level < minimumLevel)
                continue;

            chunk.Add(item);
            if (chunk.Count < chunkSize)
                continue;

            yield return chunk;
            chunk.Clear();
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    public void Clear()
    {
        metrics.AddHistory(-_history.Count);
        _history.Clear();
    }
}
