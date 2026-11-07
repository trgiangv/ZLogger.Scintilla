using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class FilterSemanticsTests
{
    [TestMethod]
    public void ClampFilterLevel_OutOfRangeValues_AreClamped()
    {
        Assert.AreEqual(LogLevel.Trace, ScintillaLogViewerBase.ClampFilterLevel((LogLevel)(-999)));
        Assert.AreEqual(LogLevel.None,  ScintillaLogViewerBase.ClampFilterLevel((LogLevel)999));
    }

    [TestMethod]
    public void ClampFilterLevel_ValidValues_PassThrough()
    {
        Assert.AreEqual(LogLevel.Trace,   ScintillaLogViewerBase.ClampFilterLevel(LogLevel.Trace));
        Assert.AreEqual(LogLevel.Warning, ScintillaLogViewerBase.ClampFilterLevel(LogLevel.Warning));
        Assert.AreEqual(LogLevel.None,    ScintillaLogViewerBase.ClampFilterLevel(LogLevel.None));
    }
}
