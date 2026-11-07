using System.Buffers;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// Reusable scratch space for the logging thread. Being per thread protects against other threads, but not
/// against this one: formatting runs application code, like a parameter's <c>ToString</c>, and that code can log.
/// So a buffer leaves the cache while in use, and a nested log call gets a different one instead of
/// overwriting a message that is half written.
/// </summary>
internal static class ThreadBufferCache
{
    // Two, because one log call can hold two at once: its text while it formats its JSON.
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _first;
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _second;
    [ThreadStatic]
    private static ParameterSpan[]? _spans;

    public static ArrayBufferWriter<byte> TakeWriter()
    {
        var writer = _first;
        if (writer is not null)
        {
            _first = null;
            writer.Clear();
            return writer;
        }

        writer = _second;
        if (writer is not null)
        {
            _second = null;
            writer.Clear();
            return writer;
        }

        return new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
    }

    /// <summary>A writer that grew for one huge message is not kept, or the thread would hold that memory for good.</summary>
    public static void ReturnWriter(ArrayBufferWriter<byte> writer)
    {
        if (writer.Capacity > LogConstants.MaxCachedWriterBytes)
            return;

        if (_first is null)
            _first = writer;
        else
            _second = writer;
    }

    public static ParameterSpan[] TakeSpans(int minimumLength)
    {
        var spans = _spans;
        if (spans is null || spans.Length < minimumLength)
            return new ParameterSpan[Math.Max(minimumLength, 8)];

        _spans = null;
        return spans;
    }

    /// <summary>Clears what was used, so that the cache does not keep strings and types alive.</summary>
    public static void ReturnSpans(ParameterSpan[] spans, int used)
    {
        Array.Clear(spans, 0, used);
        _spans = spans;
    }
}
