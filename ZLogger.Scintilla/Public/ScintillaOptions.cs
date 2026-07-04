using System;
using System.Threading.Channels;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Startup configuration for the Scintilla log viewer.
/// Supports both direct property assignment and fluent method chaining
/// (following the same pattern as <c>ZLoggerOptions</c>).
/// All settings are sealed after the viewer is constructed — use
/// <see cref="ScintillaLogViewerBase"/> for runtime-mutable operations.
/// </summary>
public sealed class ScintillaOptions
{
    public int ChannelCapacity { get; private set; } = LogConstants.DefaultChannelCapacity;
    public BoundedChannelFullMode DropPolicy { get; private set; } = BoundedChannelFullMode.DropWrite;
    public int MaxBatchSize { get; private set; } = LogConstants.DefaultMaxBatchSize;
    public int FlushIntervalMs { get; private set; } = LogConstants.DefaultFlushIntervalMs;

    public int MaxLines { get; private set; } = LogConstants.DefaultMaxLines;
    public bool AutoScroll { get; private set; } = true;
    public string FontFamily { get; private set; } = LogConstants.DefaultFontFamily;
    public int FontSize { get; private set; } = LogConstants.DefaultFontSize;

    public int MaxHistoryEntries { get; private set; } = LogConstants.DefaultMaxHistoryEntries;
    public bool DisableHistory { get; private set; }

    public IScintillaTheme Theme { get; private set; } = ScintillaThemes.Dark;
    public ILogColorizer? Colorizer { get; private set; }
    public ILinkifier? Linkifier { get; private set; }

    /// <summary>Enable clickable link tokens. True when any linkifier is registered.</summary>
    public bool EnableTokenLinks => Linkifier is not null;

    /// <summary>
    /// Registers a sink for exceptions thrown by colorizers and linkifiers during rendering.
    /// </summary>
    public Action<Exception>? EnrichmentErrorSink { get; private set; }

    /// <summary>Max recursion depth for JSON object decomposition in pretty-print mode. Default 2.</summary>
    public int JsonDecomposeDepth { get; private set; } = LogConstants.DefaultDecomposeDepth;

    /// <summary>Max items to serialize per collection/iterable in pretty-print JSON. Default 50.</summary>
    public int JsonDecomposeMaxCollection { get; private set; } = LogConstants.DefaultDecomposeCollection;

    /// <summary>
    /// Optional serializer for parameter types that cannot be safely decomposed
    /// by the default reflection-based pipeline (e.g. Python objects).
    /// </summary>
    public ICustomSerializer? CustomSerializer { get; private set; }

    /// <summary>Configures the ingest channel.</summary>
    public ScintillaOptions Channel(
        int capacity = LogConstants.DefaultChannelCapacity,
        int flushMs = LogConstants.DefaultFlushIntervalMs,
        int maxBatch = LogConstants.DefaultMaxBatchSize,
        BoundedChannelFullMode dropPolicy = BoundedChannelFullMode.DropWrite)
    {
        ChannelCapacity = capacity > 0 ? capacity : LogConstants.DefaultChannelCapacity;
        FlushIntervalMs = flushMs > 0 ? flushMs : LogConstants.DefaultFlushIntervalMs;
        MaxBatchSize = maxBatch > 0 ? maxBatch : LogConstants.DefaultMaxBatchSize;
        DropPolicy = dropPolicy;
        return this;
    }

    /// <summary>Configures display and document limits.</summary>
    public ScintillaOptions Display(
        int maxLines = LogConstants.DefaultMaxLines,
        string? font = null,
        int fontSize = LogConstants.DefaultFontSize,
        bool autoScroll = true)
    {
        MaxLines = maxLines > 0 ? maxLines : LogConstants.DefaultMaxLines;
        FontFamily = string.IsNullOrWhiteSpace(font) ? LogConstants.DefaultFontFamily : font!;
        FontSize = fontSize > 0 ? fontSize : LogConstants.DefaultFontSize;
        AutoScroll = autoScroll;
        return this;
    }

    /// <summary>Configures the log history buffer used for filter reapply.</summary>
    public ScintillaOptions History(int maxEntries = LogConstants.DefaultMaxHistoryEntries, bool disabled = false)
    {
        MaxHistoryEntries = maxEntries >= 0 ? maxEntries : LogConstants.DefaultMaxHistoryEntries;
        DisableHistory = disabled;
        return this;
    }

    /// <summary>Sets the initial theme. Can be changed at runtime via <see cref="ScintillaLogViewerBase.SetTheme"/>.</summary>
    public ScintillaOptions SetTheme(IScintillaTheme? theme)
    {
        Theme = theme ?? ScintillaThemes.Dark;
        return this;
    }

    /// <summary>
    /// Registers a colorizer that overrides token foreground colors.
    /// If a token is also matched by a linkifier, only the color is overridden.
    /// </summary>
    public ScintillaOptions WithColorized(ILogColorizer colorizer)
    {
        Colorizer = colorizer;
        return this;
    }

    /// <summary>
    /// Registers a linkifier that creates clickable tokens. First match wins.
    /// </summary>
    public ScintillaOptions WithLinkify(ILinkifier linkifier)
    {
        Linkifier = linkifier;
        return this;
    }

    /// <summary>
    /// Registers a sink for exceptions thrown by colorizers and linkifiers during rendering.
    /// Use this to log or surface enrichment errors without crashing the viewer.
    /// </summary>
    public ScintillaOptions WithEnrichmentErrorSink(Action<Exception> sink)
    {
        EnrichmentErrorSink = sink;
        return this;
    }

    /// <summary>
    /// Sets the max recursion depth for JSON object decomposition in pretty-print mode.
    /// Higher values produce more detailed JSON but risk performance with deeply nested or circular objects.
    /// Default is 2.
    /// </summary>
    public ScintillaOptions WithJsonDepth(int depth)
    {
        JsonDecomposeDepth = depth > 0 ? depth : LogConstants.DefaultDecomposeDepth;
        return this;
    }

    /// <summary>
    /// Sets the max number of items to serialize per collection in pretty-print JSON.
    /// Higher values show more data but risk performance with large collections.
    /// Default is 50.
    /// </summary>
    public ScintillaOptions WithCollectionLimit(int maxItems)
    {
        JsonDecomposeMaxCollection = maxItems > 0 ? maxItems : LogConstants.DefaultDecomposeCollection;
        return this;
    }

    /// <summary>
    /// Registers a custom parameter serializer for types that the default
    /// <c>LookupComposer</c> decomposition cannot safely process.
    /// </summary>
    public ScintillaOptions WithCustomSerializer(ICustomSerializer serializer)
    {
        CustomSerializer = serializer;
        return this;
    }
}
