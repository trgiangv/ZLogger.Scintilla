using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// The WPF host pumps Win32 messages through its Dispatcher, not through Application.Run,
/// so rendering is checked once inside a real WPF window.
/// </summary>
[TestClass]
public sealed class WpfHostTests
{
    [TestMethod]
    public void WpfViewer_RendersFiltersAndSearchesInsideARealWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunInsideWindow();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The WPF window never finished.");
        Assert.IsNull(failure, failure?.ToString());
    }

    private static void RunInsideWindow()
    {
        using var services = new ServiceCollection()
            .AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerScintillaWpf();
            })
            .BuildServiceProvider();
        var viewer = services.GetRequiredService<ScintillaLogViewerWpf>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("T");

        var window = new Window
        {
            Width = 800,
            Height = 400,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = viewer.HostElement
        };
        Exception? failure = null;
        var matchCount = -1;
        var firstMatch = -1;

        window.ContentRendered += async (_, _) =>
        {
            try
            {
                viewer.Start();
                for (var i = 0; i < 5000; i++)
                {
                    if (i % 2 == 0)
                        logger.LogInformation("Tiếng Việt info {I}", i);
                    else
                        logger.LogWarning("Tiếng Việt warning {I}", i);
                }

                logger.LogWarning("sentinel");
                await WaitFor(() => viewer.ScintillaControl.Lines.Count == 5002);

                viewer.SetFilter(LogLevel.Warning);
                await WaitFor(() => viewer.ScintillaControl.Lines.Count == 2502);

                viewer.HighlightSearch("warning");
                matchCount = await viewer.CountMatchesAsync();
                firstMatch = (await viewer.FindNextAsync()).Current;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                viewer.Dispose();
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        };

        window.Show();
        Dispatcher.Run();
        if (failure is not null)
            throw new InvalidOperationException("The viewer failed inside the WPF window.", failure);

        Assert.AreEqual(2500, matchCount);
        Assert.AreEqual(1, firstMatch);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The document did not reach the expected state.");
            await Task.Delay(10);
        }
    }
}
