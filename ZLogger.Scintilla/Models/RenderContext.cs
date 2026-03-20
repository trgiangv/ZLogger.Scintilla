using System;
using System.Collections.Generic;
namespace ZLogger.Scintilla.Models;

public readonly struct RenderContext(ReadOnlyMemory<byte> messageBytes, IReadOnlyDictionary<string, object?> properties)
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new Dictionary<string, object?>(0);

    public static readonly RenderContext Empty = new(
        ReadOnlyMemory<byte>.Empty, EmptyProperties);

    public ReadOnlyMemory<byte> MessageBytes { get; } = messageBytes;
    public IReadOnlyDictionary<string, object?> Properties { get; } = properties;
}
