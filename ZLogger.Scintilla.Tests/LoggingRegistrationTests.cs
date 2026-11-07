using System.Reflection;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class LoggingRegistrationTests
{
    [TestMethod]
    public void ScintillaLogProcessor_Ctor_MustAcceptPostDelegate()
    {
        var constructors = typeof(ScintillaLogProcessor)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);

        var hasDelegate = constructors.Any(c =>
            c.GetParameters().Any(p =>
                p.ParameterType == typeof(Func<LogEntry, bool>)));

        Assert.IsTrue(hasDelegate);
    }
}
