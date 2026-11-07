using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class UrlClickTests
{
    private static List<string> Find(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var matches = new List<(int Start, int Length)>();
        UrlScanner.FindAll(bytes, matches);
        return matches.Select(m => Encoding.UTF8.GetString(bytes, m.Start, m.Length)).ToList();
    }

    [TestMethod]
    [DataRow("visit https://example.com for details", "https://example.com")]
    [DataRow("https://github.com/user/repo", "https://github.com/user/repo")]
    [DataRow("see http://example.com/path?q=1.", "http://example.com/path?q=1")]
    [DataRow("Tiếng Việt https://example.com/đường dẫn", "https://example.com/đường")]
    [DataRow("go to www.example.com, now", "www.example.com")]
    public void FindAll_FindsTheUrl(string text, string expected)
    {
        CollectionAssert.AreEqual(new[] { expected }, Find(text));
    }

    [TestMethod]
    [DataRow("no url here at all")]
    [DataRow("just plain text 123")]
    [DataRow("")]
    [DataRow("time 12:00:00 and path c:/x")]
    [DataRow("://no-scheme")]
    [DataRow("awww.example.com")]
    public void FindAll_FindsNothing(string text)
    {
        Assert.IsEmpty(Find(text));
    }

    [TestMethod]
    public void FindAll_FindsEveryUrlInOrder()
    {
        CollectionAssert.AreEqual(
            new[] { "https://a.com", "www.b.com", "https://c.com" },
            Find("go to https://a.com and www.b.com then https://c.com for info"));
    }

    [TestMethod]
    public void TryCreateLink_MakesALink_ForASafeUrl()
    {
        Assert.IsTrue(UrlScanner.TryCreateLink("https://example.com"u8, out var link));
        Assert.IsNotNull(link);
        Assert.IsTrue(link.IsLink);
    }

    [TestMethod]
    public void TryCreateLink_Refuses_AnUnsafeUrl()
    {
        Assert.IsFalse(UrlScanner.TryCreateLink("file:///c:/x.exe"u8, out var link));
        Assert.IsNull(link);
    }

    [TestMethod]
    public void TryNormalizeUri_ReturnsFalse_ForInvalidUrl()
    {
        Assert.IsFalse(UrlScanner.TryNormalizeUri("not-a-url", out _));
    }

    [TestMethod]
    public void BuildSegments_ANumberInsideAUrl_DoesNotSplitTheLink()
    {
        var renderer = new LogRenderer();
        var spans = new[] { new ParameterSpan { FormattedValue = "5", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) } };
        var properties = new Dictionary<string, object?> { [LogConstants.ParameterSpans] = spans };
        const string message = "[12:00:00 INF] get https://a.example/x?i=5 took 5";
        var segments = new List<RenderSegment>();

        renderer.BuildSegments(TestHelper.MakeEntry(message, LogLevel.Information, properties), segments);

        var link = segments.Single(s => s.IsLink);
        Assert.AreEqual("https://a.example/x?i=5".Length, link.Utf8Length);
        Assert.AreEqual(1, segments.Count(s => s.SemanticStyle == SemanticStyle.JsonNumber), "The other 5 still gets its color.");
        Assert.AreEqual(Encoding.UTF8.GetByteCount(message), segments.Sum(s => s.Utf8Length));
    }

    [TestMethod]
    public void BuildSegments_MakesAUrlALink_WithNoParametersAndNoLinkifier()
    {
        var renderer = new LogRenderer();
        const string message = "[12:00:00 INF] see https://example.com";
        var segments = new List<RenderSegment>();

        renderer.BuildSegments(TestHelper.MakeEntry(message, LogLevel.Information), segments);

        Assert.AreEqual(1, segments.Count(s => s.IsLink));
    }

    [TestMethod]
    public void JsonHighlighter_KeepsTheStringStyle_ForALinkInsideAString()
    {
        var highlighter = new JsonSyntaxHighlighter();
        var segments = new List<RenderSegment>();
        var json = "{\"url\": \"see https://a.example/x now\"}"u8.ToArray();

        highlighter.AppendJsonStyledSegments(json, segments);

        var link = segments.Single(s => s.IsLink);
        Assert.AreEqual(SemanticStyle.JsonString, link.SemanticStyle);
        Assert.AreEqual(json.Length, segments.Sum(s => s.Utf8Length));
    }
}
