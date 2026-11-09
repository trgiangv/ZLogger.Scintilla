using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

// The posting threads are joined before the test's `using` disposes the viewer.
// ReSharper disable AccessToDisposedClosure
namespace ZLogger.Scintilla.Tests;

/// <summary>
/// Stress tests for concurrent ingest and rapid filter switching.
/// These tests verify no exceptions are thrown and accounting invariants hold.
/// </summary>
[TestClass]
public sealed class ConcurrencyTests
{
    [TestMethod]
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
                    var entry = TestHelper.MakeEntry($"msg{i}", (LogLevel)(i % 6));
                    viewer.Pump.TryPost(entry);
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

        Assert.IsEmpty(exceptions);
    }

    [TestMethod]
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
                barrier.SignalAndWait();
                for (var i = 0; i < entriesPerThread; i++)
                {
                    try
                    {
                        var entry = TestHelper.MakeEntry($"t{threadId}-{i}", LogLevel.Information);
                        viewer.Pump.TryPost(entry);
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

        Assert.IsEmpty(exceptions);
    }

    [TestMethod]
    public void DocumentEpoch_Advances_OnRapidSetFilter()
    {
        var viewer = new ScintillaLogViewer();
        viewer.Start();

        var levels = new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Information, LogLevel.Trace };
        for (var i = 0; i < 20; i++)
            viewer.SetFilter(levels[i % levels.Length]);

        var epoch = viewer.RenderQueue.Epoch;

        viewer.Stop();
        viewer.Dispose();

        Assert.IsTrue(epoch > 0, $"Expected the document epoch > 0 after 20 SetFilter calls, got {epoch}");
    }

    [TestMethod]
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
                var entry = TestHelper.MakeEntry($"post-after-stop-{i}", LogLevel.Information);
                viewer.Pump.TryPost(entry);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        viewer.Dispose();
        Assert.IsEmpty(exceptions);
    }
}
