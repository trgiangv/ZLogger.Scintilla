using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Rendering;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class BatchPreparerTests
{
    private readonly LogRenderer _renderer = new();
    private readonly BatchPreparer _preparer;

    public BatchPreparerTests()
    {
        _preparer = new BatchPreparer(_renderer);
    }

    [TestMethod]
    public void Prepare_SingleEntry_ProducesTextAndStyles()
    {
        var entry = TestHelper.MakeEntry("Hello world", LogLevel.Information);
        var entries = new List<LogEntry> { entry };

        var batch = _preparer.Prepare(entries, epoch: 1);

        Assert.IsTrue(batch.Text.Count > 0, "Text should contain bytes");
        Assert.IsTrue(batch.Styles.Count > 0, "Styles should contain at least one range");
        Assert.AreEqual(1, batch.EntryCount);
        Assert.AreEqual(1, batch.Epoch);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_TextContainsOriginalMessageBytes()
    {
        const string message = "[12:00:00 INF] Test message";
        var entry = TestHelper.MakeEntry(message, LogLevel.Information);
        var entries = new List<LogEntry> { entry };

        var batch = _preparer.Prepare(entries, epoch: 1);

        var text = Encoding.UTF8.GetString(batch.Text.Array, 0, batch.Text.Count);
        Assert.Contains("Test message", text);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_MultipleEntries_ConcatenatesText()
    {
        var entries = new List<LogEntry>
        {
            TestHelper.MakeEntry("Line one", LogLevel.Information),
            TestHelper.MakeEntry("Line two", LogLevel.Warning)
        };

        var batch = _preparer.Prepare(entries, epoch: 2);

        var text = Encoding.UTF8.GetString(batch.Text.Array, 0, batch.Text.Count);
        Assert.Contains("Line one", text);
        Assert.Contains("Line two", text);
        Assert.AreEqual(2, batch.EntryCount);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_WithJsonBytes_AppendsJsonAndStyleRanges()
    {
        const string jsonContent = "{\n  \"key\": \"value\"\n}";
        var jsonBytes = Encoding.UTF8.GetBytes(jsonContent);
        var properties = new Dictionary<string, object?>
        {
            [LogConstants.JsonBytes] = jsonBytes
        };
        var entry = TestHelper.MakeEntry("[12:00:00 INF] Test", LogLevel.Information, properties);

        _renderer.SetPrettyJson(true);

        var entries = new List<LogEntry> { entry };
        var batch = _preparer.Prepare(entries, epoch: 1);

        var text = Encoding.UTF8.GetString(batch.Text.Array, 0, batch.Text.Count);
        Assert.Contains("\"key\"", text);
        Assert.Contains("\"value\"", text);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_WithJsonBytes_CollectsTokenRangesForLinks()
    {
        const string jsonContent = "{\n  \"url\": \"https://example.com\"\n}";
        var jsonBytes = Encoding.UTF8.GetBytes(jsonContent);
        var properties = new Dictionary<string, object?>
        {
            [LogConstants.JsonBytes] = jsonBytes
        };
        var entry = TestHelper.MakeEntry("[12:00:00 INF] Test", LogLevel.Information, properties);

        _renderer.SetPrettyJson(true);

        var entries = new List<LogEntry> { entry };
        var batch = _preparer.Prepare(entries, epoch: 1);

        Assert.AreEqual(1, batch.Tokens.Count, "The URL inside the JSON should become a link.");

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_WithoutJsonBytes_NoJsonInOutput()
    {
        var entry = TestHelper.MakeEntry("[12:00:00 INF] No JSON here", LogLevel.Information);

        _renderer.SetPrettyJson(true);

        var entries = new List<LogEntry> { entry };
        var batch = _preparer.Prepare(entries, epoch: 1);

        var text = Encoding.UTF8.GetString(batch.Text.Array, 0, batch.Text.Count);
        Assert.DoesNotContain("{", text.Replace("[12:00:00 INF]", ""));

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_EmptyJsonBytes_SkipsJsonBlock()
    {
        var properties = new Dictionary<string, object?>
        {
            [LogConstants.JsonBytes] = Array.Empty<byte>()
        };
        var entry = TestHelper.MakeEntry("[12:00:00 INF] Test", LogLevel.Information, properties);

        _renderer.SetPrettyJson(true);

        var entries = new List<LogEntry> { entry };
        var batch = _preparer.Prepare(entries, epoch: 1);

        var text = Encoding.UTF8.GetString(batch.Text.Array, 0, batch.Text.Count);
        Assert.DoesNotContain("\"$type\"", text);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_StyleRangesSum_MatchesTextByteCount()
    {
        var entry = TestHelper.MakeEntry("Hello", LogLevel.Debug);
        var entries = new List<LogEntry> { entry };

        var batch = _preparer.Prepare(entries, epoch: 1);

        var styleSum = 0;
        for (var i = 0; i < batch.Styles.Count; i++)
            styleSum += batch.Styles.Array[i].Length;

        Assert.AreEqual(batch.Text.Count, styleSum);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_EmptyEntryList_ProducesEmptyBatch()
    {
        var batch = _preparer.Prepare([], epoch: 5);

        Assert.AreEqual(0, batch.EntryCount);
        Assert.AreEqual(0, batch.Text.Count);
        Assert.AreEqual(0, batch.Styles.Count);
        Assert.AreEqual(5, batch.Epoch);

        batch.ReturnBuffers();
    }

    [TestMethod]
    public void Prepare_ReturnBuffers_DoesNotThrow()
    {
        var entry = TestHelper.MakeEntry("test", LogLevel.Information);
        var batch = _preparer.Prepare(new List<LogEntry> { entry }, epoch: 1);

        var ex = TestHelper.CaptureException(batch.ReturnBuffers);
        Assert.IsNull(ex);
    }

}
