using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LookupEngine;
using LookupEngine.Abstractions;
using LookupEngine.Abstractions.Enums;
using ZLogger.Scintilla.Models;
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
namespace ZLogger.Scintilla.Pipeline;

internal sealed class ScintillaLogProcessor : IAsyncLogProcessor
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _textBuffer;
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _jsonBuffer;

    private static readonly DecomposeOptions LogDecomposeOptions = new()
    {
        IncludeRoot = false,
        IncludeFields = false,
        IncludeEvents = false,
        IncludeUnsupported = false,
        IncludePrivateMembers = false,
        IncludeStaticMembers = false,
        EnableExtensions = false,
        EnableRedirection = false
    };

    private readonly ZLoggerOptions _options;
    private readonly IZLoggerFormatter _formatter;
    private readonly Func<LogEntry, bool> _tryPost;
    private int _disposed;
    private volatile bool _prettyJsonEnabled;

    internal ScintillaLogProcessor(ZLoggerOptions options, Func<LogEntry, bool> tryPost)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _formatter = options.CreateFormatter();
        _tryPost = tryPost ?? throw new ArgumentNullException(nameof(tryPost));
    }

    internal void SetPrettyJson(bool enabled) => _prettyJsonEnabled = enabled;

    public void Post(IZLoggerEntry log)
    {
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
        catch (Exception ex)
        {
            _options.InternalErrorLogger?.Invoke(ex);
        }
        finally
        {
            log.Return();
        }
    }

    private static byte[]? FormatJsonBytes(IZLoggerEntry log)
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
        catch
        {
            return null;
        }
    }

    private static bool TryWriteParameter(Utf8JsonWriter writer, IZLoggerEntry log, int index)
    {
        var value = log.GetParameterValue(index);
        if (value is null || value is Exception || ParameterSpan.IsLeafType(value.GetType()))
            return false;

        try
        {
            var decomposed = LookupComposer.Decompose(value, LogDecomposeOptions);
            if (decomposed.Members.Count == 0)
                return false;

            var key = log.GetParameterKeyAsString(index);
            writer.WritePropertyName(string.IsNullOrEmpty(key) ? $"arg{index}" : key);
            WriteDecomposedJson(writer, decomposed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int MaxDecomposeDepth = 2;

    private static void WriteDecomposedJson(Utf8JsonWriter writer, DecomposedObject decomposed, int depth = 0)
    {
        writer.WriteStartObject();
        writer.WriteString("$type", decomposed.TypeName);

        foreach (var member in decomposed.Members)
        {
            if ((member.MemberAttributes & MemberAttributes.Property) == 0) continue;
            writer.WritePropertyName(member.Name);
            WriteDecomposedValue(writer, member.Value, depth);
        }

        writer.WriteEndObject();
    }

    private static void WriteDecomposedValue(Utf8JsonWriter writer, DecomposedValue dv, int depth)
    {
        if (depth < MaxDecomposeDepth && TryWriteRecursiveDecompose(writer, dv, depth))
            return;

        WritePrimitiveValue(writer, dv.Name);
    }

    private static bool TryWriteRecursiveDecompose(Utf8JsonWriter writer, DecomposedValue dv, int depth)
    {
        var raw = dv.RawValue;
        if (raw is null)
            return false;

        if (ParameterSpan.IsLeafType(raw.GetType()))
            return false;

        if (raw is System.Collections.IEnumerable enumerable)
        {
            WriteEnumerable(writer, enumerable, depth);
            return true;
        }

        try
        {
            var child = LookupComposer.Decompose(raw, LogDecomposeOptions);
            if (child.Members.Count == 0)
                return false;

            WriteDecomposedJson(writer, child, depth + 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteEnumerable(Utf8JsonWriter writer, System.Collections.IEnumerable enumerable, int depth)
    {
        writer.WriteStartArray();
        var count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= 50) break;
            if (item is null)
            {
                writer.WriteNullValue();
                continue;
            }

            if (ParameterSpan.IsLeafType(item.GetType()))
            {
                WritePrimitiveValue(writer, item.ToString() ?? string.Empty);
                continue;
            }

            try
            {
                var child = LookupComposer.Decompose(item, LogDecomposeOptions);
                WriteDecomposedJson(writer, child, depth + 1);
            }
            catch
            {
                WritePrimitiveValue(writer, item.ToString() ?? string.Empty);
            }
        }
        writer.WriteEndArray();
    }

    private static void WritePrimitiveValue(Utf8JsonWriter writer, string name)
    {
        if (name is "null" or "")
        {
            writer.WriteNullValue();
            return;
        }

        if (bool.TryParse(name, out var boolVal))
        {
            writer.WriteBooleanValue(boolVal);
            return;
        }

        if (long.TryParse(name, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var longVal))
        {
            writer.WriteNumberValue(longVal);
            return;
        }

        if (double.TryParse(name, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var doubleVal))
        {
            writer.WriteNumberValue(doubleVal);
            return;
        }

        writer.WriteStringValue(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ArrayBufferWriter<byte> GetThreadTextBuffer()
    {
        _textBuffer ??= new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
        return _textBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ArrayBufferWriter<byte> GetThreadJsonBuffer()
    {
        _jsonBuffer ??= new ArrayBufferWriter<byte>(LogConstants.LogProcessorBufferCapacity);
        return _jsonBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] RentArray(int minimumLength)
    {
        return minimumLength <= 0 ? [] : ArrayPool<byte>.Shared.Rent(minimumLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
            var value = entry.GetParameterValue(i);
            if (value is null)
                continue;

            string? formatted;
            try { formatted = value.ToString(); }
            catch { formatted = null; }

            if (string.IsNullOrEmpty(formatted))
                continue;

            var type = value.GetType();
            result ??= new ParameterSpan[paramCount];
            result[count++] = new ParameterSpan
            {
                FormattedValue = formatted!,
                SemanticStyle = ParameterSpan.MapClrType(type),
                ClrType = type
            };
        }

        if (result is null)
            return null;

        if (count == result.Length)
            return result;

        var trimmed = new ParameterSpan[count];
        Array.Copy(result, trimmed, count);
        return trimmed;
    }
}
