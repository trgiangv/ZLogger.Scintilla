using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class LogPrefixParserTests
{
    [Fact]
    public void TokenPayload_ForColorizer_HasColorizerColor()
    {
        var payload = TokenPayload.ForColorizer(System.Drawing.Color.Red);
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
        var prefix = $"[12:34:56 {abbrev}] some message";
        var utf8 = Encoding.UTF8.GetBytes(prefix);
        var result = LogPrefixParser.TryParse(utf8);
        Assert.True(result.Found);

        var segments = new List<RenderSegment>();
        LogPrefixParser.BuildPrefixSegments(result, utf8, LogLevel.Information, segments);

        Assert.True(segments.Count >= 4);
        Assert.Equal(expectedStyle, segments[3].SemanticStyle);
    }

    [Fact]
    public void LogPrefixParser_Prefix_IsRecognized_ForWellFormedInput()
    {
        var prefix = "[12:34:56 INF] some message";
        var utf8 = Encoding.UTF8.GetBytes(prefix);
        var result = LogPrefixParser.TryParse(utf8);
        Assert.True(result.Found);
        Assert.Equal(15, result.PrefixByteLength);

        var remainder = Encoding.UTF8.GetString(utf8, result.PrefixByteLength, utf8.Length - result.PrefixByteLength);
        Assert.Equal("some message", remainder);
    }

    [Fact]
    public void LogPrefixParser_Prefix_StillParsesMillisecondFormat()
    {
        var prefix = "[12:34:56.789 INF] some message";
        var utf8 = Encoding.UTF8.GetBytes(prefix);
        var result = LogPrefixParser.TryParse(utf8);
        Assert.True(result.Found);
        Assert.Equal(19, result.PrefixByteLength);
    }
}
