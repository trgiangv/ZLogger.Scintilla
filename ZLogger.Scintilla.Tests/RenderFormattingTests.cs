using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class RenderFormattingTests
{
    [Fact]
    public void JsonSyntaxHighlighter_StylesJsonKeys()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"key": "value"}""";

        formatter.AppendJsonStyledSegments(json, segments);

        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonKey);
        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonString);
    }

    [Fact]
    public void JsonSyntaxHighlighter_StylesNumbers()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"count": 42}""";

        formatter.AppendJsonStyledSegments(json, segments);

        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonNumber);
    }

    [Fact]
    public void JsonSyntaxHighlighter_StylesBooleans()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"active": true}""";

        formatter.AppendJsonStyledSegments(json, segments);

        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonBoolean);
    }

    [Fact]
    public void JsonSyntaxHighlighter_StylesNull()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"data": null}""";

        formatter.AppendJsonStyledSegments(json, segments);

        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonNull);
    }

    [Fact]
    public void JsonSyntaxHighlighter_StylesPunctuation()
    {
        var formatter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        const string json = """{"a": [1, 2]}""";

        formatter.AppendJsonStyledSegments(json, segments);

        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.Punctuation);
    }

    [Fact]
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

        formatter.AppendJsonStyledSegments(json, segments);

        var totalLength = segments.Sum(s => s.Utf8Length);
        var expectedLength = Encoding.UTF8.GetByteCount(json);
        Assert.Equal(expectedLength, totalLength);
    }

    [Fact]
    public void LogRenderer_BuildSegments_PlainMessage_ProducesSegments()
    {
        var options = new ScintillaOptions();
        var renderer = new LogRenderer("Cascadia Mono", 10, options);

        var message = "[12:00:00 INF] Hello World";
        var entry = CreateEntry(message);

        var segments = new List<RenderSegment>();
        renderer.BuildSegments(entry, segments);

        Assert.NotEmpty(segments);
        var totalLength = segments.Sum(s => s.Utf8Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(message), totalLength);
    }

    [Fact]
    public void LogRenderer_BuildJsonSegments_ProducesStyledOutput()
    {
        var renderer = new LogRenderer();
        const string json = """{"key": "value", "num": 42}""";

        var segments = new List<RenderSegment>();
        renderer.BuildJsonSegments(json, segments);

        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonKey);
        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonString);
        Assert.Contains(segments, s => s.SemanticStyle == SemanticStyle.JsonNumber);
    }

    [Fact]
    public void LogRenderer_PrettyJsonDisabledByDefault()
    {
        var renderer = new LogRenderer();
        Assert.False(renderer.IsPrettyJsonEnabled);
    }

    [Fact]
    public void LogRenderer_SetPrettyJson_TogglesFlag()
    {
        var renderer = new LogRenderer();
        renderer.SetPrettyJson(true);
        Assert.True(renderer.IsPrettyJsonEnabled);
        renderer.SetPrettyJson(false);
        Assert.False(renderer.IsPrettyJsonEnabled);
    }

    private static LogEntry CreateEntry(string message, IReadOnlyDictionary<string, object?>? properties = null)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return new LogEntry
        {
            Level = LogLevel.Information,
            Message = new ArraySegment<byte>(bytes),
            Properties = properties ?? LogEntry.EmptyProperties
        };
    }
}
