using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class UrlClickTests
{
    [Fact]
    public void UrlScanner_EmitsTokenPayload_WithOnClick_ForValidUrl()
    {
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);
        var matches = new List<(int Start, int Length)>();

        const string text = "visit https://example.com for details";
        UrlScanner.FindAll(text.AsSpan(), matches);
        Assert.True(matches.Count > 0);

        var cursor = 0;
        foreach (var match in matches)
        {
            if (match.Start > cursor)
                writer.Add(text, cursor, match.Start - cursor, SemanticStyle.Text);
            UrlScanner.EmitUrlOrFallback(text, match.Start, match.Length, SemanticStyle.Text, writer);
            cursor = match.Start + match.Length;
        }
        if (cursor < text.Length)
            writer.Add(text, cursor, text.Length - cursor, SemanticStyle.Text);

        var linkSegment = segments.FirstOrDefault(s => s.IsLink);
        Assert.NotNull(linkSegment.TokenPayload);
        Assert.True(linkSegment.TokenPayload!.Value.IsLink);
    }

    [Fact]
    public void UrlScanner_EmitsTokenPayload_WithOnClick_ForHttpsUrl()
    {
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);
        var matches = new List<(int Start, int Length)>();

        const string text = "https://github.com/user/repo";
        UrlScanner.FindAll(text.AsSpan(), matches);
        Assert.True(matches.Count > 0);

        foreach (var match in matches)
            UrlScanner.EmitUrlOrFallback(text, match.Start, match.Length, SemanticStyle.Text, writer);

        var linkSegment = segments.FirstOrDefault(s => s.IsLink);
        Assert.NotNull(linkSegment.TokenPayload);
        Assert.True(linkSegment.TokenPayload!.Value.IsLink);
    }

    [Fact]
    public void UrlScanner_NoLink_ForPlainText()
    {
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);

        const string text = "no url here at all";
        Assert.False(UrlScanner.HasPotentialCandidate(text.AsSpan()));
        writer.Add(text, SemanticStyle.Text);

        Assert.DoesNotContain(segments, s => s.IsLink);
    }

    [Fact]
    public void UrlScanner_EmitUrlOrFallback_EmitsLinkPayload_ForJsonStringWithUrl()
    {
        var segments = new List<RenderSegment>();
        var writer = new RenderTokenWriter(segments);
        var matches = new List<(int Start, int Length)>();

        const string content = "see https://docs.example.com/api for reference";
        UrlScanner.FindAll(content.AsSpan(), matches);
        Assert.True(matches.Count > 0);

        foreach (var match in matches)
            UrlScanner.EmitUrlOrFallback(content, match.Start, match.Length, SemanticStyle.JsonString, writer, preserveStyle: true);

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
