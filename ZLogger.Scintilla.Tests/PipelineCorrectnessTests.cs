using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Correctness of what the pipeline produces, with no window needed.
/// </summary>
[TestClass]
public sealed class PipelineCorrectnessTests
{
    [TestMethod]
    public void DropWrite_CountsDropsAndKeepsPendingAccurate()
    {
        const int capacity = 10;
        const int posted = 100;
        var viewer = new ScintillaLogViewer(o => o.Channel(capacity: capacity, dropPolicy: BoundedChannelFullMode.DropWrite));

        for (var i = 0; i < posted; i++)
            viewer.Pump.TryPost(TestHelper.MakeEntry($"line {i}", LogLevel.Information));

        Assert.AreEqual(posted - capacity, viewer.DroppedMessages);
        Assert.AreEqual(capacity, viewer.PendingEntryCount);

        viewer.Dispose();
    }

    [TestMethod]
    [DataRow(BoundedChannelFullMode.DropOldest)]
    [DataRow(BoundedChannelFullMode.DropNewest)]
    [DataRow(BoundedChannelFullMode.Wait)]
    public void OtherPolicies_KeepTheSameAccounts(BoundedChannelFullMode policy)
    {
        const int capacity = 10;
        const int posted = 100;
        var viewer = new ScintillaLogViewer(o => o.Channel(capacity: capacity, dropPolicy: policy));

        for (var i = 0; i < posted; i++)
            viewer.Pump.TryPost(TestHelper.MakeEntry($"line {i}", LogLevel.Information));

        Assert.AreEqual(posted - capacity, viewer.DroppedMessages);
        Assert.AreEqual(capacity, viewer.PendingEntryCount);
        viewer.Dispose();
    }

    [TestMethod]
    public void Timestamp_IsLocalTime()
    {
        var before = DateTime.Now;
        var text = Capture(logger => logger.ZLogInformation($"hello"));
        var after = DateTime.Now;

        var printed = text.Substring(1, 8);
        CollectionAssert.Contains(new[] { before.ToString("HH:mm:ss"), after.ToString("HH:mm:ss") }, printed,
            $"Expected local time between {before:HH:mm:ss} and {after:HH:mm:ss}, printed {printed}.");
    }

    [TestMethod]
    public void Interpolated_HonoursFormatAlignmentAndEscapes()
    {
        const double v = 1.23456;
        const string s = "ab";

        StringAssert.EndsWith(Capture(logger => logger.ZLogInformation($"v={v:F2}")), "v=1.23");
        StringAssert.EndsWith(Capture(logger => logger.ZLogInformation($"[{s,6}]")), "[    ab]");
        StringAssert.EndsWith(Capture(logger => logger.ZLogInformation($"{{lit}} {v:F1}")), "{lit} 1.2");
    }

    [TestMethod]
    public void Template_HonoursFormatAlignmentAndEscapes()
    {
        const double v = 1.23456;

        StringAssert.EndsWith(Capture(logger => logger.LogInformation("v={V:F2}", v)), "v=1.23");
        StringAssert.EndsWith(Capture(logger => logger.LogInformation("[{S,6}]", "ab")), "[    ab]");
        StringAssert.EndsWith(Capture(logger => logger.LogInformation("{{lit}} {V:F1}", v)), "{lit} 1.2");
    }

    [TestMethod]
    public void EnumerableNextToFormattedValue_KeepsBoth()
    {
        const double v = 1.23456;
        var list = new List<int> { 1, 2, 3 };

        var text = Capture(logger => logger.ZLogInformation($"{{lit}} v={v:F2} list={list} end"));

        StringAssert.Contains(text, "{lit} v=1.23 list=");
        StringAssert.EndsWith(text, " end");
        Assert.DoesNotContain("[1,2,3]", text);
    }

    [TestMethod]
    public void ThrowingToString_KeepsTheEntry()
    {
        var bad = new ThrowsOnToString();

        var interpolated = Capture(logger => logger.ZLogInformation($"before {bad} after"));
        var template = Capture(logger => logger.LogInformation("before {Bad} after", bad));

        StringAssert.Contains(interpolated, "before");
        StringAssert.Contains(interpolated, "after");
        StringAssert.Contains(interpolated, nameof(ThrowsOnToString));
        StringAssert.Contains(template, "before");
        StringAssert.Contains(template, "after");
    }

    [TestMethod]
    public void ToStringThrowingMidMessage_LeavesNoPartialText()
    {
        const int number = 7;
        var bad = new ThrowsOnToString();

        var text = Capture(logger => logger.ZLogInformation($"first {number} then {bad} last {number}"));

        // ZLogger writes "first 7 then " before it reaches the failing value, and that must not appear twice.
        Assert.AreEqual(text.IndexOf("first", StringComparison.Ordinal), text.LastIndexOf("first", StringComparison.Ordinal), text);
        StringAssert.Contains(text, $"first 7 then <{nameof(ThrowsOnToString)}> last 7");
    }

    [TestMethod]
    public void ToStringThatLogs_DoesNotCorruptTheOuterMessage()
    {
        var entries = CaptureAll((logger, _) =>
        {
            var nested = new LogsInToString(logger);
            logger.ZLogInformation($"outer-before {nested} outer-after");
        });

        // The value's ToString runs for the message and again for its span, so the nested line can appear twice.
        StringAssert.EndsWith(GetText(entries.Single(e => GetText(e).Contains("outer"))), "outer-before nested-value outer-after");
        var inner = entries.Where(e => !GetText(e).Contains("outer")).ToList();
        Assert.IsNotEmpty(inner);
        foreach (var entry in inner)
            StringAssert.EndsWith(GetText(entry), "inner 99");
    }

    [TestMethod]
    public void ToStringThatLogs_KeepsTheOuterParameterSpans()
    {
        const int first = 1;
        var entries = CaptureAll((logger, _) =>
        {
            var nested = new LogsInToString(logger);
            logger.ZLogInformation($"a {first} b {nested} end");
        });

        var outer = entries.Single(e => GetText(e).Contains("end"));
        var spans = (ParameterSpan[])outer.Properties[LogConstants.ParameterSpans]!;
        CollectionAssert.AreEqual(new[] { "1", "nested-value" }, spans.Select(s => s.FormattedValue).ToArray());
    }

    [TestMethod]
    public void JsonOfAnObjectThatLogs_DoesNotCorruptTheOuterJson()
    {
        var entries = CaptureAll((logger, processor) =>
        {
            processor.SetPrettyJson(true);
            var value = new LogsInProperty(logger);
            logger.ZLogInformation($"outer {value}");
        });

        var outer = entries.Single(e => GetText(e).Contains("outer"));
        using var document = JsonDocument.Parse((byte[])outer.Properties[LogConstants.JsonBytes]!);
        Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [TestMethod]
    public void ADeferredSinkThatIsFull_LeavesFormattingToTheCaller()
    {
        var deferred = 0;
        var entries = CaptureAll((logger, processor) =>
        {
            processor.SetDeferredSink(_ =>
            {
                deferred++;
                return false;
            }, System.Globalization.CultureInfo.CurrentCulture);
            logger.ZLogInformation($"not lost");
        });

        Assert.AreEqual(1, deferred);
        StringAssert.EndsWith(GetText(entries.Single()), "not lost");
    }

    [TestMethod]
    public void OnlyImmutableValues_AreOfferedForDeferredFormatting()
    {
        var deferred = 0;
        var list = new List<int> { 1 };
        var exception = new InvalidOperationException("boom");
        const int number = 1;
        const string text = "a";

        CaptureAll((logger, processor) =>
        {
            processor.SetDeferredSink(entry =>
            {
                deferred++;
                entry.Return();
                return true;
            }, System.Globalization.CultureInfo.CurrentCulture);

            logger.ZLogInformation($"{number} {text}");
            logger.LogInformation("{Number} {Text}", number, text);
            Assert.AreEqual(2, deferred, "Numbers and strings can wait.");

            logger.ZLogInformation($"{list}");
            logger.LogInformation("{List}", list);
            logger.ZLogError(exception, $"{number}");
            logger.Log(LogLevel.Information, default, "custom state", null, static (state, _) => state);
            Assert.AreEqual(2, deferred, "Objects, exceptions and custom formatters must not.");
        });
    }

    [TestMethod]
    public void CustomSerializer_ProducesARealSpan()
    {
        var value = new CustomValue();
        var entry = CaptureEntry(logger => logger.ZLogInformation($"got {value}"),
            processor => processor.SetCustomSerializer(new CustomValueSerializer()));

        var spans = (ParameterSpan[])entry.Properties[LogConstants.ParameterSpans]!;
        Assert.AreEqual("custom-text", spans[0].FormattedValue);
        Assert.AreEqual(typeof(CustomValue), spans[0].ClrType);
        StringAssert.EndsWith(GetText(entry), "got custom-text");
    }

    [TestMethod]
    public void Json_KeepsNonAsciiAndAmpersandReadable()
    {
        var value = new JsonPayload("Tiếng Việt", "https://example.com/?a=1&b=2");
        var entry = CaptureEntry(logger => logger.ZLogInformation($"got {value}"),
            processor => processor.SetPrettyJson(true));

        var json = Encoding.UTF8.GetString((byte[])entry.Properties[LogConstants.JsonBytes]!);

        StringAssert.Contains(json, "Tiếng Việt");
        StringAssert.Contains(json, "https://example.com/?a=1&b=2");
    }

    [TestMethod]
    public void AttachProcessor_CopiesPrettyJson()
    {
        var viewer = new ScintillaLogViewer();
        viewer.SetPrettyJson(true);
        LogEntry? captured = null;
        var processor = new ScintillaLogProcessor(entry =>
        {
            captured = entry;
            return true;
        });
        viewer.AttachProcessor(processor);
        var value = new JsonPayload("a", "b");

        using (var factory = CreateFactory(processor))
            factory.CreateLogger("T").ZLogInformation($"got {value}");

        Assert.IsNotNull(captured);
        Assert.IsTrue(captured.Properties.ContainsKey(LogConstants.JsonBytes), "The pretty JSON flag was not copied.");
        viewer.Dispose();
    }

    [TestMethod]
    [DataRow("file:///c:/windows/system32/calc.exe")]
    [DataRow("ms-settings://network")]
    [DataRow("search-ms://query=x")]
    [DataRow("javascript://alert(1)")]
    [DataRow("ftp://example.com/x")]
    [DataRow("calculator://")]
    public void LinksRefuseUnsafeSchemes(string url)
    {
        Assert.IsFalse(UrlScanner.TryNormalizeUri(url, out _), $"{url} would be shell-executed on click.");
    }

    [TestMethod]
    [DataRow("https://example.com/a?b=1&c=2")]
    [DataRow("http://example.com")]
    [DataRow("HTTPS://EXAMPLE.COM")]
    [DataRow("www.example.com")]
    public void LinksKeepWebSchemes(string url)
    {
        Assert.IsTrue(UrlScanner.TryNormalizeUri(url, out _));
    }

    [TestMethod]
    public void PooledSegmentReturn_DropsPayloadReferences()
    {
        var ranges = ArrayPool<StyleRange>.Shared.Rent(2);
        var tokens = ArrayPool<TokenRange>.Shared.Rent(2);
        var payload = TokenPayload.ForLink(() => { });
        ranges[0] = new StyleRange(1, 1, payload, isLink: true);
        tokens[0] = new TokenRange(0, 1, payload);

        new PooledSegment<StyleRange>(ranges, 1).Return();
        new PooledSegment<TokenRange>(tokens, 1).Return();

        Assert.IsNull(ranges[0].TokenPayload, "A returned style array still holds its click action.");
        Assert.IsNull(tokens[0].Payload, "A returned token array still holds its click action.");
    }

    private static List<LogEntry> CaptureAll(Action<ILogger, ScintillaLogProcessor> log)
    {
        var entries = new List<LogEntry>();
        var processor = new ScintillaLogProcessor(entry =>
        {
            entries.Add(entry);
            return true;
        });

        // Disposed on the way out, before the caller sees the list, so the processor has flushed every entry.
        using var factory = CreateFactory(processor);
        log(factory.CreateLogger("Test"), processor);
        return entries;
    }

    private sealed class LogsInToString(ILogger logger)
    {
        public override string ToString()
        {
            const int inner = 99;
            logger.ZLogInformation($"inner {inner}");
            return "nested-value";
        }
    }

    private sealed class LogsInProperty(ILogger logger)
    {
        // Read by the JSON serializer, which is the point: the getter logs while a message is being formatted.
        // ReSharper disable once UnusedMember.Local
        public string Name
        {
            get
            {
                var payload = new JsonPayload("inner", "inner");
                logger.ZLogInformation($"inner {payload}");
                return "outer-name";
            }
        }
    }

    private static string Capture(Action<ILogger> log) => GetText(CaptureEntry(log));

    private static LogEntry CaptureEntry(Action<ILogger> log, Action<ScintillaLogProcessor>? configure = null)
    {
        LogEntry? captured = null;
        var processor = new ScintillaLogProcessor(entry =>
        {
            captured = entry;
            return true;
        });
        configure?.Invoke(processor);

        using (var factory = CreateFactory(processor))
            log(factory.CreateLogger("Test"));

        Assert.IsNotNull(captured, "The entry was lost.");
        return captured;
    }

    // Same ZLogger options as LoggingBuilderExtensions, so that these tests see what a real viewer sees.
    private static ILoggerFactory CreateFactory(ScintillaLogProcessor processor) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddZLoggerLogProcessor(options =>
            {
                options.IsFormatLogImmediatelyInStandardLog = false;
                return processor;
            });
        });

    private static string GetText(LogEntry entry) =>
        Encoding.UTF8.GetString(entry.Message.Array!, entry.Message.Offset, entry.Message.Count);

    private sealed class ThrowsOnToString
    {
        public override string ToString() => throw new InvalidOperationException("no text for you");
    }

    private sealed class CustomValue;

    // Read by the JSON serializer.
    // ReSharper disable NotAccessedPositionalProperty.Local
    private sealed record JsonPayload(string Name, string Url);
    // ReSharper restore NotAccessedPositionalProperty.Local

    private sealed class CustomValueSerializer : ICustomSerializer
    {
        public bool CanSerialize(Type type) => type == typeof(CustomValue);
        public void WriteJson(Utf8JsonWriter writer, object value, int maxDepth, int maxItems) => writer.WriteStringValue("custom-json");
        public string FormatText(object? value) => "custom-text";
    }
}
