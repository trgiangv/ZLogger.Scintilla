namespace ZLogger.Scintilla.Models;

/// <summary>How a search pattern matches: the find bar's Aa, ab and .* toggles.</summary>
[Flags]
internal enum SearchOptions
{
    None = 0,
    MatchCase = 1,
    WholeWord = 2,
    Regex = 4
}
