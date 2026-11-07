using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.LogStates;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Rendering;

/// <summary>
/// ZLogger <see cref="IZLoggerFormatter"/> producing <c>[HH:mm:ss LVL] [Category] {message}</c>,
/// with the time in local time.
/// </summary>
/// <remarks>
/// The message body comes from ZLogger's own <c>entry.ToString(writer)</c>, which is the fastest path and
/// honours format specifiers, alignment and brace escapes. It is avoided in two cases:
/// <list type="bullet">
/// <item>A parameter is a non-<see cref="IFormattable"/> <see cref="IEnumerable"/>. ZLogger would serialize it
/// with <c>JsonSerializer</c> (no cycle handling, no depth limit), which fails on Revit API objects and
/// floods the line with inline JSON. It prints through <c>ToString()</c> here, and pretty JSON is handled
/// separately by <see cref="Pipeline.ScintillaLogProcessor"/>.</item>
/// <item>A parameter's type belongs to the registered <see cref="ICustomSerializer"/>.</item>
/// </list>
/// Those messages, and any message whose <c>ToString</c> throws, go through <see cref="WriteParameterByParameter"/>.
/// </remarks>
internal sealed class ScintillaLogFormatter : IZLoggerFormatter
{
    /// <summary><c>[HH:mm:ss LVL] </c></summary>
    private const int TimePrefixLength = 15;

    [ThreadStatic]
    private static long _offsetMinute;
    [ThreadStatic]
    private static long _offsetTicks;

    internal volatile ICustomSerializer? ParameterSerializer;

    private static ReadOnlySpan<byte> CommaSpace       => ", "u8;
    private static ReadOnlySpan<byte> NewLine          => "\n"u8;

    /// <summary>
    /// <see langword="false"/> — <see cref="Pipeline.BatchPreparer"/> owns the line terminator;
    /// ZLogger's stream infrastructure must not add a second one.
    /// </summary>
    public bool WithLineBreak => false;

    /// <inheritdoc />
    public void FormatLogEntry(IBufferWriter<byte> writer, IZLoggerEntry entry)
    {
        var info = entry.LogInfo;
        WriteHeader(writer, in info);
        WriteMessageBody(writer, entry, in info);

        var ex = info.Exception;
        if (ex is not null)
        {
            writer.Write(NewLine);
            WriteUtf8(writer, SafeExceptionText(ex).AsSpan());
        }
    }

    /// <summary>Writes the line for an entry whose formatting failed, so that the entry is not lost.</summary>
    internal static void FormatFailure(IBufferWriter<byte> writer, IZLoggerEntry entry, Exception failure)
    {
        var info = entry.LogInfo;
        WriteHeader(writer, in info);
        WriteUtf8(writer, $"<log entry could not be formatted: {failure.GetType().Name}>".AsSpan());
    }

    /// <summary>Writes <c>[HH:mm:ss LVL] [Category] </c> with a single request to the writer, which is an interface call.</summary>
    private static void WriteHeader(IBufferWriter<byte> writer, in LogInfo info)
    {
        var category = info.Category.Utf8Span;
        var length = TimePrefixLength + (category.IsEmpty ? 0 : category.Length + 3);
        var span = writer.GetSpan(length);

        span[0] = (byte)'[';
        WriteLocalTime(span.Slice(1), info.Timestamp.Utc);
        span[9] = (byte)' ';
        GetLevelAbbreviation(info.LogLevel).CopyTo(span.Slice(10));
        span[13] = (byte)']';
        span[14] = (byte)' ';

        if (!category.IsEmpty)
        {
            span[15] = (byte)'[';
            category.CopyTo(span.Slice(16));
            span[16 + category.Length] = (byte)']';
            span[17 + category.Length] = (byte)' ';
        }

        writer.Advance(length);
    }

    private void WriteMessageBody(IBufferWriter<byte> writer, IZLoggerEntry entry, in LogInfo info)
    {
        if (!NeedsParameterByParameter(entry) && TryWriteWholeMessage(writer, entry, in info))
            return;

        var format = entry.GetOriginalFormat();
        if (string.IsNullOrEmpty(format))
            WriteValuesOnly(writer, entry);
        else
            WriteParameterByParameter(writer, entry, format);
    }

    /// <summary>
    /// Lets ZLogger format the whole message. Returns false when that failed and the parameters are worth
    /// another try one at a time.
    /// </summary>
    private static bool TryWriteWholeMessage(IBufferWriter<byte> writer, IZLoggerEntry entry, in LogInfo info)
    {
        try
        {
            entry.ToString(writer);
            return true;
        }
        catch
        {
            // A throwing ToString can leave part of a message behind. The processor's writer can start over,
            // and this is the only writer used outside of tests, so the cost stays on this failure path.
            if (writer is ArrayBufferWriter<byte> restartable)
            {
                restartable.Clear();
                WriteHeader(writer, in info);
            }

            if (entry.ParameterCount != 0)
                return false;

            WriteUtf8(writer, "<message could not be formatted>".AsSpan());
            return true;
        }
    }

    private bool NeedsParameterByParameter(IZLoggerEntry entry)
    {
        var count = entry.ParameterCount;
        if (count == 0)
            return false;

        var serializer = ParameterSerializer;
        for (var i = 0; i < count; i++)
        {
            if (ParameterNeedsOwnFormatting(entry, i, serializer))
                return true;
        }

        return false;
    }

    private static bool ParameterNeedsOwnFormatting(IZLoggerEntry entry, int index, ICustomSerializer? serializer)
    {
        var declared = entry.GetParameterType(index);
        if (serializer is null && (declared.IsPrimitive || declared == typeof(string)))
            return false;

        // The declared type can be object or an interface, so only the value tells the truth.
        var value = entry.GetParameterValue(index);
        return value switch
        {
            null => false,
            IEnumerable and not string and not IFormattable => true,
            _ => serializer is not null && serializer.CanSerialize(value.GetType())
        };
    }

    /// <summary>
    /// Substitutes parameters one at a time, so that one failing <c>ToString</c> costs only its own text.
    /// The two original-format grammars differ: templates use <c>{name,align:format}</c> and escape braces
    /// by doubling them, while interpolated strings report <c>{name:align,format}</c> with raw literals.
    /// </summary>
    private void WriteParameterByParameter(IBufferWriter<byte> writer, IZLoggerEntry entry, string format)
    {
        var interpolated = IsInterpolated(entry);
        var position = 0;
        for (var i = 0; i < entry.ParameterCount; i++)
        {
            var open = WriteUpToHole(writer, entry, i, format, position, interpolated);
            if (open < 0)
                return;

            var close = format.IndexOf('}', open);
            if (close < 0)
            {
                WriteUtf8(writer, format.AsSpan(open));
                return;
            }

            var hole = format.Substring(open + 1, close - open - 1);
            ParseHole(hole, interpolated, out var alignment, out var specifier);
            WriteAligned(writer, ResolveParameterText(entry, i, specifier), alignment);
            position = close + 1;
        }

        if (position >= format.Length)
            return;
        if (interpolated)
            WriteUtf8(writer, format.AsSpan(position));
        else
            WriteTemplateLiteral(writer, format, position);
    }

    /// <summary>
    /// Writes the literal text before parameter <paramref name="index"/>'s hole and returns where the hole opens.
    /// Returns -1 when there is no such hole, after writing the rest of the format.
    /// </summary>
    private static int WriteUpToHole(IBufferWriter<byte> writer, IZLoggerEntry entry, int index, string format, int position, bool interpolated)
    {
        if (!interpolated)
            return WriteTemplateLiteral(writer, format, position);

        var open = FindInterpolatedHole(format, entry.GetParameterKeyAsString(index), position);
        if (open >= 0)
            WriteUtf8(writer, format.AsSpan(position, open - position));
        else if (position < format.Length)
            WriteUtf8(writer, format.AsSpan(position));
        return open;
    }

    private static bool IsInterpolated(IZLoggerEntry entry) =>
        entry.GetType() is { IsGenericType: true } type
        && type.GetGenericArguments()[0] == typeof(InterpolatedStringLogState);

    private static int FindInterpolatedHole(string format, string name, int from)
    {
        var search = from;
        while (true)
        {
            var open = format.IndexOf('{', search);
            if (open < 0)
                return -1;

            var after = open + 1 + name.Length;
            if (after < format.Length
                && string.CompareOrdinal(format, open + 1, name, 0, name.Length) == 0
                && format[after] is '}' or ':' or ',')
                return open;

            search = open + 1;
        }
    }

    /// <summary>Writes literal text with doubled braces unescaped. Returns the next hole's start, or -1 at the end.</summary>
    private static int WriteTemplateLiteral(IBufferWriter<byte> writer, string format, int from)
    {
        var start = from;
        var i = from;
        while (i < format.Length)
        {
            var ch = format[i];
            if (ch is not ('{' or '}'))
            {
                i++;
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == ch)
            {
                WriteUtf8(writer, format.AsSpan(start, i + 1 - start));
                i += 2;
                start = i;
                continue;
            }

            if (ch == '{')
            {
                WriteUtf8(writer, format.AsSpan(start, i - start));
                return i;
            }

            i++;
        }

        WriteUtf8(writer, format.AsSpan(start));
        return -1;
    }

    private static void ParseHole(string hole, bool interpolated, out int alignment, out string? specifier)
    {
        alignment = 0;
        specifier = null;

        var (alignmentMark, specifierMark) = interpolated ? (':', ',') : (',', ':');
        var alignmentAt = hole.IndexOf(alignmentMark);
        var specifierAt = hole.IndexOf(specifierMark);

        if (specifierAt >= 0 && specifierAt + 1 < hole.Length)
            specifier = hole.Substring(specifierAt + 1);

        // A template specifier can contain the alignment mark, so only an earlier one counts.
        if (alignmentAt >= 0 && (specifierAt < 0 || alignmentAt < specifierAt))
        {
            var end = specifierAt > alignmentAt ? specifierAt : hole.Length;
            if (int.TryParse(hole.Substring(alignmentAt + 1, end - alignmentAt - 1), out var parsed))
                alignment = parsed;
        }
    }

    private static void WriteAligned(IBufferWriter<byte> writer, string text, int alignment)
    {
        var pad = Math.Abs(alignment) - text.Length;
        if (pad > 0 && alignment > 0)
            WriteUtf8(writer, new string(' ', pad).AsSpan());
        WriteUtf8(writer, text.AsSpan());
        if (pad > 0 && alignment < 0)
            WriteUtf8(writer, new string(' ', pad).AsSpan());
    }

    private string ResolveParameterText(IZLoggerEntry entry, int index, string? specifier)
    {
        try
        {
            var value = entry.GetParameterValue(index);
            if (value is null)
                return "null";

            var serializer = ParameterSerializer;
            if (serializer is not null && serializer.CanSerialize(value.GetType()))
                return serializer.FormatText(value) ?? "null";

            // Application overrides of ToString can return null whatever the annotations say, and a null here
            // would fail outside this try block.
            // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            if (specifier is not null && value is IFormattable formattable)
                return formattable.ToString(specifier, null) ?? string.Empty;

            return value.ToString() ?? string.Empty;
            // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        }
        catch
        {
            return $"<{entry.GetParameterType(index).Name}>";
        }
    }

    private void WriteValuesOnly(IBufferWriter<byte> writer, IZLoggerEntry entry)
    {
        for (var i = 0; i < entry.ParameterCount; i++)
        {
            if (i > 0)
                writer.Write(CommaSpace);
            WriteUtf8(writer, ResolveParameterText(entry, i, null).AsSpan());
        }
    }

    private static string SafeExceptionText(Exception ex)
    {
        try
        {
            return ex.ToString();
        }
        catch
        {
            return ex.GetType().FullName ?? nameof(Exception);
        }
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
            return;
        var span = writer.GetSpan(Encoding.UTF8.GetMaxByteCount(chars.Length));
        writer.Advance(Encoding.UTF8.GetBytes(chars, span));
    }

    /// <summary>
    /// Converts with an offset cached per minute. A time zone lookup costs about as much as the rest of the
    /// header, and the offset can only change on a minute boundary.
    /// </summary>
    private static void WriteLocalTime(Span<byte> buf, DateTimeOffset utc)
    {
        var minute = utc.Ticks / TimeSpan.TicksPerMinute;
        if (minute != _offsetMinute)
        {
            _offsetTicks = TimeZoneInfo.Local.GetUtcOffset(utc).Ticks;
            _offsetMinute = minute;
        }

        var secondOfDay = (int)((utc.Ticks + _offsetTicks) / TimeSpan.TicksPerSecond % 86_400);
        WriteTwoDigits(buf, 0, secondOfDay / 3600);
        buf[2] = (byte)':';
        WriteTwoDigits(buf, 3, secondOfDay / 60 % 60);
        buf[5] = (byte)':';
        WriteTwoDigits(buf, 6, secondOfDay % 60);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTwoDigits(Span<byte> buf, int offset, int value)
    {
        buf[offset]     = (byte)('0' + value / 10);
        buf[offset + 1] = (byte)('0' + value % 10);
    }

    private static ReadOnlySpan<byte> GetLevelAbbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace       => "TRC"u8,
        LogLevel.Debug       => "DBG"u8,
        LogLevel.Information => "INF"u8,
        LogLevel.Warning     => "WRN"u8,
        LogLevel.Error       => "ERR"u8,
        _                    => "CRT"u8
    };
}
