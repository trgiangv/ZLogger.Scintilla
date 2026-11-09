using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ZLogger.Scintilla.Benchmarks;

/// <summary>Runs each variant in a fresh process and prints medians.</summary>
internal static class Harness
{
    private const int ChildTimeoutSeconds = 120;

    public static int RunSuite(string suite, string[] variants, int repeats)
    {
        var runs = variants.ToDictionary(v => v, _ => new List<Dictionary<string, double>>());
        var metrics = new List<string>();
        var failures = 0;

        for (var rep = 0; rep < repeats; rep++)
        {
            foreach (var variant in rep % 2 == 0 ? variants : variants.Reverse())
            {
                var (values, ok) = RunChild(suite, variant);
                runs[variant].Add(values);
                foreach (var key in values.Keys.Where(k => !metrics.Contains(k)))
                    metrics.Add(key);
                if (ok)
                    continue;
                failures++;
                Console.WriteLine($"FAILED: {suite} {variant} run {rep + 1} (partial results kept)");
            }
        }

        Console.WriteLine($"\n## {suite}: median [min..max], {repeats} runs\n");
        Console.WriteLine($"| metric | {string.Join(" | ", variants)} |");
        Console.WriteLine($"|---|{string.Concat(variants.Select(_ => "---|"))}");
        foreach (var metric in metrics)
            Console.WriteLine($"| {metric} | {string.Join(" | ", variants.Select(v => Cell(runs[v], metric)))} |");
        return failures == 0 ? 0 : 1;
    }

    private static (Dictionary<string, double> Values, bool Ok) RunChild(string suite, string variant)
    {
        var values = new Dictionary<string, double>();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(Environment.ProcessPath!, $"child {suite} {variant}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.OutputDataReceived += (_, e) =>
        {
            var parts = e.Data?.Split('=');
            if (parts is not { Length: 2 } || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var value))
                return;
            lock (values) values[parts[0]] = value;
        };
        process.Start();
        process.BeginOutputReadLine();
        if (!process.WaitForExit(ChildTimeoutSeconds * 1000))
        {
            process.Kill(entireProcessTree: true);
            return (values, false);
        }
        process.WaitForExit();
        return (values, process.ExitCode == 0);
    }

    private static string Cell(List<Dictionary<string, double>> runs, string metric)
    {
        var values = runs.Where(r => r.ContainsKey(metric)).Select(r => r[metric]).Order().ToArray();
        if (values.Length == 0) return "-";
        var median = Format(values[values.Length / 2]);
        var (low, high) = (Format(values[0]), Format(values[^1]));
        return low == high ? median : $"{median} [{low}..{high}]";
    }

    private static string Format(double v) => v.ToString(Math.Abs(v) >= 100 ? "N0" : Math.Abs(v) >= 1 ? "0.##" : "0.####", CultureInfo.InvariantCulture);

    public static void Emit(string name, double value) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}={value}"));

    public static double PrivateMb()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024.0 * 1024.0);
    }

    public static double SettledPrivateMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Pump(100);
        return PrivateMb();
    }

    public static void Pump(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}

/// <summary>Same visible window for every variant. Does not take focus from what the user is doing.</summary>
internal sealed class BenchForm : Form
{
    public BenchForm()
    {
        ClientSize = new Size(1200, 800);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
    }

    protected override bool ShowWithoutActivation => true;
}

internal static partial class Native
{
    private const int WmUser = 0x400;
    public const int WmSetRedraw = 0x000B;
    public const int WmVScroll = 0x0115;
    public const int SbPageUp = 2;
    public const int EmSetUndoLimit = WmUser + 82;
    private const int EmFindTextExW = WmUser + 124;
    private const int FrDown = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FindTextEx
    {
        public int Min, Max;
        public IntPtr Text;
        public int FoundMin, FoundMax;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref FindTextEx lParam);

    /// <summary>EM_FINDTEXTEXW: RichTextBox's native search, without the WinForms Find wrapper.</summary>
    public static int RichEditFind(IntPtr hwnd, string text, int start)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        try
        {
            var find = new FindTextEx { Min = start, Max = -1, Text = pointer };
            return (int)SendMessage(hwnd, EmFindTextExW, FrDown, ref find);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
