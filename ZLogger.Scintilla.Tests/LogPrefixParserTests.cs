using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class LogPrefixParserTests
{
    [TestMethod]
    public void TokenPayload_ForColorizer_HasColorizerColor()
    {
        var payload = TokenPayload.ForColorizer(Color.Red);
        Assert.IsFalse(payload.IsLink);
        Assert.IsTrue(payload.HasColorizerColor);
        Assert.AreEqual(Color.Red.ToArgb(), payload.ColorizerForeground!.Value.ToArgb());
    }

    [TestMethod]
    [DataRow("TRC", SemanticStyle.LevelTrace)]
    [DataRow("DBG", SemanticStyle.LevelDebug)]
    [DataRow("INF", SemanticStyle.LevelInformation)]
    [DataRow("WRN", SemanticStyle.LevelWarning)]
    [DataRow("ERR", SemanticStyle.LevelError)]
    [DataRow("CRT", SemanticStyle.LevelCritical)]
    public void LogPrefixParser_BuildPrefixSegments_MapsAbbreviationToCorrectSemanticStyle(
        string abbrev, SemanticStyle expectedStyle)
    {
        var prefix = $"[12:34:56 {abbrev}] some message";
        var utf8 = Encoding.UTF8.GetBytes(prefix);
        var result = LogPrefixParser.TryParse(utf8);
        Assert.IsTrue(result.Found);

        var segments = new List<RenderSegment>();
        LogPrefixParser.BuildPrefixSegments(result, utf8, LogLevel.Information, segments);

        Assert.IsTrue(segments.Count >= 4);
        Assert.AreEqual(expectedStyle, segments[3].SemanticStyle);
    }

    [TestMethod]
    public void LogPrefixParser_Prefix_IsRecognized_ForWellFormedInput()
    {
        const string prefix = "[12:34:56 INF] some message";
        var utf8 = Encoding.UTF8.GetBytes(prefix);
        var result = LogPrefixParser.TryParse(utf8);
        Assert.IsTrue(result.Found);
        Assert.AreEqual(15, result.PrefixByteLength);

        var remainder = Encoding.UTF8.GetString(utf8, result.PrefixByteLength, utf8.Length - result.PrefixByteLength);
        Assert.AreEqual("some message", remainder);
    }

    [TestMethod]
    public void LogPrefixParser_Prefix_StillParsesMillisecondFormat()
    {
        const string prefix = "[12:34:56.789 INF] some message";
        var utf8 = Encoding.UTF8.GetBytes(prefix);
        var result = LogPrefixParser.TryParse(utf8);
        Assert.IsTrue(result.Found);
        Assert.AreEqual(19, result.PrefixByteLength);
    }
}
