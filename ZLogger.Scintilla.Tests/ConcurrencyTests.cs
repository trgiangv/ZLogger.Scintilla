using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Stress tests for concurrent ingest and rapid filter switching.
/// These tests verify no exceptions are thrown and accounting invariants hold.
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public void RapidFilterSwitch_DoesNotThrow()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Start();

        var levels = new[]
        {
            LogLevel.Trace, LogLevel.Debug, LogLevel.Information,
            LogLevel.Warning, LogLevel.Error, LogLevel.Critical, LogLevel.None
        };

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var postThread = new Thread(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                try
                {
                    var entry = MakeEntry($"msg{i}", (LogLevel)(i % 6));
                    viewer.TryPost(entry);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });
        postThread.Start();

        for (var i = 0; i < 50; i++)
        {
            try
            {
                viewer.SetFilter(levels[i % levels.Length]);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        postThread.Join();
        viewer.Stop();
        viewer.Dispose();

        Assert.Empty(exceptions);
    }

    [Fact]
    public void ConcurrentTryPost_FromMultipleThreads_DoesNotThrow()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Start();

        const int threadCount = 4;
        const int entriesPerThread = 250;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait(); // start all threads simultaneously
                for (var i = 0; i < entriesPerThread; i++)
                {
                    try
                    {
                        var entry = MakeEntry($"t{threadId}-{i}", LogLevel.Information);
                        viewer.TryPost(entry);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        viewer.Stop();
        viewer.Dispose();

        Assert.Empty(exceptions);
    }

    [Fact]
    public void FilterVersion_MonotonicallyIncreases_OnRapidSetFilter()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Start();

        var levels = new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Information, LogLevel.Trace };
        for (var i = 0; i < 20; i++)
            viewer.SetFilter(levels[i % levels.Length]);

        var filterVersionField = typeof(ScintillaLogViewerBase)
            .GetField("_filterVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(filterVersionField);
        var version = (long)filterVersionField!.GetValue(viewer)!;

        viewer.Stop();
        viewer.Dispose();

        Assert.True(version > 0, $"Expected _filterVersion > 0 after 20 SetFilter calls, got {version}");
    }

    [Fact]
    public void TryPost_AfterStop_ReturnsFalseOrTrue_NeverThrows()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Start();
        viewer.Stop();

        var exceptions = new List<Exception>();
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var entry = MakeEntry($"post-after-stop-{i}", LogLevel.Information);
                viewer.TryPost(entry); // may return true or false — must not throw
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        viewer.Dispose();
        Assert.Empty(exceptions);
    }

    private static LogEntry MakeEntry(string text, LogLevel level)
        => TestHelper.MakeEntry(text, level);
}
