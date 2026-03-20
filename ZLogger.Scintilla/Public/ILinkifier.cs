using System;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Recognizes a token span and assigns a clickable link action.
/// Register one or more linkifiers via <c>WithLinkify(...)</c> on the viewer builder.
/// Linkifier are tried in registration order; first match wins.
/// <para>
/// <b>Thread safety:</b> Implementations <b>must</b> be thread-safe.
/// <see cref="TryMatch"/> may be called from a background thread.
/// </para>
/// </summary>
public interface ILinkifier
{
    /// <summary>
    /// Returns true if this token should become a clickable link.
    /// <paramref name="utf8Token"/> is the raw UTF-8 bytes of the token span.
    /// <paramref name="context"/> provides scope properties and typed parameter metadata.
    /// <para>This method may be called from a background thread.</para>
    /// </summary>
    bool TryMatch(ReadOnlySpan<byte> utf8Token, RenderContext context, out Action? onClick);
}
