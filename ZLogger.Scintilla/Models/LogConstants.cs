using System;
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
    public const int UrlMatchBufferCapacity = 8;

    public const int SciAppendText = 2282;
    public const int SciSetUndoCollection = 2012;
    public const int SciPositionFromPoint = 2022;
    public const int SciGetLength = 2006;
    public const int SciStartStyling = 2032;
    public const int SciSetStylingEx = 2073;
    public const int SciGetCurrentPos = 2008;
    public const int SciPositionFromPointClose = 2023;
    public const int SciIndicatorClearRange = 2505;
    public const int SciGotoPos = 2025;
    public const int SearchRefreshThrottleMs = 40;

    public const int SearchIndicatorId = 30;
    public const int VisibleRangeBufferBytes = 512;

    public const int DarkSearchIndicatorAlpha = 70;
    public const int LightSearchIndicatorAlpha = 55;
    public const int DarkSearchIndicatorOutlineAlpha = 170;
    public const int LightSearchIndicatorOutlineAlpha = 140;

    public const int LinkStyleOffset = 100;
    public const int ColorizerStyleIdStart = 150;
    public const int ColorizerStyleIdEnd = 199;
}
