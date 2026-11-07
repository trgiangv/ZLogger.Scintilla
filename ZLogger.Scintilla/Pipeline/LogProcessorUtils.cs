using System.Text.Json;
using LookupEngine;
using LookupEngine.Abstractions;
using LookupEngine.Abstractions.Enums;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Pipeline;

internal static class LogProcessorUtils
{
    extension(Utf8JsonWriter writer)
    {
        internal void WriteDecomposedJson(DecomposedObject decomposed, int depth, int maxDepth, int maxItems)
        {
            writer.WriteStartObject();
            writer.WriteString("$type", decomposed.TypeName);

            var decomposedProperties = decomposed.Members.Where(m => (m.MemberAttributes & MemberAttributes.Property) != 0);
            foreach (var member in decomposedProperties)
            {
                writer.WritePropertyName(member.Name);
                writer.WriteDecomposedValue(member.Value, depth, maxDepth, maxItems);
            }

            writer.WriteEndObject();
        }

        private void WriteDecomposedValue(DecomposedValue dv, int depth, int maxDepth, int maxItems)
        {
            var raw = dv.RawValue;

            if (raw is null)
            {
                writer.WriteNullValue(); return;
            }
            if (depth >= maxDepth || ParameterSpan.IsLeafType(raw.GetType()))
            {
                writer.WriteJsonValue(raw); return;
            }
            if (raw is System.Collections.IEnumerable enumerable)
            {
                writer.WriteEnumerable(enumerable, depth, maxDepth, maxItems); return;
            }

            try
            {
                var child = LookupComposer.Decompose(raw, DecomposeOptions.Default);
                if (child.Members.Count > 0)
                {
                    writer.WriteDecomposedJson(child, depth + 1, maxDepth, maxItems); return;
                }
            }
            catch
            {
                // A value that cannot be decomposed is written as a plain value below.
            }

            writer.WriteJsonValue(raw);
        }

        internal void WriteEnumerable(System.Collections.IEnumerable enumerable, int depth, int maxDepth, int maxItems)
        {
            writer.WriteStartArray();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= maxItems) break;
                writer.WriteElementValue(item, depth, maxDepth, maxItems);
            }
            writer.WriteEndArray();
        }

        private void WriteElementValue(object? item, int depth, int maxDepth, int maxItems)
        {
            if (item is null)
            {
                writer.WriteNullValue(); return;
            }
            if (ParameterSpan.IsLeafType(item.GetType()))
            {
                writer.WriteJsonValue(item); return;
            }

            try
            {
                var child = LookupComposer.Decompose(item, DecomposeOptions.Default);
                writer.WriteDecomposedJson(child, depth + 1, maxDepth, maxItems);
            }
            catch
            {
                writer.WriteJsonValue(item);
            }
        }

        internal void WriteJsonValue(object value)
        {
            if (value is IConvertible)
            {
                try
                {
                    JsonSerializer.Serialize(writer, value, value.GetType());
                    return;
                }
                catch
                {
                    // Falls back to the value's text below.
                }
            }

            writer.WriteStringValue(value.ToString());
        }
    }

    internal static IReadOnlyDictionary<string, object?> BuildProperties(
        this IZLoggerEntry entry,
        ParameterSpan[]? paramSpans,
        byte[]? jsonBytes)
    {
        var scopeState = entry.LogInfo.ScopeState;
        var scopeCount = scopeState is { Properties.IsEmpty: false } ? scopeState.Properties.Length : 0;
        var totalCapacity = scopeCount
                            + (paramSpans is { Length: > 0 } ? 1 : 0)
                            + (jsonBytes is not null ? 1 : 0);

        if (totalCapacity == 0)
            return LogEntry.EmptyProperties;

        var properties = new Dictionary<string, object?>(totalCapacity, StringComparer.Ordinal);

        if (scopeCount > 0)
            AddScopeProperties(scopeState!, properties);

        if (paramSpans is { Length: > 0 })
            properties[LogConstants.ParameterSpans] = paramSpans;

        if (jsonBytes is not null)
            properties[LogConstants.JsonBytes] = jsonBytes;

        return properties.Count == 0 ? LogEntry.EmptyProperties : properties;
    }

    private static void AddScopeProperties(LogScopeState scopeState, Dictionary<string, object?> properties)
    {
        var scopeProperties = scopeState.Properties;
        foreach (var item in scopeProperties)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
                properties[item.Key] = item.Value;
        }
    }
}