using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
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
    private readonly bool _enableTokenProcessing;

    private readonly JsonSyntaxHighlighter _jsonHighlighter = new();
    private readonly DisplayValueFormatter _displayFormatter;

    public LogRenderer()
        : this(LogConstants.DefaultFontFamily, LogConstants.DefaultFontSize)
    {
    }

    public LogRenderer(string fontFamily, int fontSize, ScintillaOptions? options = null)
    {
        _fontFamily = string.IsNullOrWhiteSpace(fontFamily) ? LogConstants.DefaultFontFamily : fontFamily;
        _fontSize = fontSize <= 0 ? LogConstants.DefaultFontSize : fontSize;

        var resolvedOptions = options ?? new ScintillaOptions();
        _enablePrettyJson = false;
        _enableTokenProcessing = resolvedOptions.Colorizer is not null ||
                                 resolvedOptions.Linkifier is not null;

        var tokenResolution = new TokenResolutionEngine(
            resolvedOptions.Colorizer,
            resolvedOptions.Linkifier,
            resolvedOptions.EnrichmentErrorSink);

        _displayFormatter = new DisplayValueFormatter(tokenResolution);
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

        ApplyLogLevelStyles(scintilla, theme);
        ApplySemanticStyles(scintilla, theme);
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
        SetStyle(scintilla, StyleMapper.LinkHotspotStyleId, theme.LinkForeground, theme.Background, bold: true);
        SetStyle(scintilla, SemanticStyle.TokenClassified.ToStyleId(), theme.LinkForeground, theme.Background, bold: true);
    }

    private static void SetStyle(ScintillaNET.Scintilla scintilla, int styleId, Color foreColor, Color backColor, bool bold = false)
    {
        scintilla.Styles[styleId].ForeColor = foreColor;
        scintilla.Styles[styleId].BackColor = backColor;
        scintilla.Styles[styleId].Bold = bold;
    }

    /// <summary>
    /// Builds render segments for the text prefix + message portion of a log entry.
    /// JSON lines (when pretty-JSON is enabled) are styled separately via <see cref="BuildJsonSegments"/>.
    /// </summary>
    public void BuildSegments(LogEntry entry, IList<RenderSegment> segments)
    {
        segments.Clear();
        if (entry.Message.Array is null || entry.Message.Count <= 0)
            return;

        var utf8Slice = new ReadOnlyMemory<byte>(entry.Message.Array, entry.Message.Offset, entry.Message.Count);
        var hasAdvancedStyling = _enableTokenProcessing ||
                                 entry.Properties.ContainsKey(LogConstants.ParameterSpans);

        if (!hasAdvancedStyling)
        {
            BuildPlainSegments(entry, utf8Slice, segments);
            return;
        }

        BuildStyledSegments(entry, utf8Slice, segments);
    }

    /// <summary>
    /// Builds render segments for a pre-formatted JSON string (syntax highlighting only).
    /// </summary>
    public void BuildJsonSegments(string jsonText, IList<RenderSegment> segments)
    {
        segments.Clear();
        if (string.IsNullOrEmpty(jsonText))
            return;

        _jsonHighlighter.AppendJsonStyledSegments(jsonText, segments);
    }

    private static void BuildPlainSegments(LogEntry entry, ReadOnlyMemory<byte> utf8Slice, IList<RenderSegment> segments)
    {
        var prefixResult = LogPrefixParser.TryParse(utf8Slice.Span);
        if (prefixResult.Found)
            LogPrefixParser.BuildPrefixSegments(prefixResult, utf8Slice.Span, entry.Level, segments);
        else
            segments.Add(new RenderSegment(entry.Message.Count, SemanticStyle.Text));
    }

    private void BuildStyledSegments(LogEntry entry, ReadOnlyMemory<byte> utf8Slice, IList<RenderSegment> segments)
    {
        var (renderedUtf8, renderedMessage, prefixResult) = ParseMessageWithPrefix(entry, utf8Slice);

        if (prefixResult.Found)
            LogPrefixParser.BuildPrefixSegments(
                prefixResult,
                utf8Slice.Span.Slice(0, prefixResult.PrefixByteLength),
                entry.Level,
                segments);

        var renderContext = new RenderContext(renderedUtf8, entry.Properties);
        _displayFormatter.AppendSegments(renderContext, renderedMessage, segments);
    }

    private static (ReadOnlyMemory<byte> RenderedUtf8, string RenderedMessage, ParsedPrefix PrefixResult) ParseMessageWithPrefix(LogEntry entry, ReadOnlyMemory<byte> utf8Slice)
    {
        var prefixResult = LogPrefixParser.TryParse(utf8Slice.Span);

        if (prefixResult.Found)
        {
            var renderedUtf8 = utf8Slice.Slice(prefixResult.PrefixByteLength);
            var renderedMessage = renderedUtf8.Length > 0
                ? Encoding.UTF8.GetString(
                    entry.Message.Array!,
                    entry.Message.Offset + prefixResult.PrefixByteLength,
                    entry.Message.Count - prefixResult.PrefixByteLength)
                : string.Empty;
            return (renderedUtf8, renderedMessage, prefixResult);
        }

        var fullMessage = Encoding.UTF8.GetString(entry.Message.Array!, entry.Message.Offset, entry.Message.Count);
        return (utf8Slice, fullMessage, prefixResult);
    }
}
