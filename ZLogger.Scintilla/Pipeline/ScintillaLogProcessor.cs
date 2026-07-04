using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LookupEngine;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable RedundantSuppressNullableWarningExpression
namespace ZLogger.Scintilla.Pipeline;

internal sealed class ScintillaLogProcessor : IAsyncLogProcessor
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _textBuffer;
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _jsonBuffer;

    private readonly ScintillaLogFormatter _formatter = new();
    private readonly Func<LogEntry, bool> _tryPost;
    private int _disposed;
    private volatile bool _prettyJsonEnabled;
    private volatile int _maxDecomposeDepth = LogConstants.DefaultDecomposeDepth;
    private volatile int _maxCollectionItems = LogConstants.DefaultDecomposeCollection;
    private volatile ICustomSerializer? _parameterSerializer;

    internal ScintillaLogProcessor(Func<LogEntry, bool> tryPost)
    {
        _tryPost = tryPost ?? throw new ArgumentNullException(nameof(tryPost));
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

        try
        {
            ParameterSpan[]? paramSpans = null;
            if (log.ParameterCount > 0)
                paramSpans = ExtractParameterSpans(log);

            var textWriter = _textBuffer ??= new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
            textWriter.Clear();
            log.FormatUtf8(textWriter, _formatter);

            var writtenMemory = textWriter.WrittenMemory;
            var messageLength = writtenMemory.Length;

            byte[] rented;
            if (messageLength > 0)
            {
                rented = LogProcessorUtils.RentArray(messageLength);
                writtenMemory.Span.CopyTo(rented);
            }
            else
            {
                rented = [];
                messageLength = 0;
            }

            byte[]? jsonBytes = null;
            if (_prettyJsonEnabled)
                jsonBytes = FormatJsonBytes(log);

            var properties = log.BuildProperties(paramSpans, jsonBytes);

            var logEntry = new LogEntry
            {
                Level = log.LogInfo.LogLevel,
                Message = new ArraySegment<byte>(rented, 0, messageLength),
                Properties = properties
            };

            logEntry.AttachBufferLease(LogProcessorUtils.ReleaseArray);

            if (!_tryPost(logEntry))
                logEntry.ReleaseBuffer();
        }
        catch
        {
            // ignored
        }
        finally
        {
            log.Return();
        }
    }

    private byte[]? FormatJsonBytes(IZLoggerEntry log)
    {
        if (log.ParameterCount == 0)
            return null;

        try
        {
            var buffer = _jsonBuffer ??= new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
            buffer.Clear();

            var objectCount = 0;
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, SkipValidation = true });
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

    private bool TryFormatCustomSerializer(Type type, object? value, out string? formatted)
    {
        formatted = null;
        var serializer = _parameterSerializer;
        if (serializer is null || !serializer.CanSerialize(type))
            return false;

        try
        {
            formatted = serializer.FormatText(value);
        }
        catch
        {
            formatted = type.FullName;
        }
        return formatted is not null;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return default;
    }

    private ParameterSpan[]? ExtractParameterSpans(IZLoggerEntry entry)
    {
        var paramCount = entry.ParameterCount;
        ParameterSpan[]? result = null;
        var count = 0;

        for (var i = 0; i < paramCount; i++)
        {
            if (!TryCreateSpan(entry.GetParameterValue(i), out var span)) continue;
            result ??= new ParameterSpan[paramCount];
            result[count++] = span;
        }

        if (result is null) return null;
        if (count == result.Length) return result;

        var trimmed = new ParameterSpan[count];
        Array.Copy(result, trimmed, count);
        return trimmed;
    }

    private bool TryCreateSpan(object? value, out ParameterSpan span)
    {
        span = default;
        if (value is null) return false;

        var type = value.GetType();

        if (TryFormatCustomSerializer(type, value, out var formatted))
        {
           return true;
        }
        
        formatted = value.ToString();

        if (string.IsNullOrEmpty(formatted)) return false;

        span = new ParameterSpan
        {
            FormattedValue = formatted!,
            SemanticStyle = ParameterSpan.MapClrType(type),
            ClrType = type
        };
        return true;
    }
}
