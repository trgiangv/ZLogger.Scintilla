using System.Text.Json;

namespace ZLogger.Scintilla.Public;

/// <summary>
/// Allows host apps to handle parameter types that the default
/// <c>LookupComposer</c> decomposition cannot safely process
/// (e.g. Python objects requiring a GIL lock).
///
/// <para>
/// <see cref="CanSerialize"/> is called with CLR <see cref="Type"/> metadata only —
/// no instance method calls — so it is always safe to invoke on any thread.
/// </para>
/// <para>
/// <see cref="WriteJson"/> and <see cref="FormatText"/> may touch the object instance
/// and are responsible for any required thread synchronization (e.g. acquiring the GIL).
/// </para>
/// </summary>
public interface ICustomSerializer
{
    /// <summary>
    /// Returns <c>true</c> if this serializer handles the given type.
    /// Must be safe to call on any thread without touching the object instance.
    /// </summary>
    bool CanSerialize(Type type);

    /// <summary>
    /// Writes the object as a complete JSON value (object, array, or primitive)
    /// to <paramref name="writer"/>. The caller has already written the property name.
    /// </summary>
    void WriteJson(Utf8JsonWriter writer, object value, int maxDepth, int maxItems);

    /// <summary>
    /// Returns a safe string representation for text formatting and parameter spans.
    /// </summary>
    string? FormatText(object? value);
}
