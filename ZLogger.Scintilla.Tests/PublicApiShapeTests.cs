using System.Reflection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using LoggingBuilderExtensions = ZLogger.Scintilla.Public.LoggingBuilderExtensions;

namespace ZLogger.Scintilla.Tests;

public sealed class PublicApiShapeTests
{
    [Fact]
    public void ScintillaLogViewerBase_HasNoStringFilterOverload()
    {
        var stringFilter = typeof(ScintillaLogViewerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m =>
                m.Name == nameof(ScintillaLogViewerBase.SetFilter) &&
                m.GetParameters() is { Length: 1 } p &&
                p[0].ParameterType == typeof(string));

        Assert.Null(stringFilter);
    }

    [Fact]
    public void ScintillaLogViewerBase_HasLogLevelFilterOverload()
    {
        var logLevelFilter = typeof(ScintillaLogViewerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m =>
                m.Name == nameof(ScintillaLogViewerBase.SetFilter) &&
                m.GetParameters() is { Length: 1 } p &&
                p[0].ParameterType == typeof(LogLevel));

        Assert.NotNull(logLevelFilter);
    }

    [Fact]
    public void ScintillaLogViewerBase_HasFindNextAsync()
    {
        var method = typeof(ScintillaLogViewerBase).GetMethod(
            nameof(ScintillaLogViewerBase.FindNextAsync),
            [typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<SearchPosition>), method!.ReturnType);
    }

    [Fact]
    public void ScintillaLogViewerBase_HasFindPreviousAsync()
    {
        var method = typeof(ScintillaLogViewerBase).GetMethod(
            nameof(ScintillaLogViewerBase.FindPreviousAsync),
            [typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<SearchPosition>), method!.ReturnType);
    }

    [Fact]
    public void ScintillaLogViewerBase_ExactPublicMemberSet()
    {
        var expected = new[]
        {
            "HostElement",
            "ScintillaControl",
            "Start", "Stop",
            "Clear",
            "SetTheme", "SetPrettyJson", "SetJsonDepth", "SetLineNumbers",
            "SetFilter",
            "HighlightSearch", "ClearSearch",
            "FindNext", "FindNextAsync",
            "FindPrevious", "FindPreviousAsync",
            "CountMatchesAsync",
            "RenderedMessages", "DroppedMessages", "PendingEntryCount", "HistoryEntries",
            "RegisterDisposeAction",
            "Dispose",
        };

        var actual = typeof(ScintillaLogViewerBase)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodInfo mi || !mi.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        var missing = expected.Except(actual).ToArray();
        var extra = actual.Except(expected).ToArray();

        Assert.True(missing.Length == 0, $"Missing members: {string.Join(", ", missing)}");
        Assert.True(extra.Length == 0, $"Unexpected members: {string.Join(", ", extra)}");
    }

    [Fact]
    public void OnlyAllowedPublicInterfaces_AreExported()
    {
        var assembly = typeof(ScintillaLogViewerBase).Assembly;
        var publicInterfaces = assembly.GetExportedTypes()
            .Where(t => t.IsInterface && (t.Namespace?.StartsWith("ZLogger.Scintilla") ?? false))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        var expected = new[]
        {
            nameof(ILogColorizer),
            nameof(ILinkifier),
            nameof(IScintillaTheme),
        };

        var missing = expected.Except(publicInterfaces).ToArray();
        var extra = publicInterfaces.Except(expected).ToArray();

        Assert.True(missing.Length == 0, $"Missing public interfaces: {string.Join(", ", missing)}");
        Assert.True(extra.Length == 0,
            $"Unexpected public interfaces (add to expected or make internal): {string.Join(", ", extra)}");
    }

    [Fact]
    public void ScintillaOptions_HasNoStringFilterMethod()
    {
        var methods = typeof(ScintillaOptions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var stringFilter = methods.FirstOrDefault(m =>
            m.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name == "level"));

        Assert.Null(stringFilter);
    }

    [Fact]
    public void LoggingBuilderExtensions_HasNoStringFilterOverloads()
    {
        var methods = typeof(LoggingBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static);

        var stringFilterOverloads = methods
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string) && p.Name is "level" or "filterLevel"))
            .ToArray();

        Assert.Empty(stringFilterOverloads);
    }
}
