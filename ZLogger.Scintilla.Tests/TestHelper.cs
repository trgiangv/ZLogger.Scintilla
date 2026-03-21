using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;

namespace ZLogger.Scintilla.Tests;

internal static class TestHelper
{
    public static LogEntry MakeEntry(string text, LogLevel level)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new LogEntry
        {
            Level = level,
            Message = new ArraySegment<byte>(bytes),
            Properties = LogEntry.EmptyProperties
        };
    }

    public static LogEntry MakeEntry(
        string text,
        LogLevel level,
        IReadOnlyDictionary<string, object?> properties)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new LogEntry
        {
            Level = level,
            Message = new ArraySegment<byte>(bytes),
            Properties = properties
        };
    }
}
