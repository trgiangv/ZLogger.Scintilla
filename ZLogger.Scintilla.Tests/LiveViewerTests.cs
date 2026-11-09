using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;

// Every lambda over `live` runs, or is joined, before the test's `using` disposes it.
// ReSharper disable AccessToDisposedClosure
namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Behaviour that only exists with a message loop: rendering, the UI drain, scrolling, search and clicks.
/// </summary>
[TestClass]
public sealed class LiveViewerTests
{
    private static readonly string LongTail = string.Join(' ', Enumerable.Range(0, 60).Select(i => "word" + i));

    [TestMethod]
    public void AutoScrollWithWordWrap_ShowsTheLastMessage()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 3000; i++)
            live.Logger.LogInformation("line {I} {Tail}", i, LongTail);
        live.Logger.LogInformation("tail-sentinel");

        live.WaitForLine("tail-sentinel");
        Assert.IsTrue(live.WaitFor(live.TailVisible, 5000), "The last message never became visible.");

        // Word wrap keeps measuring lines after the drain, which must not push the last message off screen.
        Thread.Sleep(1000);
        Assert.IsTrue(live.TailVisible(), "The last message scrolled off screen once wrapping settled.");
    }

    [TestMethod]
    public void SetFilterWithActiveSearch_DoesNotHangTheUiThread()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 3000; i++)
        {
            if (i % 2 == 0)
                live.Logger.LogInformation("Tiếng Việt needle {I}", i);
            else
                live.Logger.LogWarning("Tiếng Việt needle {I}", i);
        }

        live.Logger.LogWarning("sentinel");
        live.WaitForLine("sentinel");
        live.Viewer.HighlightSearch("needle");

        foreach (var level in new[] { LogLevel.Warning, LogLevel.Trace, LogLevel.Warning, LogLevel.Information })
        {
            live.Viewer.SetFilter(level);
            live.WaitForIdle();
        }

        Assert.AreEqual(3001, live.LineCount());
    }

    [TestMethod]
    public void FilterReplayDuringIngest_DoesNotKillThePump()
    {
        using var live = new LiveViewer();
        using var stop = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                live.Logger.LogInformation("see https://a.example/x?i={I} and http://b.example/y count={N}", i, i * 2);
                i++;
            }
        });

        for (var i = 0; i < 100; i++)
        {
            live.Viewer.SetFilter(i % 2 == 0 ? LogLevel.Information : LogLevel.Debug);
            Thread.Sleep(5);
        }

        stop.Cancel();
        producer.Wait();
        live.Viewer.SetFilter(LogLevel.Trace);

        // The flood above overflows the channel on purpose. A line logged now could be dropped by policy,
        // so wait for the pump to empty the channel, which also shows that the pump is still alive.
        Assert.IsTrue(live.WaitFor(() => live.Viewer.PendingEntryCount == 0, 30_000), "The pump stopped reading.");
        live.Logger.LogInformation("still-rendering");
        live.WaitForLine("still-rendering", 30_000);
    }

    [TestMethod]
    public void ThrowingToString_NeitherReachesTheCallerNorLosesTheEntry()
    {
        using var live = new LiveViewer();
        var bad = new ThrowsOnToString();

        // Through the real registration: a template is formatted inside ZLogger's Log() unless told otherwise.
        live.Logger.LogInformation("template before {Bad} after", bad);
        live.Logger.ZLogInformation($"interpolated before {bad} after");

        live.WaitForLine("interpolated before");
        var text = live.DocumentText();
        StringAssert.Contains(text, "template before");
        StringAssert.Contains(text, nameof(ThrowsOnToString));
    }

    [TestMethod]
    public void AnEntryThatFailsToRender_DoesNotStopLaterEntries()
    {
        var live = new LiveViewer();
        try
        {
            var poison = new LogEntry
            {
                Level = LogLevel.Information,
                Message = new ArraySegment<byte>(Encoding.UTF8.GetBytes("poison message")),
                Properties = new ThrowingProperties()
            };

            live.Viewer.Pump.TryPost(poison);
            live.Logger.LogInformation("after-poison");
            live.WaitForLine("after-poison");
        }
        finally
        {
            live.Dispose();
        }

        Assert.IsNull(live.DisposeFailure, $"Dispose rethrew: {live.DisposeFailure}");
    }

    [TestMethod]
    public void HistoryDisabledWithFilter_KeepsRendering()
    {
        const int total = 40_000;
        using var live = new LiveViewer(o => o.History(disabled: true));
        live.Viewer.SetFilter(LogLevel.Warning);

        for (var i = 0; i < total; i++)
        {
            if (i % 2 == 0)
                live.Logger.LogInformation("hidden {I}", i);
            else
                live.Logger.LogWarning("shown {I}", i);
        }

        live.Logger.LogWarning("sentinel");
        live.WaitForLine("sentinel", 30_000);
        Assert.AreEqual(total / 2 + 1, live.LineCount());
    }

    [TestMethod]
    public void LinesThatArriveDuringAReplay_AreNotLost()
    {
        using var live = new LiveViewer(o => o.Channel(flushMs: 5));
        live.Logger.LogInformation("before-replay");
        live.WaitForLine("before-replay");

        // Hold the UI thread so the order of events is forced: a drain is already queued when the replay
        // snapshot is taken, then more lines are drained ahead of the replay that clears the document.
        var blocked = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        _ = live.Ui(() => live.Control.BeginInvoke(() =>
        {
            blocked.Set();
            release.Wait();
        }));
        blocked.Wait();

        live.Logger.LogInformation("queued-drain");
        Thread.Sleep(100);
        live.Viewer.SetFilter(LogLevel.Debug);
        Thread.Sleep(100);
        live.Logger.LogInformation("after-snapshot");
        Thread.Sleep(100);
        release.Set();

        live.WaitForIdle();
        var text = live.DocumentText();
        StringAssert.Contains(text, "before-replay");
        StringAssert.Contains(text, "queued-drain");
        StringAssert.Contains(text, "after-snapshot");
        Assert.AreEqual(3, live.LineCount());
    }

    [TestMethod]
    public void FilterReplayUnderLoad_LosesNoLines()
    {
        const int total = 30_000;
        using var live = new LiveViewer(o => o.Display(maxLines: 100_000).History(maxEntries: 100_000));
        var producer = Task.Run(() =>
        {
            for (var i = 0; i < total; i++)
            {
                live.Logger.LogInformation("msg {I} end", i);
                if (i % 200 == 0)
                    Thread.Sleep(1);
            }
        });

        while (!producer.IsCompleted)
        {
            live.Viewer.SetFilter(LogLevel.Debug);
            Thread.Sleep(15);
            live.Viewer.SetFilter(LogLevel.Information);
            Thread.Sleep(15);
        }

        producer.Wait();
        live.WaitForIdle();

        var seen = live.DocumentLines()
            .Select(line => Regex.Match(line, @"msg ([\d,.]+) end"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .Count();
        Assert.AreEqual(total, seen);
    }

    [TestMethod]
    public void AutoScroll_LeavesTheCaretAlone()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 20; i++)
            live.Logger.LogInformation("needle {I}", i);
        live.Logger.LogInformation("first-batch-end");
        live.WaitForLine("first-batch-end");
        live.Viewer.HighlightSearch("needle");

        var first = live.Viewer.FindNext();
        var caret = live.Ui(() => (int)live.Control.DirectMessage(LogConstants.SciGetCurrentPos));

        for (var i = 0; i < 20; i++)
            live.Logger.LogInformation("other {I}", i);
        live.Logger.LogInformation("second-batch-end");
        live.WaitForLine("second-batch-end");

        Assert.AreEqual(caret, live.Ui(() => (int)live.Control.DirectMessage(LogConstants.SciGetCurrentPos)), "Logs arriving moved the caret.");
        Assert.AreEqual(first.Current + 1, live.Viewer.FindNext().Current, "FindNext lost its place.");
    }

    [TestMethod]
    public void TrimWithNonAsciiText_KeepsLinkTokensAligned()
    {
        var linkifier = new RecordingLinkifier();
        using var live = new LiveViewer(o => o.Display(maxLines: 50).WithLinkify(linkifier));
        for (var i = 0; i < 300; i++)
            live.Logger.LogInformation("Tiếng Việt có dấu đầy đủ LINK{I} cuối", i);
        live.WaitForLine("LINK299");
        live.WaitForIdle();

        live.ClickOn("LINK299");
        Assert.IsTrue(live.WaitFor(() => linkifier.Clicked.Count > 0, 2000), "The click hit no link token.");
        Assert.AreEqual("LINK299", linkifier.Clicked.Last());
    }

    [TestMethod]
    public void HighlightWithNonAsciiText_FindsEveryVisibleMatch()
    {
        using var live = new LiveViewer(height: 700);

        // Non-ASCII text above the visible area is what separates byte positions from character positions.
        for (var i = 0; i < 2000; i++)
            live.Logger.LogInformation("Tiếng Việt có đầy đủ dấu, dòng phía trên số {I}", i);
        for (var i = 0; i < 15; i++)
            live.Logger.LogInformation("Tiếng Việt có dấu needle {I}", i);
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel");
        Assert.IsTrue(live.WaitFor(live.TailVisible, 5000));

        live.Viewer.HighlightSearch("needle");

        Assert.AreEqual(15, live.HighlightCount());
    }

    [TestMethod]
    public void SearchRank_CountsFromOne()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 5; i++)
            live.Logger.LogInformation("needle {I}", i);
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel");
        live.Viewer.HighlightSearch("needle");

        var ranks = Enumerable.Range(0, 5).Select(_ => live.Viewer.FindNext().Current).ToArray();

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, ranks);
    }

    [TestMethod]
    [DataRow(@"\bERR\b", 3)]
    [DataRow(@"(ERR|WRN)\]", 5)]
    [DataRow(@"^\[", 8)]
    [DataRow(@"value=\d+$", 8)]
    public async Task RegexNavigationAndCount_Agree(string pattern, int expected)
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 3; i++)
            live.Logger.LogError("value={I}", i);
        for (var i = 0; i < 2; i++)
            live.Logger.LogWarning("value={I}", i);
        for (var i = 0; i < 3; i++)
            live.Logger.LogInformation("value={I}", i);
        live.WaitForLine("value=2");
        live.WaitForIdle();

        live.Viewer.HighlightSearch(pattern, useRegex: true);

        Assert.AreEqual(expected, await live.Viewer.CountMatchesAsync());
        var position = live.Viewer.FindNext();
        Assert.AreEqual(1, position.Current, "Navigation found nothing.");
        Assert.AreEqual(expected, position.Total);
    }

    [TestMethod]
    public void RepeatedClear_LeavesAWorkingDocument()
    {
        using var live = new LiveViewer();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            live.Logger.LogInformation("cycle {Cycle}", cycle);
            live.WaitForLine($"cycle {cycle}");
            live.Ui(() => live.Viewer.Clear());
            Assert.AreEqual(0, live.LineCount());
        }

        live.Logger.LogInformation("after-clear");
        live.WaitForLine("after-clear");
    }

    [TestMethod]
    public void HistoryTrimDuringReplay_KeepsEveryLineIntact()
    {
        using var live = new LiveViewer(o => o.History(maxEntries: 200).Channel(flushMs: 1));
        using var stop = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                live.Logger.LogInformation("payload-{I}-payload-{Again}", i, i);
                i++;
                if (i % 50 == 0)
                    Thread.Sleep(1);
            }
        });

        var wellFormed = new Regex(@"^\[\d\d:\d\d:\d\d INF\] \[T\] payload-(\d+)-payload-\1$");
        for (var i = 0; i < 100; i++)
        {
            live.Viewer.SetFilter(i % 2 == 0 ? LogLevel.Debug : LogLevel.Information);
            Thread.Sleep(5);
            foreach (var line in live.DocumentLines())
                Assert.IsTrue(wellFormed.IsMatch(line), $"Corrupted line on round {i}: {line}");
        }

        stop.Cancel();
        producer.Wait();
    }

    [TestMethod]
    public void ALink_OpensOnce_OnADoubleClick()
    {
        var linkifier = new RecordingLinkifier();
        using var live = new LiveViewer(o => o.WithLinkify(linkifier));
        live.Logger.LogInformation("open LINK1 now");
        live.WaitForLine("LINK1");

        live.DoubleClickOn("LINK1");
        Thread.Sleep(100);

        Assert.AreEqual(1, linkifier.Clicked.Count);
    }

    [TestMethod]
    public void ADragThatSelectsText_DoesNotOpenALink()
    {
        var linkifier = new RecordingLinkifier();
        using var live = new LiveViewer(o => o.WithLinkify(linkifier));
        live.Logger.LogInformation("select from here to LINK2 now");
        live.WaitForLine("LINK2");

        live.DragFromTo("select", "LINK2");
        Thread.Sleep(100);

        Assert.IsEmpty(linkifier.Clicked);
    }

    [TestMethod]
    public void AUrl_IsALink_WithNoLinkifierRegistered()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("see https://example.com/page now");
        live.WaitForLine("example.com");

        Assert.AreEqual(Cursors.Hand, live.CursorOver("example.com"));
    }

    [TestMethod]
    public void ALineAfterAQuietMoment_AppearsWithoutWaitingForTheFlushInterval()
    {
        using var live = new LiveViewer(o => o.Channel(flushMs: 1000));
        Thread.Sleep(1200); // One quiet interval.

        var sw = Stopwatch.StartNew();
        live.Logger.LogInformation("right-away");
        live.WaitForLine("right-away", 900);

        Assert.IsLessThan(500, sw.ElapsedMilliseconds);
    }

    [TestMethod]
    public void ABacklogLargerThanTheDocument_KeepsTheNewestLines()
    {
        const int maxLines = 500;
        using var live = new LiveViewer(o => o.Display(maxLines: maxLines).Channel(flushMs: 200));
        for (var i = 0; i < 20_000; i++)
            live.Logger.LogInformation("line {I} end", i);

        live.WaitForLine("line 19999 end", 30_000);
        live.WaitForIdle();

        var lines = live.DocumentLines();
        Assert.HasCount(maxLines, lines);
        StringAssert.Contains(lines[0], $"line {20_000 - maxLines} end");
        Assert.IsLessThan(20_000, live.Viewer.RenderedMessages, "Lines that could never be seen were rendered anyway.");

        // What was skipped could never have been in the history either, so a replay gives the same lines.
        live.Viewer.SetFilter(LogLevel.Debug);
        live.WaitForIdle();
        StringAssert.Contains(live.DocumentLines()[^1], "line 19999 end");
    }

    [TestMethod]
    public void ABacklogLargerThanTheDocument_LeavesAFullHistory()
    {
        const int maxLines = 3000;
        const int total = 20_000;
        using var live = new LiveViewer(o => o.Display(maxLines: maxLines).History(maxEntries: maxLines).Channel(flushMs: 300));
        for (var i = 0; i < total; i++)
            live.Logger.LogInformation("line {I} end", i);
        live.WaitForLine($"line {total - 1} end", 30_000);
        live.WaitForIdle();

        Assert.AreEqual(maxLines, live.Viewer.HistoryEntries);

        live.Viewer.SetFilter(LogLevel.Debug);
        live.WaitForIdle();

        var lines = live.DocumentLines();
        Assert.HasCount(maxLines, lines);
        StringAssert.Contains(lines[0], $"line {total - maxLines} end");
    }

    [TestMethod]
    public void ABacklogHiddenByAFilter_DoesNotCostVisibleLines()
    {
        using var live = new LiveViewer(o => o.Display(maxLines: 100).Channel(flushMs: 200));
        live.Viewer.SetFilter(LogLevel.Warning);

        live.Logger.LogWarning("visible-early");
        for (var i = 0; i < 5000; i++)
            live.Logger.LogInformation("hidden {I}", i);
        live.Logger.LogWarning("visible-late");

        live.WaitForLine("visible-late", 30_000);
        StringAssert.Contains(live.DocumentText(), "visible-early");
    }

    [TestMethod]
    public void ScopeValues_ReachTheLinkifier_WhenEnabled()
    {
        var linkifier = new ScopeCapturingLinkifier();
        using var live = new LiveViewer(o => o.WithLinkify(linkifier).WithScopes());

        using (live.Logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r-42" }))
            live.Logger.LogInformation("inside scope");
        live.WaitForLine("inside scope");

        Assert.AreEqual("r-42", linkifier.LastRequestId);
    }

    [TestMethod]
    public void ScopeValues_AreNotCaptured_ByDefault()
    {
        var linkifier = new ScopeCapturingLinkifier();
        using var live = new LiveViewer(o => o.WithLinkify(linkifier));

        using (live.Logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r-42" }))
            live.Logger.LogInformation("inside scope");
        live.WaitForLine("inside scope");

        Assert.IsNull(linkifier.LastRequestId);
    }

    [TestMethod]
    public void AutoScroll_PausesWhileTheUserReadsOlderLines_AndResumesAtTheEnd()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 500; i++)
            live.Logger.LogInformation("first {I}", i);
        live.Logger.LogInformation("first-end");
        live.WaitForLine("first-end");
        Assert.IsTrue(live.WaitFor(live.TailVisible, 5000));

        const int wmVScroll = 0x0115;
        const int sbPageUp = 2;
        const int sbBottom = 7;
        live.Ui(() => SendMessage(live.Control.Handle, wmVScroll, sbPageUp, IntPtr.Zero));
        live.Logger.LogInformation("while-reading");
        live.WaitForLine("while-reading");
        Thread.Sleep(100);
        Assert.IsFalse(live.TailVisible(), "New lines pulled the view away from what the user was reading.");

        live.Ui(() => SendMessage(live.Control.Handle, wmVScroll, sbBottom, IntPtr.Zero));
        live.Logger.LogInformation("after-return");
        live.WaitForLine("after-return");
        Assert.IsTrue(live.WaitFor(live.TailVisible, 5000), "Following did not resume at the end of the document.");
    }

    [TestMethod]
    public void Rendering_ContinuesWhileTheControlDoesNotPaint()
    {
        using var live = new LiveViewer();
        live.Ui(() => live.Control.Visible = false);

        for (var i = 0; i < 20_000; i++)
            live.Logger.LogInformation("hidden {I}", i);
        live.Logger.LogInformation("sentinel");

        live.WaitForLine("sentinel", 30_000);
        Assert.AreEqual(20_001, live.LineCount());
    }

    [TestMethod]
    public void Rendering_ContinuesAfterStop_ForBatchesAlreadyPrepared()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 20_000; i++)
            live.Logger.LogInformation("line {I}", i);
        Assert.IsTrue(live.WaitFor(() => live.Viewer.PendingEntryCount < 20_000), "The pump never started reading.");

        live.Viewer.Stop();
        var afterStop = live.Viewer.PendingEntryCount;
        live.WaitForIdle();

        Assert.AreEqual(20_000 - afterStop, live.LineCount());
    }

    [TestMethod]
    public void SetPrettyJson_ReappliesToExistingLines()
    {
        using var live = new LiveViewer();
        live.Viewer.SetPrettyJson(true);
        var value = new JsonPayload("Tiếng Việt");
        live.Logger.ZLogInformation($"got {value}");
        live.WaitForLine("Tiếng Việt\"");
        live.WaitForIdle();

        live.Viewer.SetPrettyJson(false);
        live.WaitForIdle();

        Assert.DoesNotContain("$type", live.DocumentText());
    }

    [TestMethod]
    public void DeferredFormatting_KeepsOrderWithMessagesFormattedOnTheCaller()
    {
        using var live = new LiveViewer();
        var value = new ThreadRecording();
        for (var i = 0; i < 2000; i++)
        {
            if (i % 2 == 0)
                live.Logger.ZLogInformation($"m{i} deferred");
            else
                live.Logger.ZLogInformation($"m{i} {value}");
        }

        live.WaitForLine("m1999");
        live.WaitForIdle();

        var order = live.DocumentLines().Select(line => int.Parse(Regex.Match(line, @"m(\d+) ").Groups[1].Value));
        CollectionAssert.AreEqual(Enumerable.Range(0, 2000).ToArray(), order.ToArray());
    }

    [TestMethod]
    public void AValueThatMayBeThreadBound_IsFormattedOnTheLoggingThread()
    {
        using var live = new LiveViewer();
        var value = new ThreadRecording();

        live.Logger.ZLogInformation($"interpolated {value:F1}");
        live.Logger.LogInformation("template {Value}", value);
        live.WaitForLine("template");

        CollectionAssert.AreEquivalent(new[] { Environment.CurrentManagedThreadId }, value.Threads.Distinct().ToArray());
    }

    [TestMethod]
    public void AMutableValue_PrintsWhatItHeldWhenLogged()
    {
        using var live = new LiveViewer();
        var value = new Mutable { Text = "at-log-time" };

        live.Logger.ZLogInformation($"value {value}");
        value.Text = "changed-later";
        live.WaitForLine("value");

        StringAssert.Contains(live.DocumentText(), "at-log-time");
    }

    [TestMethod]
    public void ACallerOnAnotherCulture_GetsItsOwnNumberFormat()
    {
        using var live = new LiveViewer();
        Assert.IsTrue(live.WaitFor(() => live.Viewer.ScintillaControl.IsHandleCreated));
        Thread.Sleep(150); // The pump has started and recorded its own culture.
        const double value = 1.5;

        var thread = new Thread(() =>
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            live.Logger.ZLogInformation($"interpolated {value:F1}");
            live.Logger.LogInformation("template {Value}", value);
        });
        thread.Start();
        thread.Join();
        live.WaitForLine("template");

#if NETFRAMEWORK
        // ZLogger's interpolation formats numbers with the invariant culture on .NET Framework.
        StringAssert.Contains(live.DocumentText(), "interpolated 1.5");
#else
        StringAssert.Contains(live.DocumentText(), "interpolated 1,5");
#endif
    }

    [TestMethod]
    public void AColorizerThatWaitsForTheUiThread_DoesNotDeadlockClear()
    {
        var colorizer = new UiWaitingColorizer();
        using var live = new LiveViewer(o => o.WithColorized(colorizer));
        colorizer.Target = live.Control;

        using var stop = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
                live.Logger.LogInformation("line {I} with a few tokens to color", i);
        });

        // Each Clear() has to take the pump's lock while the pump is busy with the colorizer.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            live.Ui(() => live.Viewer.Clear(), 5000);
            Thread.Sleep(5);
        }

        stop.Cancel();
        producer.Wait();
        colorizer.Target = null;
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel");
        Assert.IsGreaterThan(0, colorizer.Calls);
    }

    [TestMethod]
    public void AColorizerThatWaitsForTheUiThread_DoesNotDeadlockDispose()
    {
        var colorizer = new UiWaitingColorizer();
        var live = new LiveViewer(o => o.WithColorized(colorizer));
        colorizer.Target = live.Control;

        using var stop = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
                live.Logger.LogInformation("line {I} with a few tokens to color", i);
        });

        try
        {
            Assert.IsTrue(live.WaitFor(() => colorizer.Calls > 100));
        }
        finally
        {
            // Disposes on the UI thread, as closing a window does, while the pump waits for that thread.
            live.Dispose();
            stop.Cancel();
            producer.Wait();
        }

        Assert.IsNull(live.DisposeFailure, $"Dispose did not complete: {live.DisposeFailure}");
    }

    [TestMethod]
    public void DeferredEntries_ReturnToZLogger_WhenDropped()
    {
        using var live = new LiveViewer(o => o.Channel(capacity: 10), start: false);
        for (var i = 0; i < 100; i++)
            live.Logger.ZLogInformation($"line {i}");

        Assert.AreEqual(90, live.Viewer.DroppedMessages);
        Assert.AreEqual(10, live.Viewer.PendingEntryCount);
    }

    [TestMethod]
    public void ColorizerAndLinkifier_SeeTokensAcrossNonAsciiText()
    {
        var linkifier = new RecordingLinkifier();
        var colorizer = new RecordingColorizer();
        using var live = new LiveViewer(o => o.WithLinkify(linkifier).WithColorized(colorizer));

        live.Logger.LogInformation("Tiếng Việt, đầy đủ dấu LINK7 RED8 cuối");
        live.WaitForLine("cuối");

        Assert.Contains("RED8", colorizer.Seen);
        Assert.Contains("Việt", colorizer.Seen);
        live.ClickOn("LINK7");
        Assert.IsTrue(live.WaitFor(() => linkifier.Clicked.Contains("LINK7"), 2000), "The link token was not found.");
    }

    [TestMethod]
    public void AMessageWithAUrl_StillOffersEveryTokenToTheLinkifier()
    {
        var linkifier = new RecordingLinkifier();
        using var live = new LiveViewer(o => o.WithLinkify(linkifier));

        live.Logger.LogInformation("Việt https://example.com/a LINK9 end");
        live.WaitForLine("LINK9");

        live.ClickOn("LINK9");
        Assert.IsTrue(live.WaitFor(() => linkifier.Clicked.Contains("LINK9"), 2000), "The link token was not found.");
    }

    [TestMethod]
    public void MaxLines_KeepsExactlyThatManyMessages()
    {
        using var live = new LiveViewer(o => o.Display(maxLines: 100));
        for (var i = 0; i < 1000; i++)
            live.Logger.LogInformation("line {I}", i);
        live.Logger.LogInformation("last");
        live.WaitForLine("last");
        live.WaitForIdle();

        Assert.AreEqual(100, live.LineCount());
    }

    [TestMethod]
    public void Clear_FromAnotherThread_KeepsLinesLoggedAfterIt()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("before-clear");
        live.WaitForLine("before-clear");

        live.Viewer.Clear();
        live.Logger.LogInformation("after-clear");

        live.WaitForLine("after-clear");
        live.WaitForIdle();
        Assert.DoesNotContain("before-clear", live.DocumentText());
    }

    [TestMethod]
    public async Task CountMatches_WithNonAsciiText_MatchesTheDocument()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 2000; i++)
            live.Logger.LogInformation("Tiếng Việt needle {I}", i);
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel");
        live.WaitForIdle();

        live.Viewer.HighlightSearch("needle");

        Assert.AreEqual(2000, await live.Viewer.CountMatchesAsync());
        Assert.AreEqual(1, live.Viewer.FindNext().Current);
    }

    [TestMethod]
    public async Task CountMatches_CanBeCancelled()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("needle");
        live.WaitForLine("needle");
        live.Viewer.HighlightSearch("needle");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => live.Viewer.CountMatchesAsync(cancelled.Token));
    }

    [TestMethod]
    public async Task InvalidRegex_FindsNothing_WithoutThrowing()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel");

        live.Viewer.HighlightSearch("(unclosed", useRegex: true);

        Assert.AreEqual(0, await live.Viewer.CountMatchesAsync());
        Assert.AreEqual(0, live.Viewer.FindNext().Current);
    }

    [TestMethod]
    public void TotalBacklog_IsBoundedWhileTheUiIsBlocked()
    {
        const int capacity = 1000;
        const int posted = 200_000;
        using var live = new LiveViewer(o => o.Channel(capacity: capacity, flushMs: 5, maxBatch: 100));
        var blocked = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        _ = live.Ui(() => live.Control.BeginInvoke(() =>
        {
            blocked.Set();
            release.Wait();
        }));
        blocked.Wait();

        for (var i = 0; i < posted; i++)
            live.Viewer.Pump.TryPost(TestHelper.MakeEntry($"line {i}", LogLevel.Information));
        Thread.Sleep(200);

        var queued = live.Viewer.RenderQueue.Count;
        var pending = live.Viewer.PendingEntryCount;
        release.Set();

        Assert.IsLessThanOrEqualTo(32, queued, $"{queued} batches queued behind a blocked UI thread.");
        Assert.IsLessThanOrEqualTo(capacity, pending);
        Assert.IsGreaterThan(0, live.Viewer.DroppedMessages);
    }

    [TestMethod]
    public void FilterReplay_KeepsTheUiResponsive()
    {
        const int total = 50_000;
        using var live = new LiveViewer();
        for (var i = 0; i < total; i++)
            live.Logger.LogInformation("Request {I} processed in {Ms}ms from {Ip}", i, i % 1500, "10.0.0.1");
        live.Logger.LogInformation("sentinel");
        live.WaitForLine("sentinel", 30_000);
        live.WaitForIdle();
        live.Viewer.SetFilter(LogLevel.Error);
        Assert.IsTrue(live.WaitFor(() => live.LineCount() == 0), "Filtering everything out did not empty the document.");

        var maxStall = 0.0;
        using var done = new ManualResetEventSlim();
        var probe = Task.Run(() =>
        {
            while (!done.IsSet)
            {
                var sw = Stopwatch.StartNew();
                live.Ui(() => { });
                maxStall = Math.Max(maxStall, sw.Elapsed.TotalMilliseconds);
                Thread.Sleep(2);
            }
        });

        var replay = Stopwatch.StartNew();
        live.Viewer.SetFilter(LogLevel.Debug);
        Assert.IsTrue(live.WaitFor(() => live.LineCount() >= total - 1), "The replay never finished.");
        replay.Stop();
        done.Set();
        probe.Wait();

        Console.WriteLine($"Replay took {replay.ElapsedMilliseconds} ms, worst UI stall {maxStall:F1} ms.");
        Assert.IsLessThan(100, maxStall, $"Replay took {replay.ElapsedMilliseconds} ms with a UI stall of {maxStall:F1} ms.");
    }

    /// <summary>
    /// A host such as Revit can destroy the window holding the editor without WinForms. ScintillaNET then parks
    /// the editor under a message-only window, where its line index misses every edit, and the first edit
    /// after the editor gets a window again threw IndexOutOfRangeException from LineCollection.TrackInsertText.
    /// </summary>
    [TestMethod]
    public void EditorParkedByTheHost_HoldsLogsAndShowsThemOnceBack()
    {
        using var live = new LiveViewer();
        var uiExceptions = new List<Exception>();
        var panel = live.Ui(() =>
        {
            Application.ThreadException += (_, e) => uiExceptions.Add(e.Exception);
            var holder = new Panel { Dock = DockStyle.Fill };
            live.Control.FindForm()!.Controls.Add(holder);
            holder.Controls.Add(live.Control);
            return holder;
        });
        live.Logger.LogInformation("before");
        live.WaitForLine("before");

        live.Ui(() => DestroyWindow(panel.Handle));
        for (var i = 0; i < 100; i++)
            live.Logger.LogInformation("parked {I}", i);

        Assert.IsTrue(
            live.WaitFor(() => live.Viewer is { PendingEntryCount: 0, RenderQueue.IsEditorParked: true }),
            "The viewer never held the logs back while the editor was parked.");
        Assert.AreEqual(1, live.LineCount(), "The document changed while the editor was parked.");

        live.Ui(() => SetParent(live.Control.Handle, panel.Handle));
        for (var i = 0; i < 100; i++)
            live.Logger.LogInformation("back {I}", i);
        live.WaitForLine("back 99");

        Assert.AreEqual(201, live.LineCount());
        Assert.AreEqual(live.LineCount() + 1, live.Ui(() => live.Control.Lines.Count), "ScintillaNET's line index is out of step.");
        Assert.IsEmpty(uiExceptions, string.Join(Environment.NewLine, uiExceptions));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    private sealed class ThreadRecording
    {
        public System.Collections.Concurrent.ConcurrentBag<int> Threads { get; } = [];

        public override string ToString()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return "recorded";
        }
    }

    private sealed class Mutable
    {
        public string Text { get; set; } = "";
        public override string ToString() => Text;
    }

    /// <summary>Waits for the UI thread on every token, as a colorizer that reads UI state would.</summary>
    private sealed class UiWaitingColorizer : ILogColorizer
    {
        private int _calls;

        public volatile Control? Target;

        public int Calls => Volatile.Read(ref _calls);

        public bool TryGetColor(ReadOnlySpan<byte> utf8Token, out Color color)
        {
            color = default;
            var target = Target;
            if (target is null || !target.InvokeRequired)
                return false;

            target.Invoke(() => { });
            Interlocked.Increment(ref _calls);
            return false;
        }
    }

    private sealed class RecordingColorizer : ILogColorizer
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = [];

        public bool TryGetColor(ReadOnlySpan<byte> utf8Token, out Color color)
        {
            var token = Encoding.UTF8.GetString(utf8Token);
            Seen.Add(token);
            color = Color.Red;
            return token.StartsWith("RED", StringComparison.Ordinal);
        }
    }

    // Read by the JSON serializer.
    // ReSharper disable once NotAccessedPositionalProperty.Local
    private sealed record JsonPayload(string Name);

    private sealed class ThrowsOnToString
    {
        public override string ToString() => throw new InvalidOperationException("no text for you");
    }

    private sealed class ScopeCapturingLinkifier : ILinkifier
    {
        public volatile string? LastRequestId;

        public bool TryMatch(ReadOnlySpan<byte> utf8Token, RenderContext context, out Action? onClick)
        {
            onClick = null;
            if (context.Properties.TryGetValue("RequestId", out var value))
                LastRequestId = value as string;
            return false;
        }
    }

    private sealed class RecordingLinkifier : ILinkifier
    {
        public List<string> Clicked { get; } = [];

        public bool TryMatch(ReadOnlySpan<byte> utf8Token, RenderContext context, out Action? onClick)
        {
            var token = Encoding.UTF8.GetString(utf8Token);
            if (!token.StartsWith("LINK", StringComparison.Ordinal))
            {
                onClick = null;
                return false;
            }

            onClick = () => Clicked.Add(token);
            return true;
        }
    }

    private sealed class ThrowingProperties : IReadOnlyDictionary<string, object?>
    {
        public object this[string key] => throw new InvalidOperationException("poison");
        public IEnumerable<string> Keys => throw new InvalidOperationException("poison");
        public IEnumerable<object?> Values => throw new InvalidOperationException("poison");
        public int Count => throw new InvalidOperationException("poison");
        public bool ContainsKey(string key) => throw new InvalidOperationException("poison");
        public bool TryGetValue(string key, out object? value) => throw new InvalidOperationException("poison");
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("poison");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new InvalidOperationException("poison");
    }
}
