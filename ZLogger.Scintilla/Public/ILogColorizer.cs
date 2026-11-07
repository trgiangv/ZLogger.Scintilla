using System.Drawing;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Provides token-level foreground color overrides.
/// Implement this interface to colorize specific tokens in log output.
/// A token matched by an <see cref="ILinkifier"/> is a link and keeps the link color;
/// the colorizer is not asked about it.
/// <para>
/// <b>Thread safety:</b> Implementations <b>must</b> be thread-safe.
/// <see cref="TryGetColor"/> may be called from a background thread.
/// </para>
/// </summary>
public interface ILogColorizer
{
    /// <summary>
    /// Returns true and sets <paramref name="color"/> if this token should be colorized.
    /// <paramref name="utf8Token"/> is the raw UTF-8 bytes of the token span.
    /// Implement exact, contains, or regex matching internally.
    /// <para>This method may be called from a background thread.</para>
    /// </summary>
    bool TryGetColor(ReadOnlySpan<byte> utf8Token, out Color color);
}
