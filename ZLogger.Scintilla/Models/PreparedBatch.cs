using System.Buffers;
namespace ZLogger.Scintilla.Models;

/// <summary>What a batch does to the document before its text is appended.</summary>
internal enum DocumentAction
{
    Append,

    /// <summary>A filter replay starts: empty the document, keeping its allocation for the text that follows.</summary>
    ResetForReplay,

    /// <summary>The user cleared the viewer: replace the document so its memory is released.</summary>
    Clear
}

/// <summary>
/// Immutable batch of pre-rendered log data ready for Scintilla API calls.
/// Created on the pump thread, consumed on the UI thread. Owns its pooled arrays and shares nothing
/// with the entries it was built from, so the entries can be released as soon as it exists.
/// </summary>
internal readonly struct PreparedBatch(
    PooledSegment<byte> text,
    PooledSegment<StyleRange> styles,
    PooledSegment<TokenRange> tokens,
    long epoch,
    int entryCount,
    DocumentAction action = DocumentAction.Append)
{
    public readonly PooledSegment<byte> Text = text;
    public readonly PooledSegment<StyleRange> Styles = styles;
    public readonly PooledSegment<TokenRange> Tokens = tokens;

    /// <summary>The document generation this batch belongs to. The UI discards a batch from an older one.</summary>
    public readonly long Epoch = epoch;
    public readonly int EntryCount = entryCount;
    public readonly DocumentAction Action = action;

    /// <summary>A batch with no text that only changes the document.</summary>
    public static PreparedBatch ForAction(DocumentAction action, long epoch) => new(
        new PooledSegment<byte>([], 0),
        new PooledSegment<StyleRange>([], 0),
        new PooledSegment<TokenRange>([], 0),
        epoch, 0, action);

    public void ReturnBuffers()
    {
        Text.Return();
        Styles.Return();
        Tokens.Return();
    }
}

/// <summary>
/// A pooled array segment: array rented from <see cref="ArrayPool{T}"/> plus its logical length.
/// </summary>
internal readonly struct PooledSegment<T>(T[] array, int count)
{
    public readonly T[] Array = array;
    public readonly int Count = count;

    public void Return()
    {
        if (Array.Length == 0)
            return;

        // Style and token ranges hold click actions. Clearing only the used part keeps this cheap
        // and stops the pool from keeping those actions, and whatever they capture, alive.
        if (typeof(T) != typeof(byte))
            System.Array.Clear(Array, 0, Count);
        ArrayPool<T>.Shared.Return(Array);
    }
}
