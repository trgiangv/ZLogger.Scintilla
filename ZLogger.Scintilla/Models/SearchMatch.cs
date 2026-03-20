namespace ZLogger.Scintilla.Models;

internal readonly record struct SearchMatch(bool Found, int CurrentIndex = 0)
{
    public static SearchMatch NotFound => new(false);
}
