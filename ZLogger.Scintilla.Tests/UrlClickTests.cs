using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class UrlClickTests
{
    [Fact]
    public void UrlScanner_EmitsTokenPayload_WithOnClick_ForValidUrl()
    {
        var helper = new UrlScanner();
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);

        const string text = "visit https://example.com for details";
        helper.AppendUrlAwareContent(text, SemanticStyle.Text, writer);

        var linkSegment = segments.FirstOrDefault(s => s.IsLink);
        Assert.NotNull(linkSegment.TokenPayload);
        Assert.True(linkSegment.TokenPayload!.Value.IsLink);
    }

    [Fact]
    public void UrlScanner_EmitsTokenPayload_WithOnClick_ForHttpsUrl()
    {
        var helper = new UrlScanner();
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);

        const string text = "https://github.com/user/repo";
        helper.AppendUrlAwareContent(text, SemanticStyle.Text, writer);

        var linkSegment = segments.FirstOrDefault(s => s.IsLink);
        Assert.NotNull(linkSegment.TokenPayload);
        Assert.True(linkSegment.TokenPayload!.Value.IsLink);
    }

    [Fact]
    public void UrlScanner_NoLink_ForPlainText()
    {
        var helper = new UrlScanner();
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);

        const string text = "no url here at all";
        helper.AppendUrlAwareContent(text, SemanticStyle.Text, writer);

        Assert.DoesNotContain(segments, s => s.IsLink);
    }

    [Fact]
    public void UrlScanner_AppendUrlAwareContent_EmitsLinkPayload_ForJsonStringWithUrl()
    {
        var helper = new UrlScanner();
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);

        const string content = "see https://docs.example.com/api for reference";
        helper.AppendUrlAwareContent(content, SemanticStyle.JsonString, writer);

        var linkSegment = segments.FirstOrDefault(s => s.IsLink);
        Assert.NotNull(linkSegment.TokenPayload);
        Assert.True(linkSegment.TokenPayload!.Value.IsLink);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("text with https://foo.bar embedded")]
    public void UrlScanner_HasPotentialCandidate_ReturnsTrue_ForUrlText(string text)
    {
        Assert.True(UrlScanner.HasPotentialCandidate(text.AsSpan()));
    }

    [Theory]
    [InlineData("no url here")]
    [InlineData("just plain text 123")]
    [InlineData("")]
    public void UrlScanner_HasPotentialCandidate_ReturnsFalse_ForNonUrlText(string text)
    {
        Assert.False(UrlScanner.HasPotentialCandidate(text.AsSpan()));
    }

    [Fact]
    public void UrlScanner_FindAll_Span_FindsCorrectCount()
    {
        const string text = "go to https://a.com and also https://b.com for info";
        var matches = new List<(int Start, int Length)>();
        var count = UrlScanner.FindAll(text.AsSpan(), matches);
        Assert.Equal(2, count);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void UrlScanner_TryNormalizeUri_ReturnsTrue_ForValidUrl()
    {
        var ok = UrlScanner.TryNormalizeUri("https://example.com", out var uri);
        Assert.True(ok);
        Assert.NotNull(uri);
    }

    [Fact]
    public void UrlScanner_TryNormalizeUri_ReturnsFalse_ForInvalidUrl()
    {
        var ok = UrlScanner.TryNormalizeUri("not-a-url", out _);
        Assert.False(ok);
    }
}
