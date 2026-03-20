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
    private static ReadOnlySpan<byte> Aurecon => "aurecon"u8;

    public bool TryGetColor(ReadOnlySpan<byte> utf8Token, out Color color)
    {
        color = default;
        var trimmed = utf8Token.Trim((byte)' ');
        if (trimmed.IsEmpty)
            return false;

        if (!IsAsciiEqualsIgnoreCase(trimmed, Aurecon))
            return false;

        color = AureconColor;
        return true;
    }

    private static bool IsAsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
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
