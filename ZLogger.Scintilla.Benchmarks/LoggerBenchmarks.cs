using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using ZLogger.Scintilla.Pipeline;

namespace ZLogger.Scintilla.Benchmarks;

/// <summary>
/// Logging framework cost with no UI. "Discard" ends at the framework's output (ZLogger processor,
/// Serilog sink) without formatting. "Format" also renders the message to reused UTF-8 buffers.
/// "Repo" is this library's processor formatting on the logging thread, so Repo minus ZLogger Format is the cost
/// this repo adds. "Repo_Deferred" is what the logging thread pays when the pump does the formatting.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class LoggerBenchmarks
{
    private const string Template = "Request {Id} processed in {Ms}ms from {Ip}";
    private const string Ip = "10.0.0.1";
    private const int Id = 42;
    private const int Ms = 17;

    private ILogger _zloggerDiscard = null!;
    private ILogger _zloggerFormat = null!;
    private ILogger _repo = null!;
    private ILogger _repoDeferred = null!;
    private Serilog.ILogger _serilogDiscard = null!;
    private Serilog.ILogger _serilogFormat = null!;

    private sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
    }

    [GlobalSetup]
    public void Setup()
    {
        _zloggerDiscard = ZLoggerTo(new DiscardProcessor());
        _zloggerFormat = ZLoggerTo(new FormatProcessor());
        var repo = new ScintillaLogProcessor(_ => true);
        repo.SetCaptureAllParameters(false); // What a viewer with no linkifier uses.
        _repo = ZLoggerTo(repo);

        var deferred = new ScintillaLogProcessor(_ => true);
        deferred.SetCaptureAllParameters(false);
        deferred.SetDeferredSink(static entry =>
        {
            entry.Return();
            return true;
        }, System.Globalization.CultureInfo.CurrentCulture);
        _repoDeferred = ZLoggerTo(deferred);
        _serilogDiscard = SerilogTo(new DiscardSink());
        _serilogFormat = SerilogTo(new FormatSink());
    }

    [Benchmark(Baseline = true)] public void Serilog_Discard() => _serilogDiscard.Information(Template, Id, Ms, Ip);
    [Benchmark] public void Serilog_Format() => _serilogFormat.Information(Template, Id, Ms, Ip);
    [Benchmark] public void ZLogger_Discard_Template() => _zloggerDiscard.LogInformation(Template, Id, Ms, Ip);
    [Benchmark] public void ZLogger_Discard_Interpolated() => _zloggerDiscard.ZLogInformation($"Request {Id} processed in {Ms}ms from {Ip}");
    [Benchmark] public void ZLogger_Format_Template() => _zloggerFormat.LogInformation(Template, Id, Ms, Ip);
    [Benchmark] public void ZLogger_Format_Interpolated() => _zloggerFormat.ZLogInformation($"Request {Id} processed in {Ms}ms from {Ip}");
    [Benchmark] public void Repo_Template() => _repo.LogInformation(Template, Id, Ms, Ip);
    [Benchmark] public void Repo_Interpolated() => _repo.ZLogInformation($"Request {Id} processed in {Ms}ms from {Ip}");
    [Benchmark] public void Repo_Deferred_Template() => _repoDeferred.LogInformation(Template, Id, Ms, Ip);
    [Benchmark] public void Repo_Deferred_Interpolated() => _repoDeferred.ZLogInformation($"Request {Id} processed in {Ms}ms from {Ip}");

    private static ILogger ZLoggerTo(IAsyncLogProcessor processor) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddZLoggerLogProcessor(processor);
        }).CreateLogger("Bench");

    private static Serilog.ILogger SerilogTo(ILogEventSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private sealed class DiscardProcessor : IAsyncLogProcessor
    {
        public void Post(IZLoggerEntry log) => log.Return();
        public ValueTask DisposeAsync() => default;
    }

    private sealed class FormatProcessor : IAsyncLogProcessor
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(4096);

        public void Post(IZLoggerEntry log)
        {
            _buffer.ResetWrittenCount();
            log.ToString(_buffer);
            log.Return();
        }

        public ValueTask DisposeAsync() => default;
    }

    private sealed class DiscardSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
        }
    }

    private sealed class FormatSink : ILogEventSink
    {
        private readonly MemoryStream _stream = new(4096);
        private readonly StreamWriter _writer;

        public FormatSink() => _writer = new StreamWriter(_stream, new UTF8Encoding(false), 4096);

        public void Emit(LogEvent logEvent)
        {
            _stream.SetLength(0);
            logEvent.RenderMessage(_writer);
            _writer.Flush();
        }
    }
}
