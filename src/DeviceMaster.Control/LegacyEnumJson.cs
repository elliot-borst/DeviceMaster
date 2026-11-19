using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceMaster.Control;

/// <summary>
/// Reads an enum leniently — by name (any case) or number — and maps a value this build no longer
/// has (e.g. the removed coolant curve source or pump screen metrics in an older config, a fleet
/// state file or a push from an older hub) to a safe fallback instead of failing the whole document.
/// Writes the name when the serializer is set up for string enums, the number otherwise, so the
/// JSON shape every consumer sees is unchanged.
/// </summary>
/// <remarks>
/// Serializer options' converters take precedence over a type-level [JsonConverter], so the
/// properties that hold these enums carry the attribute themselves.
/// </remarks>
public abstract class LenientEnumConverter<T>(T fallback) : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => Parse(reader.GetString(), out var value) ? value : fallback,
            JsonTokenType.Number => reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(T), n) ? (T)(object)n : fallback,
            _ => throw new JsonException($"expected a {typeof(T).Name} name or number"),
        };

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (options.Converters.Any(c => c is JsonStringEnumConverter))
        {
            writer.WriteStringValue(value.ToString());
        }
        else
        {
            writer.WriteNumberValue(Convert.ToInt32(value));
        }
    }

    /// <summary>True for a current member's name (not a bare number in a string).</summary>
    protected static bool Parse(string? text, out T value) =>
        Enum.TryParse(text, ignoreCase: true, out value) && !int.TryParse(text, out _) && Enum.IsDefined(value);

    /// <summary>Parses a JSON value; false (with the fallback) when it isn't a current member.</summary>
    protected static bool TryParse(JsonElement element, T fallbackValue, out T value)
    {
        value = fallbackValue;
        switch (element.ValueKind)
        {
            case JsonValueKind.String when Parse(element.GetString(), out var named):
                value = named;
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out var n) && Enum.IsDefined(typeof(T), n):
                value = (T)(object)n;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>A removed source (the old "Coolant", 0) reads as <see cref="CurveSource.Hottest"/>.</summary>
public sealed class CurveSourceJsonConverter() : LenientEnumConverter<CurveSource>(CurveSource.Hottest)
{
    public static bool TryParse(JsonElement element, out CurveSource source) => TryParse(element, CurveSource.Hottest, out source);
}

/// <summary>A removed metric (coolant, pump rpm/duty) reads as <see cref="LcdMetric.CpuTemp"/>.</summary>
public sealed class LcdMetricJsonConverter() : LenientEnumConverter<LcdMetric>(LcdMetric.CpuTemp);
