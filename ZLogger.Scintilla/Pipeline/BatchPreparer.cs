using System.Buffers;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;
using ZLogger.Scintilla.Services;
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable LoopCanBeConvertedToQuery
namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// Prepares log entries into a <see cref="PreparedBatch"/> containing pre-assembled
/// text bytes, style ranges, and token ranges. Must only be called from a single thread at a time.
/// An entry that fails to style is still shown, as plain text in its level's color.
/// </summary>
internal sealed class BatchPreparer(LogRenderer renderer, Action<Exception>? errorSink = null)
{
    private const int InitialTextCapacity = 128 * 1024;

    private readonly List<RenderSegment> _segments = new(LogConstants.SegmentsCapacity);
    private readonly List<RenderSegment> _jsonSegments = new(LogConstants.SegmentsCapacity);

    // Rented for the batch being built and handed over with it, so no text or range is copied twice.
    private byte[] _text = [];
    private StyleRange[] _styles = [];
    private TokenRange[] _tokens = [];
    private int _written;
    private int _styleCount;
    private int _tokenCount;

    private int _textCapacity = InitialTextCapacity;
    private int _styleCapacity = LogConstants.StyleRangesCapacity;

    public PreparedBatch Prepare(IReadOnlyList<LogEntry> entries, long epoch)
    {
        _text = ArrayPool<byte>.Shared.Rent(_textCapacity);
        _styles = ArrayPool<StyleRange>.Shared.Rent(_styleCapacity);
        _tokens = [];
        _written = 0;
        _styleCount = 0;
        _tokenCount = 0;
        _segments.Clear();

        for (var i = 0; i < entries.Count; i++)
            PrepareEntry(entries[i]);

        // Start the next batch at this size. Growing a rented array means copying it.
        _textCapacity = Math.Max(InitialTextCapacity, Math.Min(_text.Length, LogConstants.MaxRetainedBatchBytes));
        _styleCapacity = Math.Max(LogConstants.StyleRangesCapacity, _styles.Length);

        var batch = new PreparedBatch(
            new PooledSegment<byte>(_text, _written),
            new PooledSegment<StyleRange>(_styles, _styleCount),
            new PooledSegment<TokenRange>(_tokens, _tokenCount),
            epoch, entries.Count);

        _text = [];
        _styles = [];
        _tokens = [];
        return batch;
    }


    private void PrepareEntry(LogEntry entry)
    {
        var lineStart = _written;
        AppendMessageBytes(entry.Message);

        var styleStart = _styleCount;
        var tokenStart = _tokenCount;
        try
        {
            renderer.BuildSegments(entry, _segments);
            AppendStyleRanges(_segments, entry.Level, entry.Message.Count, lineStart);
        }
        catch (Exception ex)
        {
            _styleCount = styleStart;
            _tokenCount = tokenStart;
            AddStyle(new StyleRange(entry.Message.Count, entry.Level.ToStyleId(), null, false));
            Report(ex);
        }

        AppendNewLine();

        if (renderer.IsPrettyJsonEnabled)
            AppendJsonLines(entry);
    }

    private void AppendMessageBytes(ArraySegment<byte> message)
    {
        EnsureTextCapacity(_written + message.Count + LogConstants.NewLineBytes.Length);
        if (message is { Count: > 0, Array: not null })
            Buffer.BlockCopy(message.Array, message.Offset, _text, _written, message.Count);
        _written += message.Count;
    }

    private void AppendNewLine()
    {
        var newLine = LogConstants.NewLineBytes;
        for (var i = 0; i < newLine.Length; i++)
            _text[_written + i] = newLine[i];
        _written += newLine.Length;
        AddStyle(new StyleRange(newLine.Length, SemanticStyle.SecondaryText.ToStyleId(), null, false));
    }

    private void AppendJsonLines(LogEntry entry)
    {
        var writtenBefore = _written;
        var styleStart = _styleCount;
        var tokenStart = _tokenCount;
        try
        {
            if (entry.GetJsonBytes() is not { Length: > 0 } jsonBytes)
                return;

            EnsureTextCapacity(_written + jsonBytes.Length + LogConstants.NewLineBytes.Length);
            Buffer.BlockCopy(jsonBytes, 0, _text, _written, jsonBytes.Length);
            _written += jsonBytes.Length;

            renderer.BuildJsonSegments(jsonBytes, _jsonSegments);
            AppendStyleRanges(_jsonSegments, entry.Level, jsonBytes.Length, writtenBefore);

            AppendNewLine();
        }
        catch (Exception ex)
        {
            _written = writtenBefore;
            _styleCount = styleStart;
            _tokenCount = tokenStart;
            Report(ex);
        }
    }

    private void AppendStyleRanges(
        List<RenderSegment> segments,
        Microsoft.Extensions.Logging.LogLevel level,
        int expectedLineBytes,
        int lineOffset)
    {
        var total = 0;
        for (var i = 0; i < segments.Count; i++)
            total += segments[i].Utf8Length;

        // Fewer or more styled bytes than text would shift the style of every later line.
        if (total != expectedLineBytes)
        {
            AddStyle(new StyleRange(expectedLineBytes, level.ToStyleId(), null, false));
            return;
        }

        var cursor = lineOffset;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Utf8Length <= 0)
                continue;

            AddStyle(new StyleRange(segment.Utf8Length, segment.SemanticStyle.ToStyleId(), segment.TokenPayload, segment.IsLink));
            if (segment is { IsLink: true, TokenPayload: { } payload })
                AddToken(new TokenRange(cursor, segment.Utf8Length, payload));
            cursor += segment.Utf8Length;
        }
    }

    private void AddStyle(in StyleRange range)
    {
        if (_styleCount == _styles.Length)
            Grow(ref _styles, _styleCount, _styleCount * 2);
        _styles[_styleCount++] = range;
    }

    private void AddToken(in TokenRange range)
    {
        if (_tokenCount == _tokens.Length)
            Grow(ref _tokens, _tokenCount, Math.Max(LogConstants.TokenRangesCapacity, _tokenCount * 2));
        _tokens[_tokenCount++] = range;
    }

    private void EnsureTextCapacity(int required)
    {
        if (required <= _text.Length)
            return;

        Grow(ref _text, _written, Math.Max(required, _text.Length * 2));
    }

    private static void Grow<T>(ref T[] array, int used, int minimumLength)
    {
        var grown = ArrayPool<T>.Shared.Rent(minimumLength);
        Array.Copy(array, grown, used);
        new PooledSegment<T>(array, used).Return();
        array = grown;
    }

    private void Report(Exception ex)
    {
        try
        {
            errorSink?.Invoke(ex);
        }
        catch
        {
            // A broken sink must not stop the pipeline it reports on.
        }
    }
}
