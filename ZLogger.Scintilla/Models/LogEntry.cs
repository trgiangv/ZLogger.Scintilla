using Microsoft.Extensions.Logging;
namespace ZLogger.Scintilla.Models;

/// <summary>A formatted log message. Nothing in the public API accepts or returns one, so it stays internal.</summary>
internal sealed class LogEntry
{
    public static readonly IReadOnlyDictionary<string, object?> EmptyProperties = new Dictionary<string, object?>();

    private IReadOnlyDictionary<string, object?>? _properties;

    public LogLevel Level { get; init; } = LogLevel.Information;
    public ArraySegment<byte> Message { get; init; }

    /// <summary>
    /// Scope properties and typed parameter metadata. Built on first use, because most entries never
    /// meet a linkifier, and a dictionary per log call is the largest cost this property could add.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties
    {
        get => _properties ??= BuildProperties();
        init => _properties = value;
    }

    internal ParameterSpan[]? ParameterSpans { get; init; }
    internal byte[]? JsonBytes { get; init; }

    internal ParameterSpan[]? GetParameterSpans()
    {
        if (ParameterSpans is not null)
            return ParameterSpans;

        return _properties is not null
               && _properties.TryGetValue(LogConstants.ParameterSpans, out var raw)
            ? raw as ParameterSpan[]
            : null;
    }

    internal byte[]? GetJsonBytes()
    {
        if (JsonBytes is not null)
            return JsonBytes;

        return _properties is not null
               && _properties.TryGetValue(LogConstants.JsonBytes, out var raw)
            ? raw as byte[]
            : null;
    }

    private IReadOnlyDictionary<string, object?> BuildProperties()
    {
        if (ParameterSpans is null && JsonBytes is null)
            return EmptyProperties;

        var properties = new Dictionary<string, object?>(2, StringComparer.Ordinal);
        if (ParameterSpans is not null)
            properties[LogConstants.ParameterSpans] = ParameterSpans;
        if (JsonBytes is not null)
            properties[LogConstants.JsonBytes] = JsonBytes;
        return properties;
    }
}
