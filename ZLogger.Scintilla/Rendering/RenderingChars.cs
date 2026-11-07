#if NET8_0_OR_GREATER
using System.Buffers;
using System.Text;
#endif
namespace ZLogger.Scintilla.Rendering;

/// <summary>
/// Byte classification for UTF-8 text. Every character tested here is ASCII,
/// and no byte of a multi-byte character can equal an ASCII byte.
/// </summary>
internal static class RenderingChars
{
    private const string TokenSeparators = " \t\r\n,;:=()[]{}<>\"'|/\\";

#if NET8_0_OR_GREATER
    private static readonly SearchValues<byte> TokenSeparatorBytes =
        SearchValues.Create(Encoding.ASCII.GetBytes(TokenSeparators));
#endif

    public static bool IsTokenSeparator(byte value) => value < 0x80 && TokenSeparators.Contains((char)value);

    public static int IndexOfTokenSeparator(ReadOnlySpan<byte> text)
    {
#if NET8_0_OR_GREATER
        return text.IndexOfAny(TokenSeparatorBytes);
#else
        for (var i = 0; i < text.Length; i++)
        {
            if (IsTokenSeparator(text[i]))
                return i;
        }

        return -1;
#endif
    }

    public static bool IsAsciiLetter(byte value) => (uint)((value | 0x20) - 'a') <= 'z' - 'a';

    public static bool IsAsciiDigit(byte value) => (uint)(value - '0') <= 9;
}
