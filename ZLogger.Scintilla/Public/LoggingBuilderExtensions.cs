using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Rendering;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Extension methods for wiring a <see cref="ScintillaLogViewerBase"/> into the
/// ZLogger pipeline via <see cref="ILoggingBuilder"/>.
/// The viewer is created internally and registered as a singleton — retrieve it
/// later with <c>GetRequiredService&lt;ScintillaLogViewerBase&gt;()</c> or by concrete type.
/// </summary>
public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Creates a WinForms <see cref="ScintillaLogViewer"/> and wires it into the ZLogger pipeline.
    /// Resolve via <c>GetRequiredService&lt;ScintillaLogViewer&gt;()</c> or
    /// <c>GetRequiredService&lt;ScintillaLogViewerBase&gt;()</c>.
    /// </summary>
    public static ILoggingBuilder AddZLoggerScintillaWinForms(
        this ILoggingBuilder builder,
        Action<ScintillaOptions>? configure = null,
        Action<ZLoggerOptions>? configureZLogger = null)
    {
        var viewer = new ScintillaLogViewer(configure);
        return RegisterViewer(builder, viewer, configureZLogger);
    }

    /// <summary>
    /// Creates a WPF <see cref="ScintillaLogViewerWpf"/> (backed by <c>WindowsFormsHost</c>)
    /// and wires it into the ZLogger pipeline.
    /// Resolve via <c>GetRequiredService&lt;ScintillaLogViewerWpf&gt;()</c> or
    /// <c>GetRequiredService&lt;ScintillaLogViewerBase&gt;()</c>.
    /// </summary>
    public static ILoggingBuilder AddZLoggerScintillaWpf(
        this ILoggingBuilder builder,
        Action<ScintillaOptions>? configure = null,
        Action<ZLoggerOptions>? configureZLogger = null)
    {
        var viewer = new ScintillaLogViewerWpf(configure);
        return RegisterViewer(builder, viewer, configureZLogger);
    }

    private static ILoggingBuilder RegisterViewer<TViewer>(
        ILoggingBuilder builder, TViewer viewer,
        Action<ZLoggerOptions>? configureZLogger)
        where TViewer : ScintillaLogViewerBase
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        builder.Services.TryAddSingleton(viewer);
        builder.Services.TryAddSingleton<ScintillaLogViewerBase>(viewer);

        builder.AddZLoggerLogProcessor((zloggerOptions, _) =>
        {
            zloggerOptions.UseFormatter(static () => new ScintillaLogFormatter());
            configureZLogger?.Invoke(zloggerOptions);

            var processor = new ScintillaLogProcessor(zloggerOptions, viewer.TryPost);
            viewer.AttachProcessor(processor);
            return processor;
        });

        return builder;
    }
}
