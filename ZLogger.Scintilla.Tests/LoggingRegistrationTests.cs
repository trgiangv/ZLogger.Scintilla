using System.Reflection;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;

namespace ZLogger.Scintilla.Tests;

public sealed class LoggingRegistrationTests
{
    [Fact]
    public void ScintillaLogProcessor_Ctor_MustAcceptPostDelegate()
    {
        var constructors = typeof(ScintillaLogProcessor)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);

        var hasDelegate = constructors.Any(c =>
            c.GetParameters().Any(p =>
                p.ParameterType == typeof(Func<LogEntry, bool>)));

        Assert.True(hasDelegate);
    }
}
