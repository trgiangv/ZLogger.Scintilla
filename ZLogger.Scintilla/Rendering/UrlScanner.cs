using System.Diagnostics;
using System.Text;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Rendering;

/// <summary>Finds URLs in UTF-8 text and turns the safe ones into clickable links.</summary>
internal static class UrlScanner
{
    public static void FindAll(ReadOnlySpan<byte> text, List<(int Start, int Length)> output)
    {
        output.Clear();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = FindNextStart(text, cursor);
            if (start < 0)
                return;

            var length = MeasureUrl(text, start);
            if (length <= 0)
                return;

            output.Add((start, length));
            cursor = start + length;
        }
    }

    /// <summary>Creates a link that opens <paramref name="utf8Url"/> if it is safe to open.</summary>
    public static bool TryCreateLink(ReadOnlySpan<byte> utf8Url, out TokenPayload? payload)
    {
        payload = null;
        if (!TryNormalizeUri(Encoding.UTF8.GetString(utf8Url), out var target))
            return false;

        payload = TokenPayload.ForLink(() => OpenUrl(target));
        return true;
    }

    public static bool TryNormalizeUri(string candidate, out string targetUri)
    {
        targetUri = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var trimmed = candidate.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            if (!IsSafeToOpen(absolute))
                return false;

            targetUri = absolute.AbsoluteUri;
            return true;
        }

        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var wwwUri))
        {
            targetUri = wwwUri.AbsoluteUri;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Log text can come from anywhere, and a click shell-executes the link. Other schemes can start
    /// programs or open local files, so they stay plain text. A host that needs them can use an
    /// <see cref="Public.ILinkifier"/>, which keeps that decision in the host's own code.
    /// </summary>
    private static bool IsSafeToOpen(Uri uri) => uri.Scheme is "http" or "https";

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // A missing browser must not take the viewer down with it.
        }
    }

    private static int FindNextStart(ReadOnlySpan<byte> text, int from)
    {
        var scheme = FindSchemeStart(text, from);
        var www = FindWwwStart(text, from);
        if (scheme < 0)
            return www;
        return www < 0 ? scheme : Math.Min(scheme, www);
    }

    private static int FindSchemeStart(ReadOnlySpan<byte> text, int from)
    {
        var search = from;
        while (search < text.Length)
        {
            var found = text.Slice(search).IndexOf("://"u8);
            if (found < 0)
                return -1;

            var mark = search + found;
            var start = mark;
            while (start > from && IsSchemeByte(text[start - 1]))
                start--;

            if (start < mark)
                return start;

            search = mark + 3;
        }

        return -1;
    }

    private static int FindWwwStart(ReadOnlySpan<byte> text, int from)
    {
        var search = from;
        while (search < text.Length)
        {
            var found = text.Slice(search).IndexOf("www."u8);
            if (found < 0)
                return -1;

            var start = search + found;
            var startsWord = start == from || !IsWordByte(text[start - 1]);
            if (startsWord && start + 4 < text.Length && RenderingChars.IsAsciiLetter(text[start + 4]))
                return start;

            search = start + 4;
        }

        return -1;
    }

    private static int MeasureUrl(ReadOnlySpan<byte> text, int start)
    {
        var end = start;
        while (end < text.Length && !EndsUrl(text[end]))
            end++;

        while (end > start && IsTrailingPunctuation(text[end - 1]))
            end--;

        return end - start;
    }

    private static bool IsSchemeByte(byte value) =>
        IsWordByte(value) || value is (byte)'+' or (byte)'-' or (byte)'.';

    private static bool IsWordByte(byte value) =>
        RenderingChars.IsAsciiLetter(value) || RenderingChars.IsAsciiDigit(value);

    private static bool EndsUrl(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'
            or (byte)'<' or (byte)'>' or (byte)'"' or (byte)'\'' or (byte)'[' or (byte)']';

    private static bool IsTrailingPunctuation(byte value) =>
        value is (byte)'.' or (byte)',' or (byte)';' or (byte)':' or (byte)'!' or (byte)'?';
}
