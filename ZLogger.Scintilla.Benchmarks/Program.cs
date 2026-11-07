using BenchmarkDotNet.Running;

namespace ZLogger.Scintilla.Benchmarks;

/// <summary>
/// Three questions, measured separately so a result can be attributed to the right layer:
///   logger  - ZLogger vs Serilog, no UI (plus the cost this repo's processor adds on top of ZLogger)
///   control - Scintilla vs RichTextBox, no logging framework
///   stack   - this library vs Serilog + its RichTextBox sink, end to end on a visible window
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var suite = args.Length > 0 ? args[0] : "";
        var repeats = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 3;
        switch (suite)
        {
            case "child" when args[1] == "control":
                return ControlBench.Run(args[2]);
            case "child" when args[1] == "stack":
                return StackBench.Run(args[2]);
            case "control":
                return Harness.RunSuite("control", ControlBench.Variants, repeats);
            case "stack":
                return Harness.RunSuite("stack", StackBench.Variants, repeats);
            case "logger":
                BenchmarkRunner.Run<LoggerBenchmarks>();
                return 0;
            case "all":
                BenchmarkRunner.Run<LoggerBenchmarks>();
                return Harness.RunSuite("control", ControlBench.Variants, repeats)
                     + Harness.RunSuite("stack", StackBench.Variants, repeats);
            default:
                BenchmarkSwitcher.FromTypes([typeof(LoggerBenchmarks)]).Run(args);
                return 0;
        }
    }
}
