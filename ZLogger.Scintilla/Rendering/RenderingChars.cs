using ZLogger.Scintilla.Models;

namespace ZLogger.Scintilla.Rendering;

/// <summary>
/// Shared character sets and classification helpers for the rendering pipeline.
/// </summary>
internal static class RenderingChars
{
    public const string TokenSeparators = " \t\r\n,;:=()[]{}<>\"'|/\\";

    public static bool IsTokenSeparator(char ch) => TokenSeparators.IndexOf(ch) >= 0;

    public static bool IsJsonPunctuation(char ch) => ch is '{' or '}' or '[' or ']' or ',' or ':';

    public static bool TryAppendWhitespace(string text, RenderTokenWriter writer, ref int index)
    {
        if (!char.IsWhiteSpace(text[index]))
            return false;

        var start = index;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        writer.Add(text, start, index - start, SemanticStyle.SecondaryText);
        return true;
    }
}
