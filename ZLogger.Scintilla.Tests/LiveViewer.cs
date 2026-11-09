using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Tests;

/// <summary>
/// A real viewer on a visible window, with <see cref="Application.Run(Form)"/> pumping messages on its own
/// STA thread. The test thread stays free, so a hung UI thread fails the test instead of hanging the run.
/// </summary>
internal sealed class LiveViewer : IDisposable
{
    private const int HangTimeoutMs = 20_000;
    private const int SciGetLength = 2006;
    private const int SciGetLineCount = 2154;
    private const int SciPointXFromPosition = 2164;
    private const int SciPointYFromPosition = 2165;
    private const int SciIndicatorValueAt = 2507;
    private const int SciIndicatorEnd = 2509;
    private const int SearchIndicatorId = 30;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private ServiceProvider? _services;
    private Form _form = null!;
    private Exception? _startFailure;

    public ScintillaLogViewer Viewer { get; private set; } = null!;
    public ILogger Logger { get; private set; } = null!;
    public Exception? DisposeFailure { get; private set; }
    public ScintillaNET.Scintilla Control => Viewer.ScintillaControl;

    public LiveViewer(Action<ScintillaOptions>? configure = null, bool start = true, int width = 1000, int height = 500)
    {
        _thread = new Thread(() =>
        {
            try
            {
                _services = new ServiceCollection()
                    .AddLogging(logging =>
                    {
                        logging.SetMinimumLevel(LogLevel.Trace);
                        logging.AddZLoggerScintillaWinForms(configure);
                    })
                    .BuildServiceProvider();
                Viewer = _services.GetRequiredService<ScintillaLogViewer>();
                Logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("T");
                _form = new TestForm { ClientSize = new Size(width, height) };
                _form.Controls.Add(Viewer.ScintillaControl);
                _form.Shown += (_, _) =>
                {
                    if (start)
                        Viewer.Start();
                    _ready.Set();
                };
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                _startFailure = ex;
                _ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "LiveViewer UI"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(HangTimeoutMs))
            throw new TimeoutException("The window did not appear.");
        if (_startFailure is not null)
            throw new InvalidOperationException("The viewer failed to start.", _startFailure);
    }

    /// <summary>Runs on the UI thread. Throws if the UI thread does not respond, which is how a hang fails a test.</summary>
    public T Ui<T>(Func<T> action, int timeoutMs = HangTimeoutMs)
    {
        if (!_form.InvokeRequired)
            return action();

        var result = _form.BeginInvoke(action);
        if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
            throw new TimeoutException($"The UI thread did not respond within {timeoutMs} ms.");
        return (T)_form.EndInvoke(result)!;
    }

    public void Ui(Action action, int timeoutMs = HangTimeoutMs) => Ui<object?>(() =>
    {
        action();
        return null;
    }, timeoutMs);

    /// <summary>Lets the message loop run while polling a condition on the UI thread.</summary>
    public bool WaitFor(Func<bool> condition, int timeoutMs = HangTimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Ui(condition))
                return true;
            Thread.Sleep(10);
        }

        return false;
    }

    public void WaitForLine(string text, int timeoutMs = HangTimeoutMs)
    {
        if (!WaitFor(() => Control.Text.Contains(text, StringComparison.Ordinal), timeoutMs))
            Assert.Fail($"'{text}' did not reach the document within {timeoutMs} ms. Lines: {LineCount()}");
    }

    /// <summary>Waits for the document to stop changing, which is what a settled replay or drain looks like.</summary>
    public void WaitForIdle(int quietMs = 400, int timeoutMs = HangTimeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var last = (-1L, -1L);
        var quietSince = sw.ElapsedMilliseconds;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var current = (Ui(() => (long)Control.DirectMessage(SciGetLength)), Viewer.RenderedMessages + Viewer.PendingEntryCount);
            if (current != last)
            {
                last = current;
                quietSince = sw.ElapsedMilliseconds;
            }
            else if (sw.ElapsedMilliseconds - quietSince >= quietMs)
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail($"The document did not settle within {timeoutMs} ms.");
    }

    public string DocumentText() => Ui(() => Control.Text);

    public string[] DocumentLines() =>
        DocumentText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lines holding text. Scintilla counts the empty line after the final line break too.</summary>
    public int LineCount() => Ui(() => (int)Control.DirectMessage(SciGetLineCount) - 1);

    /// <summary>True when the last character of the last message is inside the client area.</summary>
    public bool TailVisible() => Ui(() =>
    {
        var length = (int)Control.DirectMessage(SciGetLength);
        if (length < 3)
            return false;
        var y = (int)Sci(SciPointYFromPosition, 0, length - 3);
        return y >= 0 && y < Control.ClientSize.Height;
    });

    /// <summary>Number of separate search highlights in the document.</summary>
    public int HighlightCount() => Ui(() =>
    {
        var length = (int)Control.DirectMessage(SciGetLength);
        var count = 0;
        var position = 0;
        while (position < length)
        {
            var end = (int)Sci(SciIndicatorEnd, SearchIndicatorId, position);
            if (Sci(SciIndicatorValueAt, SearchIndicatorId, position) != IntPtr.Zero)
                count++;
            if (end <= position)
                break;
            position = end;
        }

        return count;
    });

    /// <summary>Sends a real left click at the last occurrence of <paramref name="text"/>.</summary>
    public void ClickOn(string text) => Ui(() =>
    {
        var position = PointAt(text);
        SendMessage(Control.Handle, WmLButtonDown, 1, position);
        SendMessage(Control.Handle, WmLButtonUp, 0, position);
    });

    public void DoubleClickOn(string text) => Ui(() =>
    {
        var position = PointAt(text);
        SendMessage(Control.Handle, WmLButtonDown, 1, position);
        SendMessage(Control.Handle, WmLButtonUp, 0, position);
        SendMessage(Control.Handle, WmLButtonDblClk, 1, position);
        SendMessage(Control.Handle, WmLButtonUp, 0, position);
    });

    /// <summary>Presses at one text, moves to another with the button down, and releases there.</summary>
    public void DragFromTo(string from, string to) => Ui(() =>
    {
        var start = PointAt(from);
        var end = PointAt(to);
        SendMessage(Control.Handle, WmLButtonDown, 1, start);
        SendMessage(Control.Handle, WmMouseMove, 1, end);
        SendMessage(Control.Handle, WmLButtonUp, 0, end);
    });

    /// <summary>
    /// The cursor shown over <paramref name="text"/>. Read in the same UI call, because the real pointer is
    /// somewhere else, and Windows soon reports it leaving the control, which resets the cursor.
    /// </summary>
    public Cursor CursorOver(string text) => Ui(() =>
    {
        SendMessage(Control.Handle, WmMouseMove, 0, PointAt(text));
        return Control.Cursor;
    });

    // nint, so that int arguments convert implicitly on net48 too.
    private IntPtr Sci(int message, nint wParam, nint lParam) => Control.DirectMessage(message, wParam, lParam);

    private nint PointAt(string text)
    {
        var document = Control.Text;
        var index = document.LastIndexOf(text, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, index, $"'{text}' is not in the document.");
        var bytePosition = System.Text.Encoding.UTF8.GetByteCount(document.AsSpan(0, index)) + 1;
        var x = (int)Sci(SciPointXFromPosition, 0, bytePosition);
        var y = (int)Sci(SciPointYFromPosition, 0, bytePosition) + 4;
        return y << 16 | x & 0xFFFF;
    }

    public void Dispose()
    {
        try
        {
            Ui(() =>
            {
                try
                {
                    Viewer.Dispose();
                }
                catch (Exception ex)
                {
                    DisposeFailure = ex;
                }

                _form.Close();
            }, 5000);
            _thread.Join(5000);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            DisposeFailure ??= ex;
        }

        _services?.Dispose();
        _ready.Dispose();
    }

    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmMouseMove = 0x0200;

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, nint wParam, nint lParam);

    private sealed class TestForm : Form
    {
        public TestForm()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(60, 60);
            Text = "ZLogger.Scintilla live test";
        }

        protected override bool ShowWithoutActivation => true;
    }
}
