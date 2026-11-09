using System.Threading.Channels;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// An item in the channel: a message formatted on the logging thread, or a ZLogger entry
/// that the pump will format. A struct, so that queuing a deferred entry allocates nothing.
/// </summary>
internal readonly struct PendingEntry
{
    public readonly LogEntry? Ready;
    public readonly IZLoggerEntry? Deferred;

    public PendingEntry(LogEntry ready)
    {
        Ready = ready;
        Deferred = null;
    }

    public PendingEntry(IZLoggerEntry deferred)
    {
        Ready = null;
        Deferred = deferred;
    }
}

/// <summary>
/// The bounded channel between logging threads and the pump. Reads must come from one thread at a time,
/// which <see cref="BatchPump"/> guarantees by reading only while holding its pipeline lock.
/// </summary>
internal sealed class LogIngestChannel
{
    private readonly ChannelWriter<PendingEntry> _writer;
    private readonly ChannelReader<PendingEntry> _reader;
    private readonly ScintillaOptions _options;

    /// <param name="options">Channel capacity, batch size and drop policy.</param>
    /// <param name="onDropped">
    /// Called for every entry the channel discards when full. With a drop policy, <c>TryWrite</c> returns
    /// true even when it discards, so this callback is the only way to learn about the discard.
    /// </param>
    public LogIngestChannel(ScintillaOptions options, Action<PendingEntry>? onDropped = null)
    {
        _options = options;

        var channelOptions = new BoundedChannelOptions(Math.Max(1, options.ChannelCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = options.DropPolicy
        };

        var channel = Channel.CreateBounded(channelOptions, onDropped);
        _writer = channel.Writer;
        _reader = channel.Reader;
    }

    public bool TryPost(LogEntry entry) => _writer.TryWrite(new PendingEntry(entry));

    public bool TryPost(IZLoggerEntry entry) => _writer.TryWrite(new PendingEntry(entry));

    /// <summary>Reads up to a batch of entries and returns how many left the channel.</summary>
    /// <param name="targetBuffer">Receives the entries to keep.</param>
    /// <param name="materialize">Formats a deferred entry, or returns null if it does not need to be kept.</param>
    /// <param name="metrics">
    /// Counted one entry at a time, because <paramref name="materialize"/> decides by how many are still waiting.
    /// </param>
    public int TryReadBatch(List<LogEntry> targetBuffer, Func<IZLoggerEntry, LogEntry?> materialize, LogIngestMetrics metrics)
    {
        targetBuffer.Clear();
        var read = 0;
        while (read < _options.MaxBatchSize && _reader.TryRead(out var pending))
        {
            read++;
            metrics.DecrementBacklog(1);
            if ((pending.Ready ?? materialize(pending.Deferred!)) is { } entry)
                targetBuffer.Add(entry);
        }

        return read;
    }

    /// <summary>Empties the channel, handing each entry to <paramref name="onDiscarded"/>. Returns how many there were.</summary>
    public int DiscardAll(Action<PendingEntry> onDiscarded)
    {
        var count = 0;
        while (_reader.TryRead(out var entry))
        {
            onDiscarded(entry);
            count++;
        }

        return count;
    }

    public void Complete()
        => _writer.TryComplete();
}
