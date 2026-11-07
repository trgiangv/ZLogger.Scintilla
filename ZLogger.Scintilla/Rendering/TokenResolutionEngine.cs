using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Rendering;

internal sealed class TokenResolutionEngine(ILogColorizer? colorizer, ILinkifier? linkifier, Action<Exception>? errorSink)
{
    public bool HasEnrichers => colorizer is not null || linkifier is not null;

    /// <summary>
    /// Attempts to resolve a token. Resolution order:
    ///   1. Try the <see cref="ILinkifier"/> — a match makes it a link.
    ///   2. Otherwise try the <see cref="ILogColorizer"/> — a match gives it a color.
    /// </summary>
    public bool TryResolve(in RenderContext context, ReadOnlySpan<byte> utf8Token, out RenderSegment segment)
    {
        segment = default;
        try
        {
            if (linkifier is not null && linkifier.TryMatch(utf8Token, context, out var onClick))
            {
                segment = new RenderSegment(utf8Token.Length, SemanticStyle.TokenLink, TokenPayload.ForLink(onClick!), isLink: true);
                return true;
            }

            if (colorizer is null || !colorizer.TryGetColor(utf8Token, out var color))
                return false;

            segment = new RenderSegment(utf8Token.Length, SemanticStyle.TokenClassified, TokenPayload.ForColorizer(color));
            return true;
        }
        catch (Exception ex)
        {
            errorSink?.Invoke(ex);
            return false;
        }
    }
}
