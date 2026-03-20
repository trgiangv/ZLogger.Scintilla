using System;
using System.Collections.Generic;
using System.Text;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Rendering;

internal sealed class RenderTokenWriter(IList<RenderSegment> segments)
{
    public void Add(
        string text,
        SemanticStyle style,
        TokenPayload? payload = null,
        bool isLink = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > 0)
            segments.Add(new RenderSegment(bytes, style, payload, isLink));
    }

    public void Add(
        string source,
        int start,
        int length,
        SemanticStyle style,
        TokenPayload? payload = null,
        bool isLink = false)
    {
        if (length <= 0 || start < 0 || start + length > source.Length)
            return;
        var bytes = Encoding.UTF8.GetByteCount(source.AsSpan(start, length));
        if (bytes > 0)
            segments.Add(new RenderSegment(bytes, style, payload, isLink));
    }
}
