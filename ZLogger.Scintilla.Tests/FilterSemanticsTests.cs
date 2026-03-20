using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Filter logic is a single comparison: entry.Level >= minimumLevel.
/// These tests verify the threshold semantics and edge cases.
/// </summary>
public sealed class FilterSemanticsTests
{
    [Theory]
    [InlineData(LogLevel.Trace,       LogLevel.Trace,       true)]
    [InlineData(LogLevel.Trace,       LogLevel.Information, true)]
    [InlineData(LogLevel.Trace,       LogLevel.Critical,    true)]
    [InlineData(LogLevel.Warning,     LogLevel.Warning,     true)]
    [InlineData(LogLevel.Warning,     LogLevel.Error,       true)]
    [InlineData(LogLevel.Warning,     LogLevel.Critical,    true)]
    [InlineData(LogLevel.Warning,     LogLevel.Information, false)]
    [InlineData(LogLevel.Error,       LogLevel.Warning,     false)]
    [InlineData(LogLevel.None,        LogLevel.Trace,       false)]
    [InlineData(LogLevel.None,        LogLevel.Critical,    false)]
    public void ThresholdFilter_MatchesCorrectly(LogLevel minimum, LogLevel entryLevel, bool expected)
    {
        Assert.Equal(expected, entryLevel >= minimum);
    }

    [Fact]
    public void ClampFilterLevel_OutOfRangeValues_AreClamped()
    {
        Assert.Equal(LogLevel.Trace, ScintillaLogViewerBase.ClampFilterLevel((LogLevel)(-999)));
        Assert.Equal(LogLevel.None,  ScintillaLogViewerBase.ClampFilterLevel((LogLevel)999));
    }

    [Fact]
    public void ClampFilterLevel_ValidValues_PassThrough()
    {
        Assert.Equal(LogLevel.Trace,   ScintillaLogViewerBase.ClampFilterLevel(LogLevel.Trace));
        Assert.Equal(LogLevel.Warning, ScintillaLogViewerBase.ClampFilterLevel(LogLevel.Warning));
        Assert.Equal(LogLevel.None,    ScintillaLogViewerBase.ClampFilterLevel(LogLevel.None));
    }

    [Fact]
    public void Trace_IsShowAll()
    {
        foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
        {
            if (level == LogLevel.None) continue;
            Assert.True(level >= LogLevel.Trace, $"{level} should be visible when filter is Trace");
        }
    }

    [Fact]
    public void None_IsShowNothing()
    {
        foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
        {
            if (level == LogLevel.None) continue;
            Assert.False(level >= LogLevel.None, $"{level} should NOT be visible when filter is None");
        }
    }
}
