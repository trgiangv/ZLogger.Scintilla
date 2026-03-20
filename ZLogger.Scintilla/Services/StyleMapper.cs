using System.Drawing;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Services;

/// <summary>Maps semantic styles and log levels to fixed Scintilla style IDs.</summary>
internal static class StyleMapper
{
    private const int TraceStyleId       = 10;
    private const int DebugStyleId       = 11;
    private const int InformationStyleId = 12;
    private const int WarningStyleId     = 13;
    private const int ErrorStyleId       = 14;
    private const int CriticalStyleId    = 15;

    private const int TextStyleId            = 27;
    private const int SecondaryTextStyleId   = 17;
    private const int PunctuationStyleId     = 18;
    private const int JsonKeyStyleId         = 19;
    private const int JsonStringStyleId      = 20;
    private const int JsonNumberStyleId      = 21;
    private const int JsonBooleanStyleId     = 22;
    private const int JsonNullStyleId        = 23;
    private const int JsonDateTimeStyleId    = 25;
    private const int TokenLinkStyleId       = 24;
    private const int TokenClassifiedStyleId = 28;

    public const int  TokenIndicatorStyleId  = 8;
    public const int  LinkHotspotStyleId     = 16;
    public const bool TokenIndicatorUnderText = true;
    public static readonly Color TokenIndicatorColor = Color.FromArgb(80, 140, 220);

    private static readonly int[] SemanticLookup = BuildSemanticLookup();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToStyleId(this LogLevel level) => level switch
    {
        LogLevel.Trace       => TraceStyleId,
        LogLevel.Debug       => DebugStyleId,
        LogLevel.Information => InformationStyleId,
        LogLevel.Warning     => WarningStyleId,
        LogLevel.Error       => ErrorStyleId,
        LogLevel.Critical    => CriticalStyleId,
        _                    => InformationStyleId
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToStyleId(this SemanticStyle style)
    {
        var index = (int)style;
        return (uint)index < (uint)SemanticLookup.Length ? SemanticLookup[index] : TextStyleId;
    }

    private static int[] BuildSemanticLookup()
    {
        var lookup = new int[34 + 1];
        for (var i = 0; i < lookup.Length; i++)
            lookup[i] = TextStyleId;

        lookup[(int)SemanticStyle.Text]            = TextStyleId;
        lookup[(int)SemanticStyle.SecondaryText]   = SecondaryTextStyleId;
        lookup[(int)SemanticStyle.Punctuation]     = PunctuationStyleId;
        lookup[(int)SemanticStyle.LevelTrace]      = TraceStyleId;
        lookup[(int)SemanticStyle.LevelDebug]      = DebugStyleId;
        lookup[(int)SemanticStyle.LevelInformation]= InformationStyleId;
        lookup[(int)SemanticStyle.LevelWarning]    = WarningStyleId;
        lookup[(int)SemanticStyle.LevelError]      = ErrorStyleId;
        lookup[(int)SemanticStyle.LevelCritical]   = CriticalStyleId;
        lookup[(int)SemanticStyle.JsonKey]         = JsonKeyStyleId;
        lookup[(int)SemanticStyle.JsonString]      = JsonStringStyleId;
        lookup[(int)SemanticStyle.JsonNumber]      = JsonNumberStyleId;
        lookup[(int)SemanticStyle.JsonBoolean]     = JsonBooleanStyleId;
        lookup[(int)SemanticStyle.JsonNull]        = JsonNullStyleId;
        lookup[(int)SemanticStyle.JsonDateTime]    = JsonDateTimeStyleId;
        lookup[(int)SemanticStyle.TokenLink]       = TokenLinkStyleId;
        lookup[(int)SemanticStyle.TokenClassified] = TokenClassifiedStyleId;

        return lookup;
    }
}
