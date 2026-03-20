using System;
using System.Collections.Generic;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Models;

/// <summary>
/// Typed metadata extracted from <c>IZLoggerEntry.GetParameterValue(i)</c>
/// in <see cref="ScintillaLogProcessor.Post"/>. Carries the formatted
/// string, its semantic style (derived from CLR type), and the original type
/// so downstream consumers (e.g. <see cref="ILinkifier"/>) can make
/// context-aware decisions.
/// </summary>
public readonly struct ParameterSpan
{
    private static readonly HashSet<Type> LeafTypes =
    [
        typeof(string), typeof(bool),
        typeof(int), typeof(long), typeof(short), typeof(byte),
        typeof(uint), typeof(ulong), typeof(ushort), typeof(sbyte),
        typeof(float), typeof(double), typeof(decimal),
        typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
        typeof(Guid), typeof(char),
        typeof(byte[]), typeof(ReadOnlyMemory<byte>), typeof(ArraySegment<byte>)
    ];

    public string FormattedValue { get; init; }
    public SemanticStyle SemanticStyle { get; init; }
    public Type? ClrType { get; init; }

    /// <summary>
    /// Returns <see langword="true"/> for types that are leaf values and should NOT be
    /// decomposed or treated as complex objects (primitives, enums, string, DateTime, Guid, etc.).
    /// </summary>
    public static bool IsLeafType(Type type)
        => type.IsPrimitive || type.IsEnum || LeafTypes.Contains(type);

    /// <summary>
    /// Checks whether any interpolation parameter in the given <paramref name="context"/>
    /// has the specified CLR type full name and formatted value matching <paramref name="formattedValue"/>.
    /// Use this from <see cref="ILinkifier"/> implementations to identify parameters by type
    /// without depending on internal property keys.
    /// </summary>
    public static bool HasParameter(RenderContext context, string clrTypeFullName, string formattedValue)
    {
        if (!context.Properties.TryGetValue(LogConstants.ParameterSpans, out var raw)
            || raw is not ParameterSpan[] spans)
            return false;

        foreach (var span in spans)
        {
            if (span.ClrType?.FullName == clrTypeFullName &&
                string.Equals(span.FormattedValue, formattedValue, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static SemanticStyle MapClrType(Type? type)
    {
        if (type is null) return SemanticStyle.JsonNull;

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Int32 or TypeCode.Int64 or TypeCode.Int16 or TypeCode.Byte or
            TypeCode.UInt32 or TypeCode.UInt64 or TypeCode.UInt16 or TypeCode.SByte or
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal
                => SemanticStyle.JsonNumber,
            TypeCode.Boolean
                => SemanticStyle.JsonBoolean,
            TypeCode.DateTime
                => SemanticStyle.JsonDateTime,
            _ => type == typeof(DateTimeOffset) ? SemanticStyle.JsonDateTime : SemanticStyle.Text
        };
    }
}
