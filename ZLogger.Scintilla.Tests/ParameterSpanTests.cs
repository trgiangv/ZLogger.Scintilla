using ZLogger.Scintilla.Models;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class ParameterSpanTests
{
    [TestMethod]
    [DataRow(typeof(string))]
    [DataRow(typeof(int))]
    [DataRow(typeof(long))]
    [DataRow(typeof(double))]
    [DataRow(typeof(bool))]
    [DataRow(typeof(DateTime))]
    [DataRow(typeof(DateTimeOffset))]
    [DataRow(typeof(Guid))]
    [DataRow(typeof(char))]
    [DataRow(typeof(decimal))]
    [DataRow(typeof(byte[]))]
    public void IsLeafType_ReturnsTrue_ForLeafTypes(Type type)
    {
        Assert.IsTrue(ParameterSpan.IsLeafType(type));
    }

    [TestMethod]
    [DataRow(typeof(List<int>))]
    [DataRow(typeof(Dictionary<string, object>))]
    [DataRow(typeof(object))]
    [DataRow(typeof(Exception))]
    public void IsLeafType_ReturnsFalse_ForComplexTypes(Type type)
    {
        Assert.IsFalse(ParameterSpan.IsLeafType(type));
    }

    [TestMethod]
    public void IsLeafType_ReturnsTrue_ForEnums()
    {
        Assert.IsTrue(ParameterSpan.IsLeafType(typeof(SemanticStyle)));
        Assert.IsTrue(ParameterSpan.IsLeafType(typeof(DayOfWeek)));
    }

    [TestMethod]
    public void MapClrType_ReturnsJsonNumber_ForNumericTypes()
    {
        Assert.AreEqual(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(int)));
        Assert.AreEqual(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(long)));
        Assert.AreEqual(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(double)));
        Assert.AreEqual(SemanticStyle.JsonNumber, ParameterSpan.MapClrType(typeof(decimal)));
    }

    [TestMethod]
    public void MapClrType_ReturnsJsonBoolean_ForBool()
    {
        Assert.AreEqual(SemanticStyle.JsonBoolean, ParameterSpan.MapClrType(typeof(bool)));
    }

    [TestMethod]
    public void MapClrType_ReturnsJsonDateTime_ForDateTypes()
    {
        Assert.AreEqual(SemanticStyle.JsonDateTime, ParameterSpan.MapClrType(typeof(DateTime)));
        Assert.AreEqual(SemanticStyle.JsonDateTime, ParameterSpan.MapClrType(typeof(DateTimeOffset)));
    }

    [TestMethod]
    public void MapClrType_ReturnsJsonNull_ForNull()
    {
        Assert.AreEqual(SemanticStyle.JsonNull, ParameterSpan.MapClrType(null));
    }

    [TestMethod]
    public void MapClrType_ReturnsText_ForStringAndObject()
    {
        Assert.AreEqual(SemanticStyle.Text, ParameterSpan.MapClrType(typeof(string)));
        Assert.AreEqual(SemanticStyle.Text, ParameterSpan.MapClrType(typeof(object)));
    }

    [TestMethod]
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

        Assert.IsTrue(ParameterSpan.HasParameter(context, typeof(int).FullName!, "42"));
    }

    [TestMethod]
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

        Assert.IsFalse(ParameterSpan.HasParameter(context, typeof(string).FullName!, "42"));
        Assert.IsFalse(ParameterSpan.HasParameter(context, typeof(int).FullName!, "99"));
    }

    [TestMethod]
    public void HasParameter_ReturnsFalse_WhenNoSpansInProperties()
    {
        var context = new RenderContext(ReadOnlyMemory<byte>.Empty, LogEntry.EmptyProperties);
        Assert.IsFalse(ParameterSpan.HasParameter(context, typeof(int).FullName!, "42"));
    }
}
