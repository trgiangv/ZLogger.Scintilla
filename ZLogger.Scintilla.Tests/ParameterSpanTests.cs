using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

public sealed class ParameterSpanTests
{
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(double))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(char))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(byte[]))]
    public void IsLeafType_ReturnsTrue_ForLeafTypes(Type type)
    {
        Assert.True(ParameterSpan.IsLeafType(type));
    }

    [Theory]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(Dictionary<string, object>))]
    [InlineData(typeof(object))]
    [InlineData(typeof(Exception))]
    public void IsLeafType_ReturnsFalse_ForComplexTypes(Type type)
    {
        Assert.False(ParameterSpan.IsLeafType(type));
    }

    [Fact]
    public void IsLeafType_ReturnsTrue_ForEnums()
    {
        Assert.True(ParameterSpan.IsLeafType(typeof(SemanticStyle)));
        Assert.True(ParameterSpan.IsLeafType(typeof(DayOfWeek)));
    }

    [Fact]
    public void MapClrType_ReturnsJsonNumber_ForNumericTypes()
    {
        Assert.Equal(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(int)));
        Assert.Equal(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(long)));
        Assert.Equal(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(double)));
        Assert.Equal(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(decimal)));
    }

    [Fact]
    public void MapClrType_ReturnsJsonBoolean_ForBool()
    {
        Assert.Equal(SemanticStyle.JsonBoolean, ParameterSpan.MapClrType(typeof(bool)));
    }

    [Fact]
    public void MapClrType_ReturnsJsonDateTime_ForDateTypes()
    {
        Assert.Equal(SemanticStyle.JsonDateTime, ParameterSpan.MapClrType(typeof(DateTime)));
        Assert.Equal(SemanticStyle.JsonDateTime, ParameterSpan.MapClrType(typeof(DateTimeOffset)));
    }

    [Fact]
    public void MapClrType_ReturnsJsonNull_ForNull()
    {
        Assert.Equal(SemanticStyle.JsonNull, ParameterSpan.MapClrType(null));
    }

    [Fact]
    public void MapClrType_ReturnsText_ForStringAndObject()
    {
        Assert.Equal(SemanticStyle.Text, ParameterSpan.MapClrType(typeof(string)));
        Assert.Equal(SemanticStyle.Text, ParameterSpan.MapClrType(typeof(object)));
    }

    [Fact]
    public void HasParameter_ReturnsTrue_WhenMatchExists()
    {
        var spans = new[]
        {
            new ParameterSpan
            {
                FormattedValue = "42",
                SemanticStyle = SemanticStyle.JsonNumber,
                ClrType = typeof(int)
            }
        };
        var properties = new Dictionary<string, object?>
        {
            [LogConstants.ParameterSpans] = spans
        };
        var context = new RenderContext(ReadOnlyMemory<byte>.Empty, properties);

        Assert.True(ParameterSpan.HasParameter(context, typeof(int).FullName!, "42"));
    }

    [Fact]
    public void HasParameter_ReturnsFalse_WhenNoMatch()
    {
        var spans = new[]
        {
            new ParameterSpan
            {
                FormattedValue = "42",
                SemanticStyle = SemanticStyle.JsonNumber,
                ClrType = typeof(int)
            }
        };
        var properties = new Dictionary<string, object?>
        {
            [LogConstants.ParameterSpans] = spans
        };
        var context = new RenderContext(ReadOnlyMemory<byte>.Empty, properties);

        Assert.False(ParameterSpan.HasParameter(context, typeof(string).FullName!, "42"));
        Assert.False(ParameterSpan.HasParameter(context, typeof(int).FullName!, "99"));
    }

    [Fact]
    public void HasParameter_ReturnsFalse_WhenNoSpansInProperties()
    {
        var context = new RenderContext(ReadOnlyMemory<byte>.Empty, LogEntry.EmptyProperties);
        Assert.False(ParameterSpan.HasParameter(context, typeof(int).FullName!, "42"));
    }
}
