using System;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Rendering;

internal sealed class TokenResolutionEngine(ILogColorizer? colorizer, ILinkifier? linkifier, Action<Exception>? errorSink)
{
    public bool HasEnrichers => colorizer is not null || linkifier is not null;

    /// <summary>
    /// Attempts to resolve a token into a styled/linked segment.
    /// Resolution order:
    ///   1. Try each <see cref="ILinkifier"/> — first match wins (link + action).
    ///   2. If a linkifier matched, also try <see cref="ILogColorizer"/> for color override.
    ///   3. If no linkifier matched, try <see cref="ILogColorizer"/> alone (color-only).
    /// </summary>
    public bool TryResolveToken(
        RenderContext context,
        string message,
        int start,
        int length,
        RenderTokenWriter writer)
    {
        var directBytes = TrySliceAsciiTokenBytes(context, message, start, length);

        if (!directBytes.IsEmpty)
        {
            try
            {
                return TryResolveSpan(context, message, start, length, directBytes.Span, writer);
            }
            catch (Exception ex)
            {
                errorSink?.Invoke(ex);
                return false;
            }
        }

        if (length == 0)
            return false;

        var maxBytes = Encoding.UTF8.GetMaxByteCount(length);
        using var encodedOwner = SpanOwner<byte>.Allocate(maxBytes);
        var written = Encoding.UTF8.GetBytes(message.AsSpan(start, length), encodedOwner.Span[..maxBytes]);
        var tokenSpan = encodedOwner.Span[..written];

        try
        {
            return TryResolveSpan(context, message, start, length, tokenSpan, writer);
        }
        catch (Exception ex)
        {
            errorSink?.Invoke(ex);
            return false;
        }
    }

    private bool TryResolveSpan(RenderContext context, string message, int start, int length, ReadOnlySpan<byte> tokenSpan, RenderTokenWriter writer)
    {
        if (linkifier is not null && linkifier.TryMatch(tokenSpan, context, out var onClick))
        {
            writer.Add(message, start, length, SemanticStyle.TokenLink, TokenPayload.ForLink(onClick!), isLink: true);
            return true;
        }

        if (colorizer is null || !colorizer.TryGetColor(tokenSpan, out var color)) return false;
        writer.Add(message, start, length, SemanticStyle.TokenClassified, TokenPayload.ForColorizer(color));
        return true;
    }

    private static ReadOnlyMemory<byte> TrySliceAsciiTokenBytes(
        RenderContext context, string message, int charStart, int length)
    {
        if (length == 0 || context.MessageBytes.IsEmpty)
            return default;

        var msgBytes = context.MessageBytes;
        var byteEnd = charStart + length;
        if (byteEnd > msgBytes.Length)
            return default;

        var slice = msgBytes.Slice(charStart, length);
        var span = slice.Span;

        for (var i = 0; i < length; i++)
        {
            if (span[i] != message[charStart + i])
                return default;
        }

        return slice;
    }
}
