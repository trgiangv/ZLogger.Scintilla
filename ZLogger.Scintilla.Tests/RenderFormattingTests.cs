using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class RenderFormattingTests
{
    [TestMethod]
    public void JsonSyntaxHighlighter_StylesJsonKeys()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"key": "value"}""";

        formatter.AppendJsonStyledSegments(Encoding.UTF8.GetBytes(json), segments);

        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonKey, segments);
        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonString, segments);
    }

    [TestMethod]
    public void JsonSyntaxHighlighter_StylesNumbers()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"count": 42}""";

        formatter.AppendJsonStyledSegments(Encoding.UTF8.GetBytes(json), segments);

        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonNumber, segments);
    }

    [TestMethod]
    public void JsonSyntaxHighlighter_StylesBooleans()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"active": true}""";

        formatter.AppendJsonStyledSegments(Encoding.UTF8.GetBytes(json), segments);

        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonBoolean, segments);
    }

    [TestMethod]
    public void JsonSyntaxHighlighter_StylesNull()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"data": null}""";

        formatter.AppendJsonStyledSegments(Encoding.UTF8.GetBytes(json), segments);

        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonNull, segments);
    }

    [TestMethod]
    public void JsonSyntaxHighlighter_StylesPunctuation()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"a": [1, 2]}""";

        formatter.AppendJsonStyledSegments(Encoding.UTF8.GetBytes(json), segments);

        Assert.Contains(s => s.SemanticStyle == SemanticStyle.Punctuation, segments);
    }

    [TestMethod]
    public void JsonSyntaxHighlighter_TotalUtf8Length_MatchesInput()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """
{
  "name": "test",
  "count": 3,
  "active": true,
  "data": null
}
""";

        formatter.AppendJsonStyledSegments(Encoding.UTF8.GetBytes(json), segments);

        var totalLength = segments.Sum(s => s.Utf8Length);
        var expectedLength = Encoding.UTF8.GetByteCount(json);
        Assert.AreEqual(expectedLength, totalLength);
    }

    [TestMethod]
    public void LogRenderer_BuildSegments_PlainMessage_ProducesSegments()
    {
        var options = new ScintillaOptions();
        var renderer = new LogRenderer("Cascadia Mono", 10, options);

        const string message = "[12:00:00 INF] Hello World";
        var entry = TestHelper.MakeEntry(message, LogLevel.Information);

        var segments = new List<RenderSegment>();
        renderer.BuildSegments(entry, segments);

        Assert.IsNotEmpty(segments);
        var totalLength = segments.Sum(s => s.Utf8Length);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(message), totalLength);
    }

    [TestMethod]
    public void LogRenderer_BuildJsonSegments_ProducesStyledOutput()
    {
        var renderer = new LogRenderer();
        const string json = """{"key": "value", "num": 42}""";

        var segments = new List<RenderSegment>();
        renderer.BuildJsonSegments(Encoding.UTF8.GetBytes(json), segments);

        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonKey, segments);
        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonString, segments);
        Assert.Contains(s => s.SemanticStyle == SemanticStyle.JsonNumber, segments);
    }

    [TestMethod]
    public void LogRenderer_PrettyJsonDisabledByDefault()
    {
        var renderer = new LogRenderer();
        Assert.IsFalse(renderer.IsPrettyJsonEnabled);
    }

    [TestMethod]
    public void LogRenderer_SetPrettyJson_TogglesFlag()
    {
        var renderer = new LogRenderer();
        renderer.SetPrettyJson(true);
        Assert.IsTrue(renderer.IsPrettyJsonEnabled);
        renderer.SetPrettyJson(false);
        Assert.IsFalse(renderer.IsPrettyJsonEnabled);
    }
}
