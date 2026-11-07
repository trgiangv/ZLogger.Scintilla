using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Verifies that <see cref="ScintillaLogFormatter"/> and <see cref="ScintillaLogProcessor"/>
/// do NOT produce ZLogger's auto-serialized JSON for <see cref="IEnumerable{T}"/> parameters.
/// ZLogger internally calls <c>JsonSerializer.Serialize</c> for non-<see cref="IFormattable"/>
/// <see cref="System.Collections.IEnumerable"/> types. Our formatter detects those values and
/// prints them with <c>value.ToString()</c> instead.
/// </summary>
[TestClass]
public sealed class EnumerableSerializationTests
{
    [TestMethod]
    public void Processor_ListOfInts_MessageDoesNotContainAutoSerializedJson()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var data = new List<int> { 1, 2, 3 };
            logger.ZLogInformation($"Data: {data}");
        });

        var messageText = GetMessageText(captured);

        Assert.DoesNotContain("[1,2,3]", messageText);
        Assert.DoesNotContain("\"1\"", messageText);
    }

    [TestMethod]
    public void Processor_ListOfStrings_MessageDoesNotContainAutoSerializedJson()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var items = new List<string> { "alpha", "beta", "gamma" };
            logger.ZLogInformation($"Items: {items}");
        });

        var messageText = GetMessageText(captured);

        Assert.DoesNotContain("[\"alpha\"", messageText);
        Assert.DoesNotContain("\"alpha\",\"beta\"", messageText);
    }

    [TestMethod]
    public void Processor_Array_MessageDoesNotContainAutoSerializedJson()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var arr = new[] { "x", "y", "z" };
            logger.ZLogInformation($"Arr: {arr}");
        });

        var messageText = GetMessageText(captured);

        Assert.DoesNotContain("[\"x\"", messageText);
        Assert.DoesNotContain("\"x\",\"y\"", messageText);
    }

    [TestMethod]
    public void Processor_PlainStringParameter_NotTreatedAsEnumerable()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var msg = "hello world";
            logger.ZLogInformation($"Message: {msg}");
        });

        var messageText = GetMessageText(captured);

        Assert.Contains("hello world", messageText);
        Assert.DoesNotContain("[\"h\"", messageText);
    }

    [TestMethod]
    public void Processor_ParameterSpans_FormattedValueUsesToString()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var data = new List<int> { 1, 2, 3 };
            logger.ZLogInformation($"Data: {data}");
        });

        Assert.IsTrue(captured.Properties.ContainsKey(LogConstants.ParameterSpans));
        var spans = (ParameterSpan[])captured.Properties[LogConstants.ParameterSpans]!;
        Assert.ContainsSingle(spans);

        var expected = new List<int> { 1, 2, 3 }.ToString()!;
        Assert.AreEqual(expected, spans[0].FormattedValue);
    }

    [TestMethod]
    public void Processor_ParameterSpans_FormattedValueMatchesMessageText()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var data = new List<int> { 1, 2, 3 };
            logger.ZLogInformation($"Data: {data}");
        });

        var messageText = GetMessageText(captured);
        var spans = (ParameterSpan[])captured.Properties[LogConstants.ParameterSpans]!;

        Assert.Contains(spans[0].FormattedValue, messageText);
    }

    [TestMethod]
    public void Processor_ListOfCustomObjects_MessageDoesNotContainAutoSerializedJson()
    {
        var captured = CaptureLogEntry(logger =>
        {
            var ids = new List<FakeElementId>
            {
                new(1001), new(1002), new(1003)
            };
            logger.ZLogInformation($"Selected: {ids}");
        });

        var messageText = GetMessageText(captured);

        Assert.DoesNotContain("IntegerValue", messageText);
        Assert.DoesNotContain("[{", messageText);
    }

    private sealed class FakeElementId
    {
        public FakeElementId(int v) { IntegerValue = v; }
        public int IntegerValue { get; }
        // Mirrors Revit's ElementId, so that a serializer would have this property to pick up.
        // ReSharper disable once UnusedMember.Local
        public long Value => IntegerValue;
        public override string ToString() => IntegerValue.ToString();
    }

    private static LogEntry CaptureLogEntry(Action<ILogger> logAction)
    {
        LogEntry? captured = null;
        var processor = new ScintillaLogProcessor(entry =>
        {
            captured = entry;
            return true;
        });

        // Disposing the factory flushes ZLogger, so the entry is captured once this block ends.
        using (var factory = LoggerFactory.Create(builder =>
               {
                   builder.SetMinimumLevel(LogLevel.Trace);
                   builder.AddZLoggerLogProcessor(processor);
               }))
            logAction(factory.CreateLogger("Test"));

        Assert.IsNotNull(captured);
        return captured;
    }

    private static string GetMessageText(LogEntry entry)
    {
        return Encoding.UTF8.GetString(
            entry.Message.Array!,
            entry.Message.Offset,
            entry.Message.Count);
    }
}
