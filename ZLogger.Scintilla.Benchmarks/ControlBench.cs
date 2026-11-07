using System.Diagnostics;
using System.Text;
using ScintillaNET;

namespace ZLogger.Scintilla.Benchmarks;

/// <summary>
/// Scintilla vs RichTextBox with no logging framework: same pre-built lines, same batches, same
/// line cap, same word wrap, one color per line. Each control uses its fastest common idiom.
/// </summary>
internal static class ControlBench
{
    public static readonly string[] Variants = ["scintilla", "richtextbox"];

    private const int BatchSize = 1000;
    private const int FillBatches = 50;
    private const int CappedBatches = 10;
    private const string Common = "needle-common";
    private const string Late = "needle-late";
    private const string Absent = "needle-absent";

    private static readonly (string Tag, Color Color)[] Levels =
    [
        ("INF", Color.Black), ("WRN", Color.DarkOrange), ("ERR", Color.Red), ("DBG", Color.Gray)
    ];

    private readonly record struct Line(string Text, int Level);

    public static int Run(string variant)
    {
        var lines = BuildLines(BatchSize * (FillBatches + CappedBatches));
        IControlUnderTest control = variant == "scintilla" ? new ScintillaUnderTest() : new RichTextBoxUnderTest();
        using var form = new BenchForm();
        control.Control.Dock = DockStyle.Fill;
        form.Controls.Add(control.Control);
        form.Show();
        Harness.Pump(300);
        var baseline = Harness.SettledPrivateMb();

        // Fill to the cap, one batch per flush, painting each time as a live viewer would.
        var fillTimes = new double[FillBatches];
        var allocated = GC.GetTotalAllocatedBytes();
        for (var b = 0; b < FillBatches; b++)
        {
            var batch = lines.AsSpan(b * BatchSize, BatchSize).ToArray();
            var sw = Stopwatch.StartNew();
            control.Append(batch);
            control.ScrollToEnd();
            control.Control.Update();
            fillTimes[b] = sw.Elapsed.TotalMilliseconds;
            Application.DoEvents();
        }
        Harness.Emit("fill_total_ms", fillTimes.Sum());
        Harness.Emit("fill_first10_batch_ms", fillTimes.Take(10).Average());
        Harness.Emit("fill_last10_batch_ms", fillTimes.TakeLast(10).Average());
        Harness.Emit("fill_alloc_mb", (GC.GetTotalAllocatedBytes() - allocated) / (1024.0 * 1024.0));

        // At the cap: every batch also removes the oldest lines.
        var cappedTimes = new double[CappedBatches];
        for (var b = 0; b < CappedBatches; b++)
        {
            var oldest = lines.AsSpan(b * BatchSize, BatchSize);
            var chars = 0;
            foreach (var line in oldest)
                chars += line.Text.Length + 1;
            var batch = lines.AsSpan((FillBatches + b) * BatchSize, BatchSize).ToArray();
            var sw = Stopwatch.StartNew();
            control.Append(batch);
            control.TrimStart(chars);
            control.ScrollToEnd();
            control.Control.Update();
            cappedTimes[b] = sw.Elapsed.TotalMilliseconds;
            Application.DoEvents();
        }
        Harness.Emit("capped_batch_ms", cappedTimes.Average());

        Harness.Pump(500);
        var expectedStart = lines[CappedBatches * BatchSize].Text;
        Harness.Emit("check_trim_correct", control.ReadStart(expectedStart.Length) == expectedStart ? 1 : 0);
        Harness.Emit("check_tail_visible", control.TailVisible() ? 1 : 0);
        Harness.Emit("private_over_baseline_mb", Harness.SettledPrivateMb() - baseline);

        var handle = control.Control.Handle;
        Harness.Emit("repaint_ms", Median(100, () =>
        {
            control.Control.Invalidate();
            control.Control.Update();
        }));
        Harness.Emit("page_up_ms", Median(100, () =>
        {
            Native.SendMessage(handle, Native.WmVScroll, Native.SbPageUp, IntPtr.Zero);
            control.Control.Update();
        }));

        Search("find_first", Common, control.Find);
        Search("find_late", Late, control.Find);
        Search("find_absent", Absent, control.Find);
        if (control is RichTextBoxUnderTest rich)
        {
            Search("find_first_native", Common, rich.FindNative);
            Search("find_late_native", Late, rich.FindNative);
            Search("find_absent_native", Absent, rich.FindNative);
        }
        Environment.Exit(0);
        return 0;
    }

    private static void Search(string name, string token, Func<string, int> find)
    {
        const int count = 7;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var position = -1;
        Harness.Emit($"{name}_ms", Median(count, () => position = find(token)));
        Harness.Emit($"{name}_alloc_kb", (GC.GetAllocatedBytesForCurrentThread() - before) / 1024.0 / count);
        if (token == Absent != (position < 0))
            Harness.Emit($"check_{name}_WRONG", 1);
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

    private static Line[] BuildLines(int count)
    {
        var random = new Random(42);
        var lines = new Line[count];
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var level = random.Next(10) switch { 0 => 2, 1 or 2 => 1, 3 => 3, _ => 0 };
            text.Clear().Append("[12:00:00 ").Append(Levels[level].Tag).Append("] [Bench] Request ").Append(i)
                .Append(" processed in ").Append(random.Next(1500)).Append("ms ctx=");
            for (var words = random.Next(2, 40); words > 0; words--)
                text.Append('k').Append(random.Next(10000)).Append(' ');
            if (i % 100 == 0) text.Append(Common);
            if (i == count - 1) text.Append(Late);
            lines[i] = new Line(text.ToString(), level);
        }
        return lines;
    }

    private interface IControlUnderTest
    {
        Control Control { get; }
        void Append(Line[] batch);
        void TrimStart(int chars);
        void ScrollToEnd();
        bool TailVisible();
        string ReadStart(int length);
        int Find(string token);
    }

    private sealed class ScintillaUnderTest : IControlUnderTest
    {
        private const int SciDocumentEnd = 2318;
        private const int SciSetUndoCollection = 2012;
        private readonly ScintillaNET.Scintilla _control = new() { WrapMode = WrapMode.Word };
        private readonly StringBuilder _text = new();

        public ScintillaUnderTest()
        {
            _control.CreateControl();
            _control.DirectMessage(SciSetUndoCollection, IntPtr.Zero, IntPtr.Zero);
            _control.Styles[Style.Default].Font = "Cascadia Mono";
            _control.Styles[Style.Default].Size = 10;
            _control.StyleClearAll();
            for (var i = 0; i < Levels.Length; i++)
                _control.Styles[i + 1].ForeColor = Levels[i].Color;
            _control.SearchFlags = SearchFlags.None;
        }

        public Control Control => _control;

        public void Append(Line[] batch)
        {
            _text.Clear();
            foreach (var line in batch)
                _text.Append(line.Text).Append('\n');
            var position = _control.TextLength;
            _control.AppendText(_text.ToString());
            _control.StartStyling(position);
            foreach (var line in batch)
                _control.SetStyling(line.Text.Length + 1, line.Level + 1);
        }

        public void TrimStart(int chars) => _control.DeleteRange(0, chars);
        public void ScrollToEnd() => _control.DirectMessage(SciDocumentEnd, IntPtr.Zero, IntPtr.Zero);
        public string ReadStart(int length) => _control.GetTextRange(0, length);

        public bool TailVisible()
        {
            var y = _control.PointYFromPosition(_control.TextLength - 4);
            return y >= 0 && y < _control.ClientSize.Height;
        }

        public int Find(string token)
        {
            _control.TargetStart = 0;
            _control.TargetEnd = _control.TextLength;
            return _control.SearchInTarget(token);
        }
    }

    private sealed class RichTextBoxUnderTest : IControlUnderTest
    {
        private readonly RichTextBox _control = new()
        {
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            Font = new Font("Cascadia Mono", 10f),
            WordWrap = true,
            DetectUrls = false,
            HideSelection = true
        };
        private readonly StringBuilder _rtf = new();
        private readonly string _header;

        public RichTextBoxUnderTest()
        {
            _control.CreateControl();
            Native.SendMessage(_control.Handle, Native.EmSetUndoLimit, IntPtr.Zero, IntPtr.Zero);
            var colors = string.Concat(Levels.Select(l => $"\\red{l.Color.R}\\green{l.Color.G}\\blue{l.Color.B};"));
            _header = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Cascadia Mono;}}{\\colortbl;" + colors + "}\\f0\\fs20 ";
        }

        public Control Control => _control;

        public void Append(Line[] batch)
        {
            _rtf.Clear().Append(_header);
            foreach (var line in batch)
                _rtf.Append("\\cf").Append(line.Level + 1).Append(' ').Append(line.Text).Append("\\par ");
            _rtf.Append('}');

            WithoutRedraw(() =>
            {
                _control.Select(_control.TextLength, 0);
                _control.SelectedRtf = _rtf.ToString();
            });
        }

        public void TrimStart(int chars) => WithoutRedraw(() =>
        {
            _control.Select(0, chars);
            _control.SelectedText = "";
        });

        public void ScrollToEnd()
        {
            _control.Select(_control.TextLength, 0);
            _control.ScrollToCaret();
        }

        public bool TailVisible()
        {
            var y = _control.GetPositionFromCharIndex(_control.TextLength - 4).Y;
            return y >= 0 && y < _control.ClientSize.Height;
        }

        public string ReadStart(int length)
        {
            _control.Select(0, length);
            return _control.SelectedText;
        }

        public int Find(string token) => _control.Find(token, 0, RichTextBoxFinds.None);
        public int FindNative(string token) => Native.RichEditFind(_control.Handle, token, 0);

        private void WithoutRedraw(Action action)
        {
            Native.SendMessage(_control.Handle, Native.WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            action();
            Native.SendMessage(_control.Handle, Native.WmSetRedraw, 1, IntPtr.Zero);
            _control.Invalidate();
        }
    }
}
