using System.Text;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class ParameterSpanTests
{
    [Theory]
    [InlineData(typeof(int), SemanticStyle.JsonNumber)]
    [InlineData(typeof(long), SemanticStyle.JsonNumber)]
    [InlineData(typeof(double), SemanticStyle.JsonNumber)]
    [InlineData(typeof(float), SemanticStyle.JsonNumber)]
    [InlineData(typeof(decimal), SemanticStyle.JsonNumber)]
    [InlineData(typeof(short), SemanticStyle.JsonNumber)]
    [InlineData(typeof(byte), SemanticStyle.JsonNumber)]
    [InlineData(typeof(bool), SemanticStyle.JsonBoolean)]
    [InlineData(typeof(DateTime), SemanticStyle.JsonDateTime)]
    [InlineData(typeof(DateTimeOffset), SemanticStyle.JsonDateTime)]
    [InlineData(null, SemanticStyle.JsonNull)]
    [InlineData(typeof(string), SemanticStyle.Text)]
    [InlineData(typeof(Guid), SemanticStyle.Text)]
    public void MapClrType_ReturnsExpectedStyle(Type? type, SemanticStyle expected)
    {
        Assert.Equal(expected, ParameterSpan.MapClrType(type));
    }

    [Fact]
    public void Properties_ReturnsSpans_WhenPresent()
    {
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "42", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) }
        };
        var ctx = CreateContext("val=42", new Dictionary<string, object?>
        {
            [LogConstants.ParameterSpans] = spans
        });

        Assert.True(ctx.Properties.TryGetValue(LogConstants.ParameterSpans, out var raw));
        var result = Assert.IsType<ParameterSpan[]>(raw);
        Assert.Single(result);
        Assert.Equal("42", result[0].FormattedValue);
    }

    [Fact]
    public void Properties_ReturnsNull_WhenNoSpans()
    {
        var ctx = CreateContext("hello", new Dictionary<string, object?>());
        Assert.False(ctx.Properties.ContainsKey(LogConstants.ParameterSpans));
    }

    [Fact]
    public void DisplayValueFormatter_ColorizesIntegerParameter()
    {
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "1024", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) }
        };
        var message = "Memory usage: 1024 MB";
        var segments = BuildSegments(message, spans);

        var numberBytes = Encoding.UTF8.GetByteCount("1024");
        Assert.Contains(segments, s =>
            s.SemanticStyle == SemanticStyle.JsonNumber && s.Utf8Length == numberBytes);
    }

    [Fact]
    public void DisplayValueFormatter_ColorizesBoolParameter()
    {
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "True", SemanticStyle = SemanticStyle.JsonBoolean, ClrType = typeof(bool) }
        };
        var message = "Is valid: True";
        var segments = BuildSegments(message, spans);

        var boolBytes = Encoding.UTF8.GetByteCount("True");
        Assert.Contains(segments, s =>
            s.SemanticStyle == SemanticStyle.JsonBoolean && s.Utf8Length == boolBytes);
    }

    [Fact]
    public void DisplayValueFormatter_ColorizesDateTimeParameter()
    {
        var dt = new DateTime(2026, 3, 18, 14, 30, 0);
        var formatted = dt.ToString();
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = formatted, SemanticStyle = SemanticStyle.JsonDateTime, ClrType = typeof(DateTime) }
        };
        var message = $"Response time: {formatted}";
        var segments = BuildSegments(message, spans);

        var dtBytes = Encoding.UTF8.GetByteCount(formatted);
        Assert.Contains(segments, s =>
            s.SemanticStyle == SemanticStyle.JsonDateTime && s.Utf8Length == dtBytes);
    }

    [Fact]
    public void DisplayValueFormatter_DoesNotColorize_UnknownTypeParameter()
    {
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "ORD-12345", SemanticStyle = SemanticStyle.Text, ClrType = typeof(string) }
        };
        var message = "Order: ORD-12345";
        var segments = BuildSegments(message, spans);

        Assert.DoesNotContain(segments, s =>
            s.SemanticStyle == SemanticStyle.JsonNumber ||
            s.SemanticStyle == SemanticStyle.JsonBoolean ||
            s.SemanticStyle == SemanticStyle.JsonDateTime);
    }

    [Fact]
    public void DisplayValueFormatter_MultipleParameters_AllColorized()
    {
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "1024", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) },
            new ParameterSpan { FormattedValue = "2048", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) }
        };
        var message = "Memory usage: 1024 MB (threshold: 2048 MB)";
        var segments = BuildSegments(message, spans);

        var count = segments.Count(s => s.SemanticStyle == SemanticStyle.JsonNumber);
        Assert.Equal(2, count);
    }

    [Fact]
    public void DisplayValueFormatter_SubstringNumber_DoesNotMatchInsideLargerToken()
    {
        // "75" (from index param) must NOT match inside "1075" (elementId param)
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "75", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) },
            new ParameterSpan { FormattedValue = "1075", SemanticStyle = SemanticStyle.Text, ClrType = typeof(object) }
        };
        var message = "index=75, elementId=1075";
        var segments = BuildSegments(message, spans);

        var numberSegments = segments.Where(s => s.SemanticStyle == SemanticStyle.JsonNumber).ToList();
        Assert.Single(numberSegments);
        Assert.Equal(Encoding.UTF8.GetByteCount("75"), numberSegments[0].Utf8Length);
    }

    [Fact]
    public void DisplayValueFormatter_BoundaryCheck_MatchesOnlyWholeTokens()
    {
        var spans = new[]
        {
            new ParameterSpan { FormattedValue = "42", SemanticStyle = SemanticStyle.JsonNumber, ClrType = typeof(int) }
        };
        // "42" appears twice: embedded in "4200" (no match) and standalone (match)
        var message = "code=4200 count=42";
        var segments = BuildSegments(message, spans);

        var numberSegments = segments.Where(s => s.SemanticStyle == SemanticStyle.JsonNumber).ToList();
        Assert.Single(numberSegments);
    }

    private static List<RenderSegment> BuildSegments(string message, ParameterSpan[] paramSpans)
    {
        var properties = new Dictionary<string, object?>
        {
            [LogConstants.ParameterSpans] = paramSpans
        };
        var ctx = CreateContext(message, properties);

        var tokenEngine = new TokenResolutionEngine(null, null, null);
        var formatter = new DisplayValueFormatter(tokenEngine);
        var segments = new List<RenderSegment>();
        formatter.AppendSegments(ctx, message, segments);
        return segments;
    }

    private static RenderContext CreateContext(string message, IReadOnlyDictionary<string, object?> properties)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return new RenderContext(bytes, properties);
    }
}
