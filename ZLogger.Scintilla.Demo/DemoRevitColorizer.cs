using System;
using System.Drawing;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Demo;

/// <summary>
/// Demo colorizer — highlights the word "aurecon" (case-insensitive) in a branded color.
/// </summary>
public sealed class DemoRevitColorizer : ILogColorizer
{
    private static readonly Color AureconColor = Color.FromArgb(120, 255, 170);
    private static readonly byte[] AureconBytes = { (byte)'a', (byte)'u', (byte)'r', (byte)'e', (byte)'c', (byte)'o', (byte)'n' };

    public bool TryGetColor(ReadOnlySpan<byte> utf8Token, out Color color)
    {
        color = default;
        if (utf8Token.IsEmpty)
            return false;

        var start = 0;
        var end = utf8Token.Length;
        while (start < end && utf8Token[start] == (byte)' ') start++;
        while (end > start && utf8Token[end - 1] == (byte)' ') end--;
        if (start >= end)
            return false;

        var trimmed = utf8Token.Slice(start, end - start);
        if (!IsAsciiEqualsIgnoreCase(trimmed, AureconBytes))
            return false;

        color = AureconColor;
        return true;
    }

    private static bool IsAsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if ((a[i] | 0x20) != (b[i] | 0x20))
                return false;
        }
        return true;
    }
}
