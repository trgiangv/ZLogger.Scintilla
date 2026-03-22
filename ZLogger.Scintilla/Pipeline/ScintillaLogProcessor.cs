using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LookupEngine;
using LookupEngine.Abstractions;
using LookupEngine.Abstractions.Enums;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
namespace ZLogger.Scintilla.Pipeline;

internal sealed class ScintillaLogProcessor : IAsyncLogProcessor
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _textBuffer;
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _jsonBuffer;

    private readonly IZLoggerFormatter _formatter = new ScintillaLogFormatter();
    private readonly Func<LogEntry, bool> _tryPost;
    private int _disposed;
    private volatile bool _prettyJsonEnabled;
    private volatile int _maxDecomposeDepth = 2;

    internal ScintillaLogProcessor(Func<LogEntry, bool> tryPost)
    {
        _tryPost = tryPost ?? throw new ArgumentNullException(nameof(tryPost));
    }

    internal void SetPrettyJson(bool enabled) => _prettyJsonEnabled = enabled;
    internal void SetJsonDepth(int depth) => _maxDecomposeDepth = depth > 0 ? depth : 2;

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

            var textWriter = GetThreadTextBuffer();
            textWriter.Clear();
            log.FormatUtf8(textWriter, _formatter);

            var writtenMemory = textWriter.WrittenMemory;
            var messageLength = writtenMemory.Length;

            byte[] rented;
            if (messageLength > 0)
            {
                rented = RentArray(messageLength);
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

            var properties = BuildProperties(log, paramSpans, jsonBytes);

            var logEntry = new LogEntry
            {
                Level = log.LogInfo.LogLevel,
                Message = new ArraySegment<byte>(rented, 0, messageLength),
                Properties = properties
            };

            logEntry.AttachBufferLease(ReleaseArray);

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
            var buffer = GetThreadJsonBuffer();
            buffer.Clear();

            var objectCount = 0;
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
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
        if (value is null || value is Exception || ParameterSpan.IsLeafType(value.GetType()))
            return false;

        var key = log.GetParameterKeyAsString(index);
        writer.WritePropertyName(string.IsNullOrEmpty(key) ? $"arg{index}" : key);

        if (value is System.Collections.IEnumerable enumerable)
        {
            WriteEnumerable(writer, enumerable, 0, _maxDecomposeDepth);
            return true;
        }

        try
        {
            var decomposed = LookupComposer.Decompose(value, DecomposeOptions.Default);
            if (decomposed.Members.Count == 0)
            {
                WriteJsonValue(writer, value); return true;
            }
            WriteDecomposedJson(writer, decomposed, 0, _maxDecomposeDepth);
            return true;
        }
        catch
        {
            WriteJsonValue(writer, value);
            return true;
        }
    }

    private static void WriteDecomposedJson(Utf8JsonWriter writer, DecomposedObject decomposed, int depth, int maxDepth)
    {
        writer.WriteStartObject();
        writer.WriteString("$type", decomposed.TypeName);

        foreach (var member in decomposed.Members)
        {
            if ((member.MemberAttributes & MemberAttributes.Property) == 0) continue;
            writer.WritePropertyName(member.Name);
            WriteDecomposedValue(writer, member.Value, depth, maxDepth);
        }

        writer.WriteEndObject();
    }

    private static void WriteDecomposedValue(Utf8JsonWriter writer, DecomposedValue dv, int depth, int maxDepth)
    {
        var raw = dv.RawValue;

        if (raw is null)
        {
            writer.WriteNullValue(); return;
        }
        if (depth >= maxDepth || ParameterSpan.IsLeafType(raw.GetType()))
        {
            WriteJsonValue(writer, raw); return;
        }
        if (raw is System.Collections.IEnumerable enumerable)
        {
            WriteEnumerable(writer, enumerable, depth, maxDepth); return;
        }

        try
        {
            var child = LookupComposer.Decompose(raw, DecomposeOptions.Default);
            if (child.Members.Count > 0)
            {
                WriteDecomposedJson(writer, child, depth + 1, maxDepth); return;
            }
        }
        catch
        {
            // ignored
        }

        WriteJsonValue(writer, raw);
    }

    private static void WriteEnumerable(Utf8JsonWriter writer, System.Collections.IEnumerable enumerable, int depth, int maxDepth)
    {
        writer.WriteStartArray();
        var count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= 50) break;
            WriteElementValue(writer, item, depth, maxDepth);
        }
        writer.WriteEndArray();
    }

    private static void WriteElementValue(Utf8JsonWriter writer, object? item, int depth, int maxDepth)
    {
        if (item is null)
        {
            writer.WriteNullValue(); return;
        }
        if (ParameterSpan.IsLeafType(item.GetType()))
        {
            WriteJsonValue(writer, item); return;
        }

        try
        {
            var child = LookupComposer.Decompose(item, DecomposeOptions.Default);
            WriteDecomposedJson(writer, child, depth + 1, maxDepth);
        }
        catch
        {
            WriteJsonValue(writer, item);
        }
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value)
    {
        if (value is IConvertible)
        {
            try
            {
                JsonSerializer.Serialize(writer, value, value.GetType());
                return;
            }
            catch
            {
                 // ignored
            }
        }

        writer.WriteStringValue(value.ToString());
    }

    private static ArrayBufferWriter<byte> GetThreadTextBuffer()
    {
        _textBuffer ??= new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
        return _textBuffer;
    }

    private static ArrayBufferWriter<byte> GetThreadJsonBuffer()
    {
        _jsonBuffer ??= new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
        return _jsonBuffer;
    }

    private static byte[] RentArray(int minimumLength)
    {
        return minimumLength <= 0 ? [] : ArrayPool<byte>.Shared.Rent(minimumLength);
    }

    private static void ReleaseArray(byte[] buffer)
    {
        if (buffer.Length == 0)
            return;

        ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return default;
    }

    private static IReadOnlyDictionary<string, object?> BuildProperties(
        IZLoggerEntry entry,
        ParameterSpan[]? paramSpans,
        byte[]? jsonBytes)
    {
        var scopeState = entry.LogInfo.ScopeState;
        var scopeCount = scopeState is { Properties.IsEmpty: false } ? scopeState.Properties.Length : 0;
        var totalCapacity = scopeCount
            + (paramSpans is { Length: > 0 } ? 1 : 0)
            + (jsonBytes is not null ? 1 : 0);

        if (totalCapacity == 0)
            return LogEntry.EmptyProperties;

        var properties = new Dictionary<string, object?>(totalCapacity, StringComparer.Ordinal);

        if (scopeCount > 0)
            AddScopeProperties(scopeState!, properties);

        if (paramSpans is { Length: > 0 })
            properties[LogConstants.ParameterSpans] = paramSpans;

        if (jsonBytes is not null)
            properties[LogConstants.JsonBytes] = jsonBytes;

        return properties.Count == 0 ? LogEntry.EmptyProperties : properties;
    }

    private static void AddScopeProperties(LogScopeState scopeState, Dictionary<string, object?> properties)
    {
        var scopeProperties = scopeState.Properties;
        foreach (var item in scopeProperties)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
                properties[item.Key] = item.Value;
        }
    }

    private static ParameterSpan[]? ExtractParameterSpans(IZLoggerEntry entry)
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

    private static bool TryCreateSpan(object? value, out ParameterSpan span)
    {
        span = default;
        if (value is null) return false;

        var formatted = value.ToString();

        if (string.IsNullOrEmpty(formatted)) return false;

        var type = value.GetType();
        span = new ParameterSpan
        {
            FormattedValue = formatted,
            SemanticStyle = ParameterSpan.MapClrType(type),
            ClrType = type
        };
        return true;
    }
}
