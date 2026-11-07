namespace ZLogger.Scintilla.Models;

public readonly struct RenderContext
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>(0);

    public static readonly RenderContext Empty = new(
        ReadOnlyMemory<byte>.Empty, EmptyProperties);

    private readonly LogEntry? _entry;
    private readonly IReadOnlyDictionary<string, object?>? _properties;

    public RenderContext(ReadOnlyMemory<byte> messageBytes, IReadOnlyDictionary<string, object?> properties)
    {
        MessageBytes = messageBytes;
        _properties = properties;
    }

    internal RenderContext(ReadOnlyMemory<byte> messageBytes, LogEntry entry)
    {
        MessageBytes = messageBytes;
        _entry = entry;
    }

    public ReadOnlyMemory<byte> MessageBytes { get; }
    public IReadOnlyDictionary<string, object?> Properties => _entry?.Properties ?? _properties ?? EmptyProperties;

    internal ParameterSpan[]? ParameterSpans
    {
        get
        {
            if (_entry is not null)
                return _entry.GetParameterSpans();

            return _properties is not null
                   && _properties.TryGetValue(LogConstants.ParameterSpans, out var raw)
                ? raw as ParameterSpan[]
                : null;
        }
    }
}
