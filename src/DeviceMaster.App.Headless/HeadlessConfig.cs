using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceMaster.Control;

namespace DeviceMaster.App.Headless;

/// <summary>
/// Headless (Linux/container) configuration: the standard <see cref="ControlSettings"/> block
/// (same JSON shape as the Windows app) plus the Linux-specific bits. Loaded from
/// DEVICEMASTER_CONFIG (or the --config path) and re-read when the file changes, so the
/// container can be reconfigured live without a restart.
/// </summary>
public sealed class HeadlessConfig
{
    public ControlSettings Control { get; set; } = new()
    {
        Mode = ControlMode.Curve,
        Source = CurveSource.Coolant,
        RgbEnabled = true,
        RgbR = 128,
        RgbG = 0,
        RgbB = 255,
        LcdBrightness = 100,
    };

    /// <summary>PCI address of the GPU whose i2c bus carries the ENE RGB chip (e.g. "01:00.0"). Null = first NVIDIA adapter.</summary>
    public string? GpuPciAddress { get; set; }

    /// <summary>Explicit /dev/i2c-N for the ENE chip — wins over GpuPciAddress when set.</summary>
    public string? I2cDevice { get; set; }

    public string NvidiaSmiPath { get; set; } = "/usr/bin/nvidia-smi";

    /// <summary>
    /// Path to a file holding one nvidia-smi CSV row (same --query-gpu columns as
    /// <see cref="NvidiaSmiPath"/>), refreshed by an external process (e.g. a host cron).
    /// Used when nvidia-smi cannot run inside the container; rows older than
    /// <see cref="GpuSensorFileStaleSeconds"/> are ignored. Falls back to NvidiaSmiPath.
    /// </summary>
    public string? GpuSensorFile { get; set; }

    public int GpuSensorFileStaleSeconds { get; set; } = 120;

    /// <summary>Drive the GPU's ENE RGB chip (default on when the chip is found).</summary>
    public bool GpuRgbEnabled { get; set; } = true;

    /// <summary>
    /// Base URL of a local Strata LLM server (e.g. http://127.0.0.1:8081). When set, the Turzx
    /// dashboard shows its model, live speed and slots, and its GPU figures are used (read-only:
    /// only <c>GET /metrics</c>). Null = no LLM panel.
    /// </summary>
    public string? StrataUrl { get; set; }

    /// <summary>Env-style file holding the server's <c>API_KEY=</c> (mounted read-only); never logged.</summary>
    public string? StrataKeyFile { get; set; }

    /// <summary>Drive the motherboard's spinning fan headers (Nuvoton Super I/O via hwmon) with the fan duty.</summary>
    public bool MotherboardFans { get; set; } = true;

    /// <summary>Drive ENE RGB controllers on RAM sticks over the chipset SMBus (static colour / rainbow).</summary>
    public bool RamRgbEnabled { get; set; } = true;

    /// <summary>Drive the ASUS Aura mainboard LED controller (static colour / rainbow).</summary>
    public bool AuraEnabled { get; set; } = true;

    /// <summary>Name shown on the Turzx dashboard; empty = the machine name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Write a JSON status snapshot to this path every N seconds (0 = off). Great for external dashboards.</summary>
    public string? StatusFile { get; set; }

    /// <summary>
    /// Filesystems to report to the fleet display, as "path" or "label=path" (e.g. "System=/host",
    /// "Array=/mnt/user"). A container only sees what is mounted into it, so mount these read-only.
    /// </summary>
    public List<string> StoragePaths { get; set; } = [];

    /// <summary>
    /// Docker socket to report container counts from (mounted read-only, e.g. /var/run/docker.sock);
    /// null = not reported. Only for an agent that is already privileged on its host.
    /// </summary>
    public string? DockerSocket { get; set; }

    /// <summary>
    /// Folder shared with the host's single-purpose shutdown helper (#49), e.g. /power. A fleet
    /// "power off" writes a request file there; the helper runs the host's own clean shutdown.
    /// Null = this machine can't be powered off from the fleet.
    /// </summary>
    public string? ShutdownRequestDir { get; set; }

    public int StatusFileEverySeconds { get; set; } = 5;

    /// <summary>Packet-level tracing of hub traffic (noisy).</summary>
    public bool Trace { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("DEVICEMASTER_CONFIG") is { Length: > 0 } env
            ? env
            : "/config/config.json";

    public static HeadlessConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<HeadlessConfig>(File.ReadAllText(path), JsonOptions)
                    ?? new HeadlessConfig();
            }
        }
        catch
        {
            // corrupt config -> defaults (the loop logs this via its own logger)
        }

        return new HeadlessConfig();
    }

    public string Save() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes config JSON (same options as <see cref="Save"/>).</summary>
    public static HeadlessConfig? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HeadlessConfig>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
