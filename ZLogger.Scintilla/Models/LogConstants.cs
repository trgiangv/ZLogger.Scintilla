using System.Text;
namespace ZLogger.Scintilla.Models;

internal static class LogConstants
{
    internal static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

    internal const string ParameterSpans = "__parameterSpans";
    internal const string JsonBytes = "__jsonBytes";

    public const string DefaultFontFamily = "Cascadia Mono";
    public const int DefaultFontSize = 10;
    public const int DefaultChannelCapacity = 100_000;
    public const int DefaultMaxBatchSize = 2_000;
    public const int DefaultFlushIntervalMs = 75;

    public const int DefaultMaxLines = 50_000;
    public const int DefaultMaxHistoryEntries = 50_000;
    public const int DefaultDecomposeDepth = 2;
    public const int DefaultDecomposeCollection = 20;
    public const int DefaultMaxTokenRanges = 50_000;

    public const int StyleRangesCapacity = 1024;
    public const int SegmentsCapacity = 256;
    public const int TokenRangesCapacity = 2048;
    public const int MatchPositionsCapacity = 256;
    public const int LogProcessorBufferCapacity = 1024;
    public const int MaxCachedWriterBytes = 64 * 1024;
    public const int UrlMatchBufferCapacity = 8;

    public const int MaxRetainedBatchBytes = 1024 * 1024;

    /// <summary>Below the 85 KB large object threshold, so a full chunk dies as cheaply as any other object.</summary>
    public const int MessageChunkBytes = 64 * 1024;

    /// <summary>
    /// Prepared batches that may wait for the UI thread. Past this the pump stops reading, so the channel fills
    /// and its drop policy applies. Without this limit the channel's capacity would not limit the backlog.
    /// </summary>
    public const int MaxQueuedBatches = 8;

    /// <summary>Entries the channel may hold before logging threads format their own messages.</summary>
    public const int MaxDeferredBacklog = 4096;

    /// <summary>Fewest lines trimmed at once while logs keep arriving.</summary>
    public const int MinTrimStepLines = 256;

    /// <summary>Longest time one drain holds the UI thread before letting it paint and take input.</summary>
    public const int DrainBudgetMs = 16;

    /// <summary>Longest time one slice of match counting holds the UI thread.</summary>
    public const int CountSliceBudgetMs = 8;

    /// <summary>Lines searched in one call while counting, because a single search cannot be interrupted.</summary>
    public const int CountSliceLines = 4096;

    // Scintilla positions are bytes. Everything here works in bytes and uses these messages directly,
    // because the ScintillaNET wrappers convert to UTF-16 positions and back on every call.
    public const int SciAppendText = 2282;
    public const int SciClearAll = 2004;
    public const int SciDeleteRange = 2645;
    public const int SciSetUndoCollection = 2012;
    public const int SciGetLength = 2006;
    public const int SciGetLineCount = 2154;
    public const int SciStartStyling = 2032;
    public const int SciSetStylingEx = 2073;
    public const int SciGetCurrentPos = 2008;
    public const int SciPositionFromPointClose = 2023;
    public const int SciPositionFromLine = 2167;
    public const int SciLineFromPosition = 2166;
    public const int SciGetLineEndPosition = 2136;
    public const int SciPositionAfter = 2418;
    public const int SciGetSelectionEmpty = 2650;
    public const int SciSetReadOnly = 2171;
    public const int SciSetLayoutCache = 2272;
    public const int ScCachePage = 2;
    public const int SciSetSel = 2160;
    public const int SciGetSelectionStart = 2143;
    public const int SciGetSelectionEnd = 2145;
    public const int SciSetTargetRange = 2686;
    public const int SciGetTargetEnd = 2193;
    public const int SciSearchInTarget = 2197;
    public const int SciSetSearchFlags = 2198;
    public const int SciSetStatus = 2382;
    public const int SciSetIndicatorCurrent = 2500;
    public const int SciIndicatorFillRange = 2504;
    public const int SciIndicatorClearRange = 2505;
    public const int SciGetFirstVisibleLine = 2152;
    public const int SciLinesOnScreen = 2370;
    public const int SciVisibleFromDocLine = 2220;
    public const int SciDocLineFromVisible = 2221;
    public const int SciScrollToEnd = 2629;

    public const int SearchRefreshThrottleMs = 40;

    public const int SearchIndicatorId = 30;

    public const int DarkSearchIndicatorAlpha = 70;
    public const int LightSearchIndicatorAlpha = 55;
    public const int DarkSearchIndicatorOutlineAlpha = 170;
    public const int LightSearchIndicatorOutlineAlpha = 140;

    public const int LinkStyleOffset = 100;
    public const int ColorizerStyleIdStart = 150;
    public const int ColorizerStyleIdEnd = 199;
}
