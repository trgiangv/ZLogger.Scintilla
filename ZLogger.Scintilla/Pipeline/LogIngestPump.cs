using System;
using System.Collections.Generic;
using System.Threading.Channels;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Pipeline;

internal sealed class LogIngestPump
{
    private readonly ChannelWriter<LogEntry> _writer;
    private readonly ChannelReader<LogEntry> _reader;
    private readonly ScintillaOptions _options;

    public LogIngestPump(ScintillaOptions options)
    {
        _options = options;

        var channelOptions = new BoundedChannelOptions(Math.Max(1, options.ChannelCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = options.DropPolicy,
            AllowSynchronousContinuations = true
        };

        var channel = Channel.CreateBounded<LogEntry>(channelOptions);
        _writer = channel.Writer;
        _reader = channel.Reader;
    }

    public bool TryPost(LogEntry entry)
    {
        return _writer.TryWrite(entry);
    }

    public int TryReadBatch(List<LogEntry> targetBuffer)
    {
        targetBuffer.Clear();
        while (targetBuffer.Count < _options.MaxBatchSize && _reader.TryRead(out var entry))
            targetBuffer.Add(entry);

        return targetBuffer.Count;
    }

    public int Drain(Action<LogEntry> onEntry)
    {
        var count = 0;
        while (_reader.TryRead(out var entry))
        {
            onEntry(entry);
            count++;
        }

        return count;
    }

    public void Complete()
        => _writer.TryComplete();
}
