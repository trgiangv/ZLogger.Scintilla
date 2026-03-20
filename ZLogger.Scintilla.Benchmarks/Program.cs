using BenchmarkDotNet.Running;
using ZLogger.Scintilla.Benchmarks.Benchmarks;

namespace ZLogger.Scintilla.Benchmarks;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application.SetCompatibleTextRenderingDefault(false);
        BenchmarkSwitcher.FromTypes(
        [
            typeof(IngestThroughputBenchmark),
            typeof(ColorizedRenderBenchmark),
            typeof(LargeDocumentSearchBenchmark),
            typeof(FilterReapplyBenchmark),
            typeof(PaintBenchmark),
            typeof(SustainedLoadBenchmark),
        ]).Run(args);
    }
}
