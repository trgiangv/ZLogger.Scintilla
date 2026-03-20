using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
namespace ZLogger.Scintilla.Models;

public sealed class LogEntry
{
    public static readonly IReadOnlyDictionary<string, object?> EmptyProperties = new Dictionary<string, object?>();

    private Action<byte[]>? _bufferReleaser;
    private int _bufferReleased;

    public LogLevel Level { get; init; } = LogLevel.Information;
    public ArraySegment<byte> Message { get; init; }
    public IReadOnlyDictionary<string, object?> Properties { get; init; } = EmptyProperties;

    public void AttachBufferLease(Action<byte[]> releaser)
    {
        _bufferReleaser = releaser ?? throw new ArgumentNullException(nameof(releaser));
        _bufferReleased = 0;
    }

    public void ReleaseBuffer()
    {
        var releaser = _bufferReleaser;
        if (releaser is null)
            return;

        if (Interlocked.Exchange(ref _bufferReleased, 1) != 0)
            return;

        var buffer = Message.Array;
        if (buffer is null)
            return;

        releaser(buffer);
    }
}
