using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using SerilogThemePresets = Serilog.Sinks.RichTextBoxForms.Themes.ThemePresets;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

internal static class BenchmarkHarness
{
    private const int WmPrintClient = 0x0318;
    private const int PrfClient = 0x00000004;
    private const int PrfEraseBkgnd = 0x00000008;
    private const int PrfChildren = 0x00000010;

    // ── Scintilla factory ────────────────────────────────────────────────

    public static ScintillaLogViewer CreateScintillaViewer(
        int maxLines = 50_000,
        int maxHistory = 50_000,
        bool disableHistory = false,
        bool autoScroll = false,
        int maxBatchSize = 4096,
        int flushIntervalMs = 1)
    {
        return new ScintillaLogViewer(b => b
            .Display(maxLines: maxLines, autoScroll: autoScroll)
            .Channel(maxBatch: maxBatchSize, flushMs: flushIntervalMs)
            .History(maxEntries: maxHistory, disabled: disableHistory));
    }

    // ── Serilog factories ────────────────────────────────────────────────

    public static (RichTextBox rtb, Serilog.ILogger logger, RichTextBoxSink sink) CreateSerilogProductionSink()
    {
        var rtb = new RichTextBox();
        PrepareOffscreenControl(rtb);
        var sink = new RichTextBoxSink(rtb, new RichTextBoxSinkOptions(
            SerilogThemePresets.Colored,
            autoScroll: false,
            maxLogLines: 2048,
            prettyPrintJson: true));
        var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (rtb, logger, sink);
    }

    public static (RichTextBox rtb, Serilog.ILogger logger, RichTextBoxSink sink) CreateSerilogUncappedSink(int maxLines = 50_000)
    {
        var rtb = new RichTextBox();
        PrepareOffscreenControl(rtb);
        var options = CreateUncappedOptions(maxLines);
        var sink = new RichTextBoxSink(rtb, options);
        var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (rtb, logger, sink);
    }

    /// <summary>
    /// Creates a RichTextBoxSinkOptions with the MaxLogLines cap bypassed via reflection.
    /// The original setter clamps to [1, 2048]; we write directly to the backing field.
    /// </summary>
    private static RichTextBoxSinkOptions CreateUncappedOptions(int maxLines)
    {
        var options = new RichTextBoxSinkOptions(
            SerilogThemePresets.Colored,
            autoScroll: false,
            maxLogLines: 2048,
            prettyPrintJson: true);

        var field = typeof(RichTextBoxSinkOptions)
            .GetField("_maxLogLines", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is null)
            throw new InvalidOperationException(
                "Cannot find _maxLogLines field on RichTextBoxSinkOptions. " +
                "The Serilog sink library may have changed its internals.");
        field.SetValue(options, maxLines);
        return options;
    }

    // ── Scintilla ingest helpers ─────────────────────────────────────────

    public static void IngestToScintilla(
        ScintillaLogViewer viewer,
        IReadOnlyList<byte[]> utf8Messages,
        IReadOnlyList<LogLevel> levels)
    {
        for (var i = 0; i < utf8Messages.Count; i++)
        {
            var bytes = utf8Messages[i];
            viewer.TryPost(new LogEntry
            {
                Level = levels[i],
                Message = new ArraySegment<byte>(bytes, 0, bytes.Length),
                Properties = LogEntry.EmptyProperties
            });
        }
    }

    public static void IngestToScintilla(
        ScintillaLogViewer viewer,
        IReadOnlyList<byte[]> utf8Messages,
        LogLevel level = LogLevel.Information)
    {
        for (var i = 0; i < utf8Messages.Count; i++)
        {
            var bytes = utf8Messages[i];
            viewer.TryPost(new LogEntry
            {
                Level = level,
                Message = new ArraySegment<byte>(bytes, 0, bytes.Length),
                Properties = LogEntry.EmptyProperties
            });
        }
    }

    public static void EmitToSerilog(
        Serilog.ILogger logger,
        IReadOnlyList<string> messages,
        IReadOnlyList<LogLevel> levels)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var serilogLevel = MapToSerilogLevel(levels[i]);
            logger.Write(serilogLevel, "{Message}", messages[i]);
        }
    }

    public static void EmitToSerilog(Serilog.ILogger logger, IReadOnlyList<string> messages)
    {
        for (var i = 0; i < messages.Count; i++)
            logger.Information("{Message}", messages[i]);
    }

    // ── Wait / drain ─────────────────────────────────────────────────────

    public static void WaitForScintillaDrain(ScintillaLogViewerBase controller, int expectedDelta = 0, int timeoutMs = 5000)
    {
        var startRendered = controller.RenderedMessages;
        var targetRendered = expectedDelta > 0 ? startRendered + expectedDelta : startRendered;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var drained = controller.PendingEntryCount <= 0;
            var rendered = expectedDelta <= 0 || controller.RenderedMessages >= targetRendered;
            if (drained && rendered)
            {
                Thread.SpinWait(128);
                if (controller.PendingEntryCount <= 0 &&
                    (expectedDelta <= 0 || controller.RenderedMessages >= targetRendered))
                    return;
            }
            Thread.Sleep(0);
        }
    }

    /// <summary>
    /// Waits for a filter reapply to complete by polling the Scintilla control's line count
    /// until it stabilises. Filter reapply goes through BeginInvoke (not the ingest channel),
    /// so WaitForScintillaDrain is insufficient — it only checks PendingEntryCount.
    /// </summary>
    public static void WaitForFilterReapply(ScintillaLogViewer viewer, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastLines = -1;
        var stableTicks = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            var current = viewer.ScintillaControl.Lines.Count;
            if (current == lastLines && current > 0)
            {
                if (++stableTicks >= 4) return;
            }
            else
            {
                stableTicks = 0;
                lastLines = current;
            }
            Thread.Sleep(5);
        }
    }

    public static void WaitForRichTextStable(RichTextBox rtb, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastLength = -1;
        var stableTicks = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var current = rtb.TextLength;
            if (current == lastLength)
            {
                if (++stableTicks >= 3) return;
            }
            else
            {
                stableTicks = 0;
                lastLength = current;
            }
            Thread.Sleep(5);
        }
    }

    // ── Control helpers ──────────────────────────────────────────────────

    public static void PrepareOffscreenControl(System.Windows.Forms.Control control, int width = 1200, int height = 800)
    {
        if (control.Width != width || control.Height != height)
            control.Size = new Size(width, height);
        if (!control.IsHandleCreated)
            control.CreateControl();
    }

    // ── Paint helpers ────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static int ForcePaintAndHash(System.Windows.Forms.Control control)
    {
        if (!control.IsHandleCreated) control.CreateControl();
        control.Invalidate(true);
        control.Update();

        var w = Math.Max(1, control.ClientSize.Width);
        var h = Math.Max(1, control.ClientSize.Height);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var gfx = Graphics.FromImage(bmp);
        var hdc = gfx.GetHdc();
        try
        {
            SendMessage(control.Handle, WmPrintClient, hdc, (IntPtr)(PrfClient | PrfEraseBkgnd | PrfChildren));
        }
        finally
        {
            gfx.ReleaseHdc(hdc);
        }
        return SparseBitmapHash(bmp);
    }

    internal sealed class OffscreenPainter : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly Graphics _graphics;

        public OffscreenPainter(int width, int height)
        {
            _bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppPArgb);
            _graphics = Graphics.FromImage(_bitmap);
        }

        public int PaintAndHash(System.Windows.Forms.Control control)
        {
            if (!control.IsHandleCreated) control.CreateControl();
            control.Invalidate(true);
            control.Update();
            var hdc = _graphics.GetHdc();
            try
            {
                SendMessage(control.Handle, WmPrintClient, hdc, (IntPtr)(PrfClient | PrfEraseBkgnd | PrfChildren));
            }
            finally
            {
                _graphics.ReleaseHdc(hdc);
            }
            return SparseBitmapHash(_bitmap);
        }

        public void Dispose()
        {
            _graphics.Dispose();
            _bitmap.Dispose();
        }
    }

    // ── Level mapping ────────────────────────────────────────────────────

    public static LogEventLevel MapToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

    // ── Bitmap hash ──────────────────────────────────────────────────────

    private static int SparseBitmapHash(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unchecked
            {
                const int fnvOffset = (int)2166136261;
                const int fnvPrime = 16777619;
                var hash = fnvOffset;
                var xStep = Math.Max(1, bitmap.Width / 16);
                var yStep = Math.Max(1, bitmap.Height / 16);
                for (var y = 0; y < bitmap.Height; y += yStep)
                for (var x = 0; x < bitmap.Width; x += xStep)
                {
                    var pixel = Marshal.ReadInt32(data.Scan0, (y * data.Stride) + (x * 4));
                    hash = (hash ^ pixel) * fnvPrime;
                }
                return hash;
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
