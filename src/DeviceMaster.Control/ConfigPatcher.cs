using System.Text.Json;
using DeviceMaster.Core.Curves;
using DeviceMaster.Core.Lighting;
using DeviceMaster.Core.Safety;

namespace DeviceMaster.Control;

/// <summary>
/// Applies a web-dashboard patch to a machine's <see cref="ControlSettings"/>. The patch is a
/// flat JSON object; only whitelisted control fields are accepted, everything else is
/// ignored so the dashboard can never clobber transport/GPU settings. Values are clamped
/// before the caller persists the config (the loop hot-reloads it on the next tick).
/// Returns the list of applied changes (empty = nothing to do); the caller persists only
/// when the list is non-empty.
/// </summary>
public static class ConfigPatcher
{
    /// <summary>The same patch applied to any machine's settings (the fleet agent API uses it on Windows too).</summary>
    public static List<string> ApplyPatch(ControlSettings s, string patchJson, out bool invalid)
    {
        var applied = new List<string>();
        invalid = false;
        JsonElement patch;
        try
        {
            using var doc = JsonDocument.Parse(patchJson);
            patch = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            invalid = true;
            return applied;
        }

        if (patch.ValueKind != JsonValueKind.Object)
        {
            invalid = true;
            return applied;
        }

        foreach (var prop in patch.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "mode":
                    if (TryEnum(prop.Value, out var mode) && Enum.IsDefined(typeof(ControlMode), mode))
                    {
                        s.Mode = (ControlMode)mode;
                        applied.Add($"mode={(int)s.Mode}");
                    }
                    break;
                case "fanDuty":
                    if (TryInt(prop.Value, 0, 100, out var duty))
                    {
                        s.ManualDutyPercent = duty;
                        applied.Add($"fanDuty={duty}");
                    }
                    break;
                case "pumpDuty":
                    // hard floor — a low pump duty can damage the loop
                    if (TryInt(prop.Value, SafetyLimits.PumpMinimumDutyPercent, 100, out var pump))
                    {
                        s.PumpDutyPercent = pump;
                        applied.Add($"pumpDuty={pump}");
                    }
                    break;
                case "source":
                    // each source keeps its own curve: switching swaps the active curve too
                    if (TryEnum(prop.Value, out var source) && Enum.IsDefined(typeof(CurveSource), source)
                        && s.SetSource((CurveSource)source))
                    {
                        applied.Add($"source={(int)s.Source}");
                    }
                    break;
                case "curvePoints":
                    // [[tempC, duty], …] — repaired by the same rules as the desktop editor
                    if (TryCurve(prop.Value, out var points) && s.SetCurve(points))
                    {
                        applied.Add("curvePoints=" + string.Join(";", s.CurvePoints.Select(p => $"{p.TemperatureC:0}:{p.DutyPercent}")));
                    }
                    break;
                case "rgbEnabled":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        s.RgbEnabled = prop.Value.GetBoolean();
                        applied.Add($"rgbEnabled={s.RgbEnabled}");
                    }
                    break;
                case "rgbR":
                    if (TryInt(prop.Value, 0, 255, out var r)) { s.RgbR = r; applied.Add($"rgbR={r}"); }
                    break;
                case "rgbG":
                    if (TryInt(prop.Value, 0, 255, out var g)) { s.RgbG = g; applied.Add($"rgbG={g}"); }
                    break;
                case "rgbB":
                    if (TryInt(prop.Value, 0, 255, out var b)) { s.RgbB = b; applied.Add($"rgbB={b}"); }
                    break;
                case "rgbBrightness":
                    if (TryInt(prop.Value, 0, 100, out var rb)) { s.RgbBrightness = rb; applied.Add($"rgbBrightness={rb}"); }
                    break;
                case "rgbEffect":
                    if (TryEnum(prop.Value, out var effect) && Enum.IsDefined(typeof(RgbEffect), effect))
                    {
                        s.RgbEffect = (RgbEffect)effect;
                        if (s.RgbEffect == RgbEffect.Rainbow)
                        {
                            s.RgbOff = false; // "lights out" and rainbow are opposites
                        }

                        applied.Add($"rgbEffect={(int)s.RgbEffect}");
                    }
                    break;
                case "rgbSpeed":
                    if (TryInt(prop.Value, RainbowEffect.MinSpeed, RainbowEffect.MaxSpeed, out var speed))
                    {
                        s.RgbSpeed = speed;
                        applied.Add($"rgbSpeed={speed}");
                    }
                    break;
                case "lcdScreens":
                    if (TryEnum(prop.Value, out var lcd) && Enum.IsDefined(typeof(LcdMode), lcd))
                    {
                        s.LcdScreens = (LcdMode)lcd;
                        applied.Add($"lcdScreens={(int)s.LcdScreens}");
                    }
                    break;
                case "lcdBrightness":
                    if (TryInt(prop.Value, 0, 100, out var lb)) { s.LcdBrightness = lb; applied.Add($"lcdBrightness={lb}"); }
                    break;
                case "pumpScreenMetric":
                    if (TryEnum(prop.Value, out var metric) && Enum.IsDefined(typeof(LcdMetric), metric))
                    {
                        s.PumpScreenMetric = (LcdMetric)metric;
                        applied.Add($"pumpScreenMetric={(int)s.PumpScreenMetric}");
                    }
                    break;
                case "blackout":
                    // movie mode: same remember/black-out/restore as the desktop toggle
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && s.SetBlackout(prop.Value.GetBoolean()))
                    {
                        applied.Add($"blackout={s.BlackoutActive}");
                    }
                    break;
                default:
                    // unknown fields are ignored, never fatal
                    break;
            }
        }

        return applied;
    }

    private static bool TryInt(JsonElement value, int min, int max, out int result)
    {
        result = min;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n)
            && (result = Math.Clamp(n, min, max)) switch { _ => true };
    }

    private static bool TryCurve(JsonElement value, out List<CurvePoint> points)
    {
        points = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 8)
        {
            return false;
        }

        foreach (var pair in value.EnumerateArray())
        {
            // TryGet* throw on non-numbers, so check the kind first — a malformed patch is ignored, never fatal
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                || pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number
                || !pair[0].TryGetDouble(out var temp) || !pair[1].TryGetInt32(out var duty))
            {
                return false;
            }

            points.Add(new CurvePoint(temp, duty));
        }

        return true;
    }

        private static bool TryEnum(JsonElement value, out int result)
    {
        result = -1;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n)
            && n >= 0
            && (result = n) switch { _ => true };
    }
}
