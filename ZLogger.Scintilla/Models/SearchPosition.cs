namespace ZLogger.Scintilla.Models;

/// <summary>
/// Represents the current position within search results.
/// </summary>
public readonly struct SearchPosition
{
    public static readonly SearchPosition NotFound = new() { Current = 0, Total = 0 };

    /// <summary>1-based index of the current match. 0 = not found.</summary>
    public int Current { get; init; }

    /// <summary>Total number of matches in the document.</summary>
    public int Total { get; init; }

    public bool Found => Current > 0;

    /// <summary>Returns "100 of 1000" or "not found".</summary>
    public override string ToString() => Found ? $"{Current} of {Total}" : "not found";
}
