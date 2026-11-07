using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using LookupEngine;
using ZLogger.LogStates;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable RedundantSuppressNullableWarningExpression
namespace ZLogger.Scintilla.Pipeline;

internal sealed class ScintillaLogProcessor : IAsyncLogProcessor
{
    // This JSON is shown to a person, not embedded in HTML, so nothing needs escaping for safety.
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [ThreadStatic]
    private static byte[]? _messageChunk;
    [ThreadStatic]
    private static int _messageChunkUsed;

    private static volatile Type? _templateEntryType;

    private readonly ScintillaLogFormatter _formatter = new();
    private readonly Func<LogEntry, bool> _tryPost;
    private int _disposed;
    private volatile bool _prettyJsonEnabled;
    private volatile bool _captureAllParameters = true;
    private volatile int _maxDecomposeDepth = LogConstants.DefaultDecomposeDepth;
    private volatile int _maxCollectionItems = LogConstants.DefaultDecomposeCollection;
    private volatile ICustomSerializer? _parameterSerializer;
    private volatile Func<IZLoggerEntry, bool>? _deferredSink;
    private volatile CultureInfo? _deferralCulture;

    internal ScintillaLogProcessor(Func<LogEntry, bool> tryPost)
    {
        _tryPost = tryPost ?? throw new ArgumentNullException(nameof(tryPost));
    }

    /// <summary>
    /// Only a linkifier can read every parameter, through <see cref="ParameterSpan.HasParameter"/>. Without one,
    /// a span is needed only for values that get their own color, and strings do not.
    /// </summary>
    internal void SetCaptureAllParameters(bool enabled) => _captureAllParameters = enabled;

    /// <summary>
    /// Lets entries be formatted later by <paramref name="sink"/>, for callers using <paramref name="culture"/>.
    /// A sink that returns false did not take the entry, so it gets formatted on the logging thread.
    /// </summary>
    internal void SetDeferredSink(Func<IZLoggerEntry, bool> sink, CultureInfo? culture)
    {
        _deferralCulture = culture;
        _deferredSink = sink;
    }

    internal void SetPrettyJson(bool enabled) => _prettyJsonEnabled = enabled;
    internal void SetJsonDepth(int depth) => _maxDecomposeDepth = depth > 0 ? depth : LogConstants.DefaultDecomposeDepth;
    internal void SetCollectionLimit(int maxItems) => _maxCollectionItems = maxItems > 0 ? maxItems : LogConstants.DefaultDecomposeCollection;
    internal void SetCustomSerializer(ICustomSerializer? serializer)
    {
        _parameterSerializer = serializer;
        _formatter.ParameterSerializer = serializer;
    }

    public void Post(IZLoggerEntry log)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            log.Return();
            return;
        }

        if (TryDefer(log))
            return;

        try
        {
            _tryPost(Build(log));
        }
        catch
        {
            // This runs on the application's logging call, which must not throw.
        }
        finally
        {
            log.Return();
        }
    }

    /// <summary>Formats a deferred entry on the pump and gives it back to ZLogger.</summary>
    internal LogEntry Materialize(IZLoggerEntry log)
    {
        try
        {
            return Build(log);
        }
        finally
        {
            log.Return();
        }
    }

    /// <summary>
    /// Hands the entry to the pump unformatted when formatting it later cannot change the result. This is how
    /// ZLogger itself is meant to be used, and it leaves only ZLogger's own cost on the logging thread.
    /// Anything else is formatted here, because a value may be mutable or tied to this thread, like a Revit
    /// API object or a Python object that needs the GIL.
    /// </summary>
    private bool TryDefer(IZLoggerEntry log)
    {
        var sink = _deferredSink;
        var culture = _deferralCulture;
        if (sink is null
            || culture is null
            || !ReferenceEquals(culture, CultureInfo.CurrentCulture) // Numbers and dates would print differently.
            || _parameterSerializer is not null
            || !IsKnownSafeState(log))
            return false;

        var count = log.ParameterCount;
        for (var i = 0; i < count; i++)
        {
            if (!IsImmutableValue(log.GetParameterType(i)))
                return false;
        }

        // An exception's text can read anything it holds, and scope values can be anything.
        var info = log.LogInfo;
        if (info.Exception is not null || info.ScopeState is not null)
            return false;

        return sink(log);
    }

    /// <summary>
    /// Interpolated strings and <c>ILogger.LogXxx</c> templates only format their parameters. Any other state
    /// comes with the caller's own formatter delegate, which can read anything.
    /// </summary>
    private static bool IsKnownSafeState(IZLoggerEntry log)
    {
        if (log is ZLoggerEntry<InterpolatedStringLogState>)
            return true;

        var type = log.GetType();
        if (type == _templateEntryType)
            return true;

        // Its state types are internal to their libraries, so the type can only be recognized by name.
        if (type.IsGenericType
            && type.GetGenericArguments()[0] is { IsGenericType: true } state
            && state.GetGenericArguments()[0].FullName == "Microsoft.Extensions.Logging.FormattedLogValues")
        {
            _templateEntryType = type;
            return true;
        }

        return false;
    }

    private static bool IsImmutableValue(Type type) =>
        type == typeof(string)
        || type.IsPrimitive
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid)
        || type.IsEnum;

    private LogEntry Build(IZLoggerEntry log)
    {
        var textWriter = ThreadBufferCache.TakeWriter();
        try
        {
            log.FormatUtf8(textWriter, _formatter);

            var spans = log.ParameterCount > 0 ? ExtractParameterSpans(log) : null;
            var json = _prettyJsonEnabled ? FormatJsonBytes(log) : null;
            return Create(log, textWriter.WrittenSpan, spans, json);
        }
        catch (Exception ex)
        {
            return CreateFailure(log, ex);
        }
        finally
        {
            ThreadBufferCache.ReturnWriter(textWriter);
        }
    }

    private static LogEntry Create(IZLoggerEntry log, ReadOnlySpan<byte> text, ParameterSpan[]? spans, byte[]? json)
    {
        // LogInfo is a large struct, and each read of the property copies all of it.
        var info = log.LogInfo;
        var hasScopes = info.ScopeState is { Properties.IsEmpty: false };
        return new LogEntry
        {
            Level = info.LogLevel,
            Message = CopyToMessageChunk(text),
            ParameterSpans = spans,
            JsonBytes = json,
            Properties = hasScopes ? log.BuildProperties(spans, json) : null!
        };
    }

    /// <summary>An entry must not vanish because something in it could not be turned into text.</summary>
    private static LogEntry CreateFailure(IZLoggerEntry log, Exception failure)
    {
        var writer = ThreadBufferCache.TakeWriter();
        try
        {
            ScintillaLogFormatter.FormatFailure(writer, log, failure);
            return Create(log, writer.WrittenSpan, null, null);
        }
        finally
        {
            ThreadBufferCache.ReturnWriter(writer);
        }
    }

    /// <summary>
    /// Copies a message into a chunk owned by this thread. Messages live about as long as the history keeps
    /// them, and nearly all of that time a pool would be empty anyway, so renting would only round each one
    /// up to a power of two. Packed like this they cost their exact size, and a chunk goes back to the GC
    /// when the last message in it leaves the history.
    /// </summary>
    private static ArraySegment<byte> CopyToMessageChunk(ReadOnlySpan<byte> text)
    {
        if (text.Length > LogConstants.MessageChunkBytes / 4)
        {
            var own = new byte[text.Length];
            text.CopyTo(own);
            return new ArraySegment<byte>(own);
        }

        var chunk = _messageChunk;
        if (chunk is null || _messageChunkUsed + text.Length > chunk.Length)
        {
            chunk = _messageChunk = new byte[LogConstants.MessageChunkBytes];
            _messageChunkUsed = 0;
        }

        var start = _messageChunkUsed;
        text.CopyTo(chunk.AsSpan(start));
        _messageChunkUsed = start + text.Length;
        return new ArraySegment<byte>(chunk, start, text.Length);
    }

    private byte[]? FormatJsonBytes(IZLoggerEntry log)
    {
        if (log.ParameterCount == 0)
            return null;

        var buffer = ThreadBufferCache.TakeWriter();
        try
        {
            var objectCount = 0;
            using var writer = new Utf8JsonWriter(buffer, JsonOptions);
            writer.WriteStartObject();

            for (var i = 0; i < log.ParameterCount; i++)
            {
                if (TryWriteParameter(writer, log, i))
                    objectCount++;
            }

            writer.WriteEndObject();
            writer.Flush();

            if (objectCount == 0)
                return null;

            var result = new byte[buffer.WrittenCount];
            buffer.WrittenSpan.CopyTo(result);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            ThreadBufferCache.ReturnWriter(buffer);
        }
    }

    private bool TryWriteParameter(Utf8JsonWriter writer, IZLoggerEntry log, int index)
    {
        var value = log.GetParameterValue(index);
        if (value is null or Exception)
            return false;

        var type = value.GetType();
        var key = log.GetParameterKeyAsString(index);

        if (TryWriteCustomSerializer(type, value, writer, key, index))
            return true;

        if (ParameterSpan.IsLeafType(type))
            return false;

        writer.WritePropertyName(string.IsNullOrEmpty(key) ? $"arg{index}" : key);

        if (value is System.Collections.IEnumerable enumerable)
        {
            writer.WriteEnumerable(enumerable, 0, _maxDecomposeDepth, _maxCollectionItems);
            return true;
        }

        try
        {
            var decomposed = LookupComposer.Decompose(value, DecomposeOptions.Default);
            if (decomposed.Members.Count == 0)
            {
                writer.WriteJsonValue(value);
                return true;
            }
            writer.WriteDecomposedJson(decomposed, 0, _maxDecomposeDepth, _maxCollectionItems);
            return true;
        }
        catch
        {
            writer.WriteJsonValue(value);
            return true;
        }
    }

    private bool TryWriteCustomSerializer(Type type, object value, Utf8JsonWriter writer, string key, int index)
    {
        var serializer = _parameterSerializer;
        if (serializer is null || !serializer.CanSerialize(type))
            return false;

        writer.WritePropertyName(string.IsNullOrEmpty(key) ? $"arg{index}" : key);
        try
        {
            serializer.WriteJson(writer, value, _maxDecomposeDepth, _maxCollectionItems);
        }
        catch
        {
            writer.WriteStringValue(type.FullName);
        }
        return true;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return default;
    }

    private ParameterSpan[]? ExtractParameterSpans(IZLoggerEntry entry)
    {
        var paramCount = entry.ParameterCount;
        var scratch = ThreadBufferCache.TakeSpans(paramCount);
        var captureAll = _captureAllParameters;
        var count = 0;
        for (var i = 0; i < paramCount; i++)
        {
            var created = captureAll
                ? TryCreateSpan(entry.GetParameterValue(i), out var span)
                : TryCreateColoredSpan(entry, i, out span);
            if (created)
                scratch[count++] = span;
        }

        ParameterSpan[]? result = null;
        if (count > 0)
        {
            result = new ParameterSpan[count];
            Array.Copy(scratch, result, count);
        }

        ThreadBufferCache.ReturnSpans(scratch, count);
        return result;
    }

    /// <summary>
    /// Creates a span only for a value that gets its own color. The common number types are read as themselves,
    /// because reading them through <see cref="object"/> would box each one just to call ToString.
    /// </summary>
    private bool TryCreateColoredSpan(IZLoggerEntry entry, int index, out ParameterSpan span)
    {
        var type = entry.GetParameterType(index);
        if (type == typeof(int))
            return CreateNumberSpan(entry.GetParameterValue<int>(index).ToString(), type, out span);
        if (type == typeof(long))
            return CreateNumberSpan(entry.GetParameterValue<long>(index).ToString(), type, out span);
        if (type == typeof(double))
            return CreateNumberSpan(entry.GetParameterValue<double>(index).ToString(CultureInfo.CurrentCulture), type, out span);

        span = default;
        if (type == typeof(string))
            return false;

        var value = entry.GetParameterValue(index);
        return value is not null
               && ParameterSpan.MapClrType(value.GetType()) != SemanticStyle.Text
               && TryCreateSpan(value, out span);
    }

    private static bool CreateNumberSpan(string text, Type type, out ParameterSpan span)
    {
        span = new ParameterSpan { FormattedValue = text, SemanticStyle = SemanticStyle.JsonNumber, ClrType = type };
        return true;
    }

    private bool TryCreateSpan(object? value, out ParameterSpan span)
    {
        span = default;
        if (value is null)
            return false;

        var type = value.GetType();
        var formatted = FormatParameter(type, value);
        if (string.IsNullOrEmpty(formatted))
            return false;

        span = new ParameterSpan
        {
            FormattedValue = formatted!,
            SemanticStyle = ParameterSpan.MapClrType(type),
            ClrType = type
        };
        return true;
    }

    private string? FormatParameter(Type type, object value)
    {
        try
        {
            var serializer = _parameterSerializer;
            return serializer is not null && serializer.CanSerialize(type)
                ? serializer.FormatText(value)
                : value.ToString();
        }
        catch
        {
            return $"<{type.Name}>";
        }
    }
}
