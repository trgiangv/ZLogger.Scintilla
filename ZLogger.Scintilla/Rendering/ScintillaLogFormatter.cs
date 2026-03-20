using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
namespace ZLogger.Scintilla.Rendering;

/// <summary>
/// ZLogger <see cref="IZLoggerFormatter"/> that bypasses ZLogger's internal
/// <c>CodeGeneratorUtil.AppendAsJson</c> for <see cref="System.Collections.IEnumerable"/> parameters.
/// </summary>
/// <remarks>
/// Output format: <c>[HH:mm:ss.fff LVL] [Category] {message}</c>.
/// <para>
/// ZLogger's default <c>entry.ToString(writer)</c> auto-serialises non-<see cref="IFormattable"/>
/// <see cref="System.Collections.IEnumerable"/> values with <c>JsonSerializer.Serialize</c>
/// (no <c>ReferenceHandler.IgnoreCycles</c>, no <c>MaxDepth</c>).
/// This causes cycle exceptions for Revit API objects and unwanted inline JSON for collections.
/// </para>
/// <para>
/// Instead, when parameters exist, we parse <see cref="IZLoggerFormattable.GetOriginalFormat"/>
/// and substitute each placeholder with <c>GetParameterValue(i).ToString()</c>.
/// Pretty-print JSON is handled separately by <see cref="Pipeline.ScintillaLogProcessor"/>.
/// </para>
/// </remarks>
internal sealed class ScintillaLogFormatter : IZLoggerFormatter
{
    private static ReadOnlySpan<byte> OpenBracket      => "["u8;
    private static ReadOnlySpan<byte> CloseBracketSpace => "] "u8;
    private static ReadOnlySpan<byte> Space            => " "u8;
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

        writer.Write(OpenBracket);
        WriteTimestamp(writer, info.Timestamp.Utc);
        writer.Write(Space);
        writer.Write(GetLevelAbbreviation(info.LogLevel));
        writer.Write(CloseBracketSpace);

        var categoryBytes = info.Category.Utf8Span;
        if (!categoryBytes.IsEmpty)
        {
            writer.Write(OpenBracket);
            writer.Write(categoryBytes);
            writer.Write(CloseBracketSpace);
        }

        WriteMessageBody(writer, entry);

        var ex = info.Exception;
        if (ex is not null)
        {
            writer.Write(NewLine);
            WriteStringUtf8(writer, ex.ToString());
        }
    }

    private static void WriteMessageBody(IBufferWriter<byte> writer, IZLoggerEntry entry)
    {
        if (entry.ParameterCount == 0)
        {
            entry.ToString(writer);
            return;
        }

        var format = entry.GetOriginalFormat();
        if (string.IsNullOrEmpty(format))
            WriteFallbackValues(writer, entry);
        else
            SubstitutePlaceholders(writer, entry, format);
    }

    private static void SubstitutePlaceholders(IBufferWriter<byte> writer, IZLoggerEntry entry, string format)
    {
        var paramIdx = 0;
        var pos = 0;

        while (pos < format.Length)
        {
            var open = format.IndexOf('{', pos);
            if (open < 0)
            {
                WriteSpanUtf8(writer, format.AsSpan(pos));
                return;
            }

            if (open > pos)
                WriteSpanUtf8(writer, format.AsSpan(pos, open - pos));

            var close = format.IndexOf('}', open + 1);
            if (close < 0)
            {
                WriteSpanUtf8(writer, format.AsSpan(open));
                return;
            }

            if (paramIdx < entry.ParameterCount)
            {
                WriteStringUtf8(writer, ResolveParameterText(entry, paramIdx));
                paramIdx++;
            }

            pos = close + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ResolveParameterText(IZLoggerEntry entry, int paramIdx)
    {
        try
        {
            return entry.GetParameterValue(paramIdx)?.ToString() ?? "null";
        }
        catch
        {
            return $"<{entry.GetParameterType(paramIdx).Name}>";
        }
    }

    private static void WriteFallbackValues(IBufferWriter<byte> writer, IZLoggerEntry entry)
    {
        for (var i = 0; i < entry.ParameterCount; i++)
        {
            if (i > 0) writer.Write(CommaSpace);
            WriteStringUtf8(writer, ResolveParameterText(entry, i));
        }
    }

    private static void WriteSpanUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        var maxBytes = Encoding.UTF8.GetMaxByteCount(chars.Length);
        var span = writer.GetSpan(maxBytes);
        var written = Encoding.UTF8.GetBytes(chars, span);
        writer.Advance(written);
    }

    private static void WriteStringUtf8(IBufferWriter<byte> writer, string text)
    {
        WriteSpanUtf8(writer, text.AsSpan());
    }

    private static void WriteTimestamp(IBufferWriter<byte> writer, DateTimeOffset ts)
    {
        var buf = writer.GetSpan(12);
        WriteTwoDigits(buf, 0, ts.Hour);
        buf[2]  = (byte)':';
        WriteTwoDigits(buf, 3, ts.Minute);
        buf[5]  = (byte)':';
        WriteTwoDigits(buf, 6, ts.Second);
        buf[8]  = (byte)'.';
        WriteThreeDigits(buf, 9, ts.Millisecond);
        writer.Advance(12);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTwoDigits(Span<byte> buf, int offset, int value)
    {
        buf[offset]     = (byte)('0' + value / 10);
        buf[offset + 1] = (byte)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteThreeDigits(Span<byte> buf, int offset, int value)
    {
        buf[offset]     = (byte)('0' + value / 100);
        buf[offset + 1] = (byte)('0' + value / 10 % 10);
        buf[offset + 2] = (byte)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
