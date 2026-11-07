using System.Drawing;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Rendering;

internal sealed class LogRenderer
{
    private readonly string _fontFamily;
    private readonly int _fontSize;
    private bool _enablePrettyJson;

    private const int MaxStyledValueChars = 64;

    private readonly JsonSyntaxHighlighter _jsonHighlighter = new();
    private readonly TokenResolutionEngine _tokenResolution;
    private readonly List<(int Start, int Length)> _urls = new(LogConstants.UrlMatchBufferCapacity);
    private readonly List<StyledHit> _hits = new(4);

    internal LogRenderer()
        : this(LogConstants.DefaultFontFamily, LogConstants.DefaultFontSize)
    {
    }

    public LogRenderer(string fontFamily, int fontSize, ScintillaOptions? options = null)
    {
        _fontFamily = string.IsNullOrWhiteSpace(fontFamily) ? LogConstants.DefaultFontFamily : fontFamily;
        _fontSize = fontSize <= 0 ? LogConstants.DefaultFontSize : fontSize;

        var resolvedOptions = options ?? new ScintillaOptions();
        _tokenResolution = new TokenResolutionEngine(
            resolvedOptions.Colorizer,
            resolvedOptions.Linkifier,
            resolvedOptions.EnrichmentErrorSink);
    }

    internal void SetPrettyJson(bool enabled)
    {
        Volatile.Write(ref _enablePrettyJson, enabled);
    }

    internal bool IsPrettyJsonEnabled => Volatile.Read(ref _enablePrettyJson);

    internal void ApplyThemeStyles(ScintillaNET.Scintilla scintilla, IScintillaTheme theme)
    {
        scintilla.Styles[ScintillaNET.Style.Default].Font = _fontFamily;
        scintilla.Styles[ScintillaNET.Style.Default].Size = _fontSize;
        scintilla.Styles[ScintillaNET.Style.Default].ForeColor = theme.Foreground;
        scintilla.Styles[ScintillaNET.Style.Default].BackColor = theme.Background;
        scintilla.StyleClearAll();

        scintilla.Styles[ScintillaNET.Style.LineNumber].ForeColor = theme.SecondaryText;
        scintilla.Styles[ScintillaNET.Style.LineNumber].BackColor = theme.Background;

        ApplySelectionStyles(scintilla, theme);
        ApplyLogLevelStyles(scintilla, theme);
        ApplySemanticStyles(scintilla, theme);
    }
    
    private static readonly Color DarkSelectionBackColor = Color.FromArgb(60, 80, 120);
    private static readonly Color DarkCaretColor = Color.FromArgb(220, 220, 220);
    private static readonly Color LightSelectionBackColor = Color.FromArgb(173, 214, 255);
    private static readonly Color LightCaretColor = Color.FromArgb(30, 30, 30);

    private static void ApplySelectionStyles(ScintillaNET.Scintilla scintilla, IScintillaTheme theme)
    {
        if (theme.IsDark)
        {
            scintilla.SelectionBackColor = DarkSelectionBackColor;
            scintilla.CaretForeColor = DarkCaretColor;
        }
        else
        {
            scintilla.SelectionBackColor = LightSelectionBackColor;
            scintilla.CaretForeColor = LightCaretColor;
        }
    }

    private static void ApplyLogLevelStyles(ScintillaNET.Scintilla scintilla, IScintillaTheme theme)
    {
        SetStyle(scintilla, LogLevel.Trace.ToStyleId(), theme.LevelTrace, theme.Background);
        SetStyle(scintilla, LogLevel.Debug.ToStyleId(), theme.LevelDebug, theme.Background);
        SetStyle(scintilla, LogLevel.Information.ToStyleId(), theme.LevelInformation, theme.Background);
        SetStyle(scintilla, LogLevel.Warning.ToStyleId(), theme.LevelWarning, theme.Background, bold: true);
        SetStyle(scintilla, LogLevel.Error.ToStyleId(), theme.LevelError, theme.Background, bold: true);
        SetStyle(scintilla, LogLevel.Critical.ToStyleId(), theme.LevelCritical, theme.Background, bold: true);
    }

    private static void ApplySemanticStyles(ScintillaNET.Scintilla scintilla, IScintillaTheme theme)
    {
        SetStyle(scintilla, SemanticStyle.Text.ToStyleId(), theme.Foreground, theme.Background);
        SetStyle(scintilla, SemanticStyle.SecondaryText.ToStyleId(), theme.SecondaryText, theme.Background);
        SetStyle(scintilla, SemanticStyle.Punctuation.ToStyleId(), theme.Punctuation, theme.Background);
        SetStyle(scintilla, SemanticStyle.JsonKey.ToStyleId(), theme.JsonKey, theme.Background, bold: true);
        SetStyle(scintilla, SemanticStyle.JsonString.ToStyleId(), theme.JsonString, theme.Background);
        SetStyle(scintilla, SemanticStyle.JsonNumber.ToStyleId(), theme.JsonNumber, theme.Background);
        SetStyle(scintilla, SemanticStyle.JsonBoolean.ToStyleId(), theme.JsonBoolean, theme.Background, bold: true);
        SetStyle(scintilla, SemanticStyle.JsonNull.ToStyleId(), theme.JsonNull, theme.Background, bold: true);
        SetStyle(scintilla, SemanticStyle.JsonDateTime.ToStyleId(), theme.JsonDateTime, theme.Background);
        SetStyle(scintilla, SemanticStyle.TokenLink.ToStyleId(), theme.LinkForeground, theme.Background, bold: true);

        SetStyle(scintilla, SemanticStyle.TokenClassified.ToStyleId(), theme.LinkForeground, theme.Background, bold: true);
    }

    private static void SetStyle(ScintillaNET.Scintilla scintilla, int styleId, Color foreColor, Color backColor, bool bold = false)
    {
        scintilla.Styles[styleId].ForeColor = foreColor;
        scintilla.Styles[styleId].BackColor = backColor;
        scintilla.Styles[styleId].Bold = bold;
    }

    /// <summary>
    /// Builds render segments for one message. Works on the UTF-8 text throughout: colorizers and linkifiers
    /// take UTF-8 too, so decoding to a string would only have to be undone for every token.
    /// </summary>
    public void BuildSegments(LogEntry entry, IList<RenderSegment> segments)
    {
        segments.Clear();
        if (entry.Message.Array is null || entry.Message.Count <= 0)
            return;

        var line = new ReadOnlyMemory<byte>(entry.Message.Array, entry.Message.Offset, entry.Message.Count);
        var prefix = LogPrefixParser.TryParse(line.Span);
        var bodyStart = 0;
        if (prefix.Found)
        {
            bodyStart = prefix.PrefixByteLength;
            LogPrefixParser.BuildPrefixSegments(prefix, line.Span.Slice(0, bodyStart), entry.Level, segments);
        }

        var body = line.Span.Slice(bodyStart);
        _hits.Clear();
        CollectUrlHits(body);
        CollectParameterHits(body, entry);
        SortHitsByStart();
        AppendBody(new RenderContext(line.Slice(bodyStart), entry), body, segments);
    }

    /// <summary>Builds render segments for pre-formatted UTF-8 JSON (syntax highlighting only).</summary>
    public void BuildJsonSegments(ReadOnlySpan<byte> json, IList<RenderSegment> segments)
    {
        segments.Clear();
        _jsonHighlighter.AppendJsonStyledSegments(json, segments);
    }

    /// <summary>URLs go first, so that a number inside a URL does not split the link.</summary>
    private void CollectUrlHits(ReadOnlySpan<byte> body)
    {
        UrlScanner.FindAll(body, _urls);
        foreach (var (start, length) in _urls)
        {
            if (UrlScanner.TryCreateLink(body.Slice(start, length), out var link))
                _hits.Add(new StyledHit(start, new RenderSegment(length, SemanticStyle.TokenLink, link, isLink: true)));
        }
    }

    private void CollectParameterHits(ReadOnlySpan<byte> body, LogEntry entry)
    {
        if (entry.GetParameterSpans() is not { } spans)
            return;

        foreach (var span in spans)
            FindParameterHits(body, span);
    }

    private void FindParameterHits(ReadOnlySpan<byte> body, in ParameterSpan span)
    {
        var value = span.FormattedValue;
        if (span.SemanticStyle == SemanticStyle.Text || string.IsNullOrEmpty(value) || value.Length > MaxStyledValueChars)
            return;

        Span<byte> buffer = stackalloc byte[MaxStyledValueChars * 3];
        var needle = buffer.Slice(0, Encoding.UTF8.GetBytes(value.AsSpan(), buffer));

        var position = 0;
        while (position <= body.Length - needle.Length)
        {
            var found = body.Slice(position).IndexOf(needle);
            if (found < 0)
                return;

            var start = position + found;
            position = start + needle.Length;
            if (IsWholeToken(body, start, position))
                _hits.Add(new StyledHit(start, new RenderSegment(needle.Length, span.SemanticStyle)));
        }
    }

    private static bool IsWholeToken(ReadOnlySpan<byte> body, int start, int end) =>
        (start == 0 || RenderingChars.IsTokenSeparator(body[start - 1]))
        && (end == body.Length || RenderingChars.IsTokenSeparator(body[end]));

    /// <summary>A stable sort, so that a URL stays ahead of a parameter at the same place. There are rarely more than a few.</summary>
    private void SortHitsByStart()
    {
        for (var i = 1; i < _hits.Count; i++)
        {
            var hit = _hits[i];
            var j = i - 1;
            while (j >= 0 && _hits[j].Start > hit.Start)
            {
                _hits[j + 1] = _hits[j];
                j--;
            }
            _hits[j + 1] = hit;
        }
    }

    private void AppendBody(in RenderContext context, ReadOnlySpan<byte> body, IList<RenderSegment> segments)
    {
        var cursor = 0;
        foreach (var hit in _hits)
        {
            if (hit.Start < cursor)
                continue;

            AppendTokens(context, body.Slice(cursor, hit.Start - cursor), segments);
            segments.Add(hit.Segment);
            cursor = hit.Start + hit.Segment.Utf8Length;
        }

        AppendTokens(context, body.Slice(cursor), segments);
    }

    private void AppendTokens(in RenderContext context, ReadOnlySpan<byte> text, IList<RenderSegment> segments)
    {
        if (text.IsEmpty)
            return;

        if (!_tokenResolution.HasEnrichers)
        {
            segments.Add(new RenderSegment(text.Length, SemanticStyle.Text));
            return;
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var separator = RenderingChars.IndexOfTokenSeparator(text.Slice(cursor));
            var tokenLength = separator < 0 ? text.Length - cursor : separator;
            if (tokenLength > 0)
                segments.Add(ResolveToken(context, text.Slice(cursor, tokenLength)));

            if (separator < 0)
                return;

            segments.Add(new RenderSegment(1, SemanticStyle.Punctuation));
            cursor += tokenLength + 1;
        }
    }

    private RenderSegment ResolveToken(in RenderContext context, ReadOnlySpan<byte> token) =>
        _tokenResolution.TryResolve(context, token, out var segment)
            ? segment
            : new RenderSegment(token.Length, SemanticStyle.Text);

    private readonly record struct StyledHit(int Start, RenderSegment Segment);
}
