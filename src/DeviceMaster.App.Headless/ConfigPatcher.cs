using System.Text.Json;
using DeviceMaster.Core.Safety;
using DeviceMaster.Control;

namespace DeviceMaster.App.Headless;

/// <summary>
/// Applies a web-dashboard patch to a loaded <see cref="HeadlessConfig"/>. The patch is a
/// flat JSON object; only whitelisted control fields are accepted, everything else is
/// ignored so the dashboard can never clobber transport/GPU settings. Values are clamped
/// before the caller persists the config (the loop hot-reloads it on the next tick).
/// Returns the list of applied changes (empty = nothing to do); the caller persists only
/// when the list is non-empty.
/// </summary>
public static class ConfigPatcher
{
    public static List<string> ApplyPatch(HeadlessConfig cfg, string patchJson, out bool invalid)
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
                        cfg.Control.Mode = (ControlMode)mode;
                        applied.Add($"mode={(int)cfg.Control.Mode}");
                    }
                    break;
                case "fanDuty":
                    if (TryInt(prop.Value, 0, 100, out var duty))
                    {
                        cfg.Control.ManualDutyPercent = duty;
                        applied.Add($"fanDuty={duty}");
                    }
                    break;
                case "pumpDuty":
                    // hard floor — a low pump duty can damage the loop
                    if (TryInt(prop.Value, SafetyLimits.PumpMinimumDutyPercent, 100, out var pump))
                    {
                        cfg.Control.PumpDutyPercent = pump;
                        applied.Add($"pumpDuty={pump}");
                    }
                    break;
                case "source":
                    if (TryEnum(prop.Value, out var source) && Enum.IsDefined(typeof(CurveSource), source))
                    {
                        cfg.Control.Source = (CurveSource)source;
                        applied.Add($"source={(int)cfg.Control.Source}");
                    }
                    break;
                case "rgbEnabled":
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        cfg.Control.RgbEnabled = prop.Value.GetBoolean();
                        applied.Add($"rgbEnabled={cfg.Control.RgbEnabled}");
                    }
                    break;
                case "rgbR":
                    if (TryInt(prop.Value, 0, 255, out var r)) { cfg.Control.RgbR = r; applied.Add($"rgbR={r}"); }
                    break;
                case "rgbG":
                    if (TryInt(prop.Value, 0, 255, out var g)) { cfg.Control.RgbG = g; applied.Add($"rgbG={g}"); }
                    break;
                case "rgbB":
                    if (TryInt(prop.Value, 0, 255, out var b)) { cfg.Control.RgbB = b; applied.Add($"rgbB={b}"); }
                    break;
                case "rgbBrightness":
                    if (TryInt(prop.Value, 0, 100, out var rb)) { cfg.Control.RgbBrightness = rb; applied.Add($"rgbBrightness={rb}"); }
                    break;
                case "lcdScreens":
                    if (TryEnum(prop.Value, out var lcd) && Enum.IsDefined(typeof(LcdMode), lcd))
                    {
                        cfg.Control.LcdScreens = (LcdMode)lcd;
                        applied.Add($"lcdScreens={(int)cfg.Control.LcdScreens}");
                    }
                    break;
                case "lcdBrightness":
                    if (TryInt(prop.Value, 0, 100, out var lb)) { cfg.Control.LcdBrightness = lb; applied.Add($"lcdBrightness={lb}"); }
                    break;
                case "pumpScreenMetric":
                    if (TryEnum(prop.Value, out var metric) && Enum.IsDefined(typeof(LcdMetric), metric))
                    {
                        cfg.Control.PumpScreenMetric = (LcdMetric)metric;
                        applied.Add($"pumpScreenMetric={(int)cfg.Control.PumpScreenMetric}");
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

    private static bool TryEnum(JsonElement value, out int result)
    {
        result = -1;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n)
            && n >= 0
            && (result = n) switch { _ => true };
    }
}
