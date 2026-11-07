using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Sinks.RichTextBoxForms;
using Serilog.Sinks.RichTextBoxForms.Rendering;
using ZLogger.Scintilla.Public;
using SerilogThemes = Serilog.Sinks.RichTextBoxForms.Themes.ThemePresets;

namespace ZLogger.Scintilla.Benchmarks;

/// <summary>
/// End to end through public logging APIs onto a visible window, measuring what a user experiences.
/// "library" and "serilog" keep the same number of lines: 2,048, the most the sink's public API allows. The
/// sink renders its whole buffer on every flush, so its cost depends on that number and a comparison at
/// different sizes says nothing. "library50k" is the library's default, which the sink has no counterpart for.
/// A difference here belongs to framework, pipeline and control together, not to any one of them.
/// </summary>
internal static class StackBench
{
    public static readonly string[] Variants =
    [
        "library.burst", "serilog.burst",
        "library.sustained", "serilog.sustained",
        "library50k.burst", "library50k.sustained"
    ];

    private const int ComparedLines = 2048;
    private const int BurstMessages = 50_000;
    private const int SustainedRate = 20_000;
    private const int SustainedSeconds = 5;
    private const int IdleSamples = 9;
    private const string Template = "Request {Id} processed in {Ms}ms from {Ip} ctx={Ctx}";
    private const string Sentinel = "bench-sentinel";
    private const string Absent = "bench-absent-token";
    private const string OutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext:l}] {Message:lj}{NewLine}{Exception}";

    public static int Run(string variant)
    {
        var parts = variant.Split('.');
        var form = new BenchForm();
        IStack stack = parts[0] switch
        {
            "library" => new LibraryStack(form, ComparedLines),
            "library50k" => new LibraryStack(form, maxLines: 50_000),
            _ => new SerilogStack(form, ComparedLines)
        };
        form.Shown += (_, _) =>
        {
            stack.Start();
            new Thread(() => Measure(form, stack, parts[1] == "burst")) { IsBackground = true }.Start();
        };
        Application.Run(form);
        return 0;
    }

    private static void Measure(Form form, IStack stack, bool burst)
    {
        Thread.Sleep(500);
        Harness.Emit("idle_line_visible_ms", MeasureIdleLatency(form, stack));

        var baseline = Harness.PrivateMb();
        var allocated = GC.GetTotalAllocatedBytes();
        var contexts = Enumerable.Range(0, 64).Select(i => string.Join(' ', Enumerable.Range(0, i).Select(k => "k" + k))).ToArray();
        var total = burst ? BurstMessages : SustainedRate * SustainedSeconds;
        void Log(int i) => stack.Log(i, i % 1500, "10.0.0.1", contexts[i % contexts.Length]);

        var freeze = new FreezeMeter(form);
        var paintsBefore = form.Invoke(() => stack.Paints);
        var load = Stopwatch.StartNew();

        if (burst)
            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 4 }, Log);
        else
            ProduceAtRate(total, Log);

        var producerMs = load.Elapsed.TotalMilliseconds;
        stack.LogSentinel(Sentinel);
        var visible = WaitUntilVisible(form, stack, Sentinel, TimeSpan.FromSeconds(60));
        var lastVisibleMs = load.Elapsed.TotalMilliseconds;
        var freezeMax = freeze.Stop();
        var paints = form.Invoke(() => stack.Paints) - paintsBefore;

        Harness.Emit("completed", visible ? 1 : 0);
        Harness.Emit("last_line_visible_ms", lastVisibleMs);
        if (burst)
            Harness.Emit("logging_threads_ms", producerMs);
        Harness.Emit("ui_freeze_max_ms", freezeMax);
        Harness.Emit("frames_per_s", paints / load.Elapsed.TotalSeconds);
        Harness.Emit("total_alloc_mb", (GC.GetTotalAllocatedBytes() - allocated) / (1024.0 * 1024.0));
        Harness.Emit("private_over_baseline_mb", Harness.PrivateMb() - baseline);

        Thread.Sleep(1500);
        var settled = form.Invoke(() => stack.Paints);
        Thread.Sleep(1000);
        Harness.Emit("paints_per_s_when_quiet", form.Invoke(() => stack.Paints) - settled);
        form.Invoke(() =>
        {
            Harness.Emit("retained_messages", stack.RetainedLines());
            Harness.Emit("tail_visible", stack.TailVisible() ? 1 : 0);
            Harness.Emit("find_absent_ms", Median(7, () => stack.Find(Absent)));
            Harness.Emit("page_up_ms", Median(20, stack.PageUp));
        });
        Environment.Exit(0);
    }

    /// <summary>How long one line takes to appear when nothing else is happening, the case a person watches most.</summary>
    private static double MeasureIdleLatency(Form form, IStack stack)
    {
        var samples = new List<double>();
        for (var i = 0; i < IdleSamples; i++)
        {
            var token = "idle-line-" + i;
            var sw = Stopwatch.StartNew();
            stack.LogSentinel(token);
            if (WaitUntilVisible(form, stack, token, TimeSpan.FromSeconds(5)))
                samples.Add(sw.Elapsed.TotalMilliseconds);
            Thread.Sleep(200);
        }

        samples.Sort();
        return samples.Count == 0 ? double.NaN : samples[samples.Count / 2];
    }

    private static void ProduceAtRate(int total, Action<int> log)
    {
        var producer = Stopwatch.StartNew();
        for (var sent = 0; sent < total;)
        {
            var due = Math.Min(total, (int)(producer.Elapsed.TotalSeconds * SustainedRate));
            while (sent < due)
                log(sent++);
            Thread.Sleep(1);
        }
    }

    private static bool WaitUntilVisible(Form form, IStack stack, string token, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (form.Invoke(() => stack.HasText(token) && stack.TailVisible()))
                return true;
            Thread.Sleep(1);
        }

        return false;
    }

    private static double Median(int count, Action action)
    {
        var times = new double[count];
        for (var i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times[count / 2];
    }

    /// <summary>
    /// The longest gap between ticks of a UI timer. Windows delivers a timer only when no posted message, input or
    /// paint is waiting, so a long gap is a window that cannot respond. A round trip with Invoke would miss this,
    /// because its posted message overtakes the input and paint that are starving.
    /// </summary>
    private sealed class FreezeMeter
    {
        private readonly Form _form;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Stopwatch _sinceTick = new();
        private double _max;

        public FreezeMeter(Form form)
        {
            _form = form;
            _timer = form.Invoke(() => new System.Windows.Forms.Timer { Interval = 1 });
            _timer.Tick += (_, _) =>
            {
                _max = Math.Max(_max, _sinceTick.Elapsed.TotalMilliseconds);
                _sinceTick.Restart();
            };
            form.Invoke(() =>
            {
                _sinceTick.Start();
                _timer.Start();
            });
        }

        public double Stop()
        {
            _form.Invoke(() =>
            {
                _timer.Stop();
                _max = Math.Max(_max, _sinceTick.Elapsed.TotalMilliseconds);
            });
            return _max;
        }
    }

    private interface IStack
    {
        int Paints { get; }
        void Start();
        void Log(int id, int ms, string ip, string ctx);
        void LogSentinel(string sentinel);
        bool HasText(string text);
        int RetainedLines();
        bool TailVisible();
        void Find(string text);
        void PageUp();
    }

    private sealed class LibraryStack : IStack
    {
        private readonly ScintillaLogViewer _viewer;
        private readonly ILogger _logger;

        public int Paints { get; private set; }

        public LibraryStack(Form form, int maxLines)
        {
            var services = new ServiceCollection()
                .AddLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddZLoggerScintillaWinForms(o => o.Display(maxLines: maxLines).History(maxEntries: maxLines));
                })
                .BuildServiceProvider();
            _viewer = services.GetRequiredService<ScintillaLogViewer>();
            _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Bench");
            _viewer.ScintillaControl.Painted += (_, _) => Paints++;
            form.Controls.Add(_viewer.ScintillaControl);
        }

        public void Start() => _viewer.Start();
        public void Log(int id, int ms, string ip, string ctx) => _logger.LogInformation(Template, id, ms, ip, ctx);
        public void LogSentinel(string sentinel) => _logger.LogInformation(sentinel);
        public int RetainedLines() => _viewer.ScintillaControl.Lines.Count - 1;

        public bool HasText(string text)
        {
            var control = _viewer.ScintillaControl;
            var length = control.TextLength;
            var start = Math.Max(0, length - 200);
            return control.GetTextRange(start, length - start).Contains(text, StringComparison.Ordinal);
        }

        public bool TailVisible()
        {
            var control = _viewer.ScintillaControl;
            var y = control.PointYFromPosition(control.TextLength - 4);
            return y >= 0 && y < control.ClientSize.Height;
        }

        public void Find(string text)
        {
            _viewer.HighlightSearch(text);
            _viewer.FindNext();
            _viewer.ClearSearch();
        }

        public void PageUp()
        {
            var control = _viewer.ScintillaControl;
            Native.SendMessage(control.Handle, Native.WmVScroll, Native.SbPageUp, IntPtr.Zero);
            control.Update();
        }
    }

    private sealed class PaintCountingRichTextBox : RichTextBox
    {
        private const int WmPaint = 0x000F;

        public int Paints { get; private set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmPaint)
                Paints++;
            base.WndProc(ref m);
        }
    }

    private sealed class SerilogStack : IStack
    {
        private readonly PaintCountingRichTextBox _control = new()
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = new Font("Cascadia Mono", 10f)
        };
        private readonly Serilog.ILogger _logger;

        public int Paints => _control.Paints;

        public SerilogStack(Form form, int maxLines)
        {
            var options = new RichTextBoxSinkOptions(
                SerilogThemes.Literate,
                autoScroll: true,
                maxLogLines: maxLines,
                outputTemplate: OutputTemplate);
            var sink = new RichTextBoxSink(_control, options, new TemplateRenderer(options));
            _logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger()
                .ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Bench");
            form.Controls.Add(_control);
        }

        public void Start()
        {
        }

        public void Log(int id, int ms, string ip, string ctx) => _logger.Information(Template, id, ms, ip, ctx);
        public void LogSentinel(string sentinel) => _logger.Information(sentinel);
        public bool HasText(string text) => Native.RichEditFind(_control.Handle, text, Math.Max(0, _control.TextLength - 200)) >= 0;
        public int RetainedLines() => _control.Lines.Length - 1;

        public bool TailVisible()
        {
            var y = _control.GetPositionFromCharIndex(_control.TextLength - 4).Y;
            return y >= 0 && y < _control.ClientSize.Height;
        }

        public void Find(string text) => _control.Find(text);

        public void PageUp()
        {
            Native.SendMessage(_control.Handle, Native.WmVScroll, Native.SbPageUp, IntPtr.Zero);
            _control.Update();
        }
    }
}
