using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ZLogger.Scintilla.Tests;

[TestClass]
public sealed class FindBarTests
{
    private const int WmKeyDown = 0x0100;
    private const int WmSetCursor = 0x0020;
    private const int WmMouseMove = 0x0200;
    private const int HtClient = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, nint lParam);

    [TestMethod]
    public async Task FindInSelection_LimitsCountAndNavigationToTheRange()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 100; i++)
            live.Logger.LogInformation("needle {I}", i);
        live.WaitForLine("needle 99");
        live.WaitForIdle();

        live.Ui(() =>
        {
            var start = live.Control.DirectMessage(LogConstants.SciPositionFromLine, (IntPtr)10);
            var end = live.Control.DirectMessage(LogConstants.SciPositionFromLine, (IntPtr)20);
            live.Control.DirectMessage(LogConstants.SciSetSel, start, end);
            live.Viewer.Search.HighlightSearch("needle");
            Assert.IsTrue(live.Viewer.Search.SetScope(true));
        });

        Assert.AreEqual(10, await live.Viewer.CountMatchesAsync());
        var ranks = Enumerable.Range(0, 11).Select(_ => live.Viewer.FindNext().Current).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 1 }, ranks);

        // New lines arrive after the range and stay out of it.
        for (var i = 100; i < 150; i++)
            live.Logger.LogInformation("needle {I}", i);
        live.WaitForLine("needle 149");
        Assert.AreEqual(10, await live.Viewer.CountMatchesAsync());

        live.Ui(() => live.Viewer.Search.SetScope(false));
        Assert.AreEqual(150, await live.Viewer.CountMatchesAsync());
    }

    [TestMethod]
    public async Task FindInSelection_FollowsItsTextWhenTheHeadIsTrimmed()
    {
        using var live = new LiveViewer(o => o.Display(maxLines: 300));
        for (var i = 0; i < 200; i++)
            live.Logger.LogInformation("needle {I}", i);
        live.WaitForLine("needle 199");
        live.WaitForIdle();

        // Lines 150..159 hold needle 150..159.
        live.Ui(() =>
        {
            var start = live.Control.DirectMessage(LogConstants.SciPositionFromLine, (IntPtr)150);
            var end = live.Control.DirectMessage(LogConstants.SciPositionFromLine, (IntPtr)160);
            live.Control.DirectMessage(LogConstants.SciSetSel, start, end);
            live.Viewer.Search.HighlightSearch("needle 15");
            live.Viewer.Search.SetScope(true);
        });
        Assert.AreEqual(10, await live.Viewer.CountMatchesAsync());

        for (var i = 200; i < 400; i++)
            live.Logger.LogInformation("other {I}", i);
        live.WaitForLine("other 399");
        live.WaitForIdle();

        Assert.AreEqual(10, await live.Viewer.CountMatchesAsync());
        live.Ui(() => live.Control.DirectMessage(LogConstants.SciSetSel, IntPtr.Zero, IntPtr.Zero));
        live.Viewer.FindNext();
        var line = live.Ui(() => live.Control.Lines[live.Control.CurrentLine].Text);
        StringAssert.Contains(line, "needle 150");
    }

    [TestMethod]
    public async Task SearchOptions_MatchCaseWholeWordAndRegex()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("Needle needle needles");
        live.Logger.LogInformation("needle2 NEEDLE");
        live.WaitForLine("NEEDLE");

        async Task<int> Count(string pattern, SearchOptions options)
        {
            live.Ui(() => live.Viewer.Search.HighlightSearch(pattern, options));
            return await live.Viewer.CountMatchesAsync();
        }

        Assert.AreEqual(5, await Count("needle", SearchOptions.None));
        Assert.AreEqual(3, await Count("needle", SearchOptions.MatchCase));
        Assert.AreEqual(3, await Count("needle", SearchOptions.WholeWord));
        Assert.AreEqual(1, await Count("needle", SearchOptions.WholeWord | SearchOptions.MatchCase));
        Assert.AreEqual(3, await Count("needles?", SearchOptions.Regex | SearchOptions.MatchCase));
        // Whole word around a regular expression: "needle2" no longer counts.
        Assert.AreEqual(2, await Count("needles?", SearchOptions.Regex | SearchOptions.MatchCase | SearchOptions.WholeWord));
    }

    [TestMethod]
    public void FindInSelection_NeedsARangeTheUserSelected()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("needle one");
        live.Logger.LogInformation("needle two");
        live.WaitForLine("needle two");

        live.Ui(() => live.Control.DirectMessage(LogConstants.SciSetSel, IntPtr.Zero, IntPtr.Zero));
        Assert.IsTrue(live.Ui(() => PressKey(live.Control, Keys.F3)));
        Assert.IsFalse(live.Ui(() => live.Viewer.FindBar.CanFindInSelection), "Enabled with nothing selected.");
        Assert.IsFalse(live.Ui(() => live.Viewer.Search.SetScope(true)), "A scope was set without a selection.");

        live.Ui(() =>
        {
            var end = live.Control.DirectMessage(LogConstants.SciGetLength);
            live.Control.DirectMessage(LogConstants.SciSetSel, IntPtr.Zero, end);
        });
        Assert.IsTrue(live.WaitFor(() => live.Viewer.FindBar.CanFindInSelection), "Still disabled with a range selected.");
    }

    /// <summary>A Revit dockable pane can be as narrow as 550px; the bar must fit and leave most of the log visible.</summary>
    [TestMethod]
    public async Task FindBar_FitsANarrowPane()
    {
        using var live = new LiveViewer(width: 550);
        for (var i = 0; i < 12_000; i++)
            live.Logger.LogInformation("needle {I}", i);
        live.WaitForLine("needle 11999");
        live.WaitForIdle();
        var bar = live.Viewer.FindBar;

        live.Ui(() => PressKey(live.Control, Keys.F3));
        var (empty, client) = live.Ui(() => (bar.Bounds, live.Control.ClientSize));
        Assert.IsGreaterThanOrEqualTo(0, empty.Left, "The bar starts outside the pane.");
        Assert.IsLessThanOrEqualTo(client.Width, empty.Right, "The bar runs past the pane.");
        Assert.IsLessThanOrEqualTo(client.Width * 2 / 3, empty.Width, $"The bar takes {empty.Width} of {client.Width}px.");

        // A five-digit count widens the bar to the left; the buttons on the right stay put.
        live.Ui(() => bar.Controls.OfType<TextBox>().Single().Text = "needle");
        Assert.IsTrue(live.WaitFor(() => bar.Bounds.Width > empty.Width), "The bar did not make room for the count.");
        await Task.Delay(200);
        var counted = live.Ui(() => bar.Bounds);
        Assert.AreEqual(empty.Right, counted.Right, "The bar's right edge moved.");
        Assert.IsGreaterThanOrEqualTo(0, counted.Left, "The bar starts outside the pane.");
    }

    /// <summary>A thread has one caret. Selecting a match must not move it out of the text box being typed in.</summary>
    [TestMethod]
    public void FindBar_KeepsTheCaretWhereTheUserTypes()
    {
        using var live = new LiveViewer();
        for (var i = 0; i < 50; i++)
            live.Logger.LogInformation("some long prefix text to push the match right needle {I}", i);
        live.WaitForLine("needle 49");
        var bar = live.Viewer.FindBar;

        var (typed, searched) = live.Ui(() =>
        {
            PressKey(live.Control, Keys.F3);
            var input = bar.Controls.OfType<TextBox>().Single();
            input.Text = "need";
            input.SelectionStart = input.TextLength;
            var caretAfterTyping = CaretX();
            PressKey(input, Keys.Enter);
            return (caretAfterTyping, CaretX());
        });

        Assert.IsGreaterThan(0, typed, "The text box has no caret after typing.");
        Assert.AreEqual(typed, searched, "Finding a match moved the text box's caret.");
    }

    private static int CaretX()
    {
        var info = new GuiThreadInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        GetGUIThreadInfo(GetCurrentThreadId(), ref info);
        return info.rcCaret.Left;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Rectangle rcCaret;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>The editor must not choose the cursor over the bar: it would show its margin's arrow there.</summary>
    [TestMethod]
    public void FindBar_ChoosesItsOwnCursor()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("needle");
        live.WaitForLine("needle");
        var bar = live.Viewer.FindBar;
        live.Ui(() => PressKey(live.Control, Keys.F3));

        var (overInput, overBar) = live.Ui(() =>
        {
            var input = bar.Controls.OfType<TextBox>().Single();
            SendMessage(input.Handle, WmSetCursor, input.Handle, (WmMouseMove << 16) | HtClient);
            var cursorOverInput = Cursor.Current;
            SendMessage(bar.Handle, WmSetCursor, bar.Handle, (WmMouseMove << 16) | HtClient);
            return (cursorOverInput, Cursor.Current);
        });

        Assert.AreEqual(Cursors.IBeam, overInput);
        Assert.AreEqual(Cursors.Default, overBar);
    }

    [TestMethod]
    public void FindBar_OpensAndClosesFromKeysInsideTheEditor()
    {
        using var live = new LiveViewer();
        live.Logger.LogInformation("needle");
        live.WaitForLine("needle");

        Assert.IsTrue(live.Ui(() => PressKey(live.Control, Keys.F3)));
        Assert.IsTrue(live.Ui(() => live.Viewer.FindBar.Visible));
        Assert.IsTrue(live.Ui(() => PressKey(live.Control, Keys.Escape)));
        Assert.IsFalse(live.Ui(() => live.Viewer.FindBar.Visible));
    }

    /// <summary>
    /// In WPF the keys reach the editor through the host's keyboard sink, not through a WinForms message loop.
    /// </summary>
    [TestMethod]
    public void FindBar_OpensFromKeysInsideTheWpfHost()
    {
        Exception? failure = null;
        var opened = false;
        var closed = false;
        var thread = new Thread(() =>
        {
            try
            {
                (opened, closed) = RunInsideWpfWindow();
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
        Assert.IsTrue(opened, "F3 did not open the find bar.");
        Assert.IsTrue(closed, "Escape did not close the find bar.");
    }

    private static (bool Opened, bool Closed) RunInsideWpfWindow()
    {
        using var services = new ServiceCollection()
            .AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerScintillaWpf();
            })
            .BuildServiceProvider();
        var viewer = services.GetRequiredService<ScintillaLogViewerWpf>();
        var host = (WindowsFormsHost)viewer.HostElement;
        var window = new Window
        {
            Width = 800,
            Height = 400,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = host
        };
        var opened = false;
        var closed = false;

        window.ContentRendered += (_, _) =>
        {
            try
            {
                viewer.ScintillaControl.Focus();
                opened = SendThroughDispatcher(viewer.ScintillaControl, Keys.F3) && viewer.FindBar.Visible;
                closed = SendThroughDispatcher(viewer.ScintillaControl, Keys.Escape) && !viewer.FindBar.Visible;
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
        return (opened, closed);
    }

    /// <summary>
    /// The way WPF's message loop hands a key to WinForms: WindowsFormsHost listens to the thread's message
    /// filter and preprocesses keys for the WinForms control that owns the window.
    /// </summary>
    private static bool SendThroughDispatcher(System.Windows.Forms.Control target, Keys key)
    {
        var msg = new MSG { hwnd = target.Handle, message = WmKeyDown, wParam = (IntPtr)key };
        return ComponentDispatcher.RaiseThreadMessage(ref msg);
    }

    private static bool PressKey(System.Windows.Forms.Control target, Keys key)
    {
        var msg = Message.Create(target.Handle, WmKeyDown, (IntPtr)key, IntPtr.Zero);
        return target.PreProcessMessage(ref msg);
    }
}
