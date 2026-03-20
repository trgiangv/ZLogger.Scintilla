using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class LogPrefixParserTests
{
    [Fact]
    public void TokenPayload_ForColorizer_SemanticStyle_IsTokenClassified()
    {
        var payload = TokenPayload.ForColorizer(System.Drawing.Color.Red);
        Assert.Equal(SemanticStyle.TokenClassified, payload.SemanticStyle);
        Assert.False(payload.IsLink);
        Assert.True(payload.HasColorizerColor);
        Assert.Equal(System.Drawing.Color.Red.ToArgb(), payload.ColorizerForeground!.Value.ToArgb());
    }

    [Theory]
    [InlineData("TRC", SemanticStyle.LevelTrace)]
    [InlineData("DBG", SemanticStyle.LevelDebug)]
    [InlineData("INF", SemanticStyle.LevelInformation)]
    [InlineData("WRN", SemanticStyle.LevelWarning)]
    [InlineData("ERR", SemanticStyle.LevelError)]
    [InlineData("CRT", SemanticStyle.LevelCritical)]
    public void LogPrefixParser_BuildPrefixSegments_MapsAbbreviationToCorrectSemanticStyle(
        string abbrev, SemanticStyle expectedStyle)
    {
        var prefix = $"[12:34:56.789 {abbrev}] some message";
        var result = LogPrefixParser.TryParse(prefix, out _);
        Assert.True(result.Found);

        var segments = new List<RenderSegment>();
        LogPrefixParser.BuildPrefixSegments(result, prefix, LogLevel.Information, segments);

        Assert.True(segments.Count >= 4);
        Assert.Equal(expectedStyle, segments[3].SemanticStyle);
    }

    [Fact]
    public void LogPrefixParser_Prefix_IsRecognized_ForWellFormedInput()
    {
        var prefix = "[12:34:56.789 INF] some message";
        var result = LogPrefixParser.TryParse(prefix, out var remainder);
        Assert.True(result.Found);
        Assert.Equal("some message", remainder);
    }
}
