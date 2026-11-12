using DeviceMaster.Devices.EneRgb;
using DeviceMaster.Platform.Linux;

namespace DeviceMaster.App.Headless;

/// <summary>
/// RGB DRAM: ENE controllers on the chipset SMBus (AMD PIIX4 / Intel I801) through the
/// kernel's SMBus ioctls. Detection is the shared <see cref="EneDramScan"/> (read-only
/// fingerprint before any write), scanned once per loop start like the GPU's ENE chip.
/// Same color/persist policy as the Windows loop: volatile apply, flash save once settled.
/// </summary>
public sealed partial class HeadlessLoop
{
    private readonly List<LinuxSmBus> _ramBuses = [];
    private IReadOnlyList<EneRgbDevice> _ramRgb = [];
    private bool _ramRgbScanned;
    private string? _appliedRamRgb;
    private string? _ramPersistedKey;
    private long _ramRgbRetryAt;
    private long _ramPersistDue;
    private string _ramRgbSummary = "RAM: not scanned";

    private void EnsureRamRgb(HeadlessConfig cfg)
    {
        if (_ramRgbScanned || !cfg.RamRgbEnabled)
        {
            return;
        }

        _ramRgbScanned = true;
        var adapters = LinuxSmBus.ChipsetAdapters();
        if (adapters.Count == 0)
        {
            _ramRgbSummary = "RAM: no chipset SMBus adapter (is i2c-dev loaded and /dev mounted?)";
            _log($"headless: {_ramRgbSummary}");
            return;
        }

        var found = new List<EneRgbDevice>();
        var errors = new List<string>();
        foreach (var (path, name) in adapters)
        {
            LinuxSmBus bus;
            try
            {
                bus = new LinuxSmBus(path, name);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
                continue;
            }

            try
            {
                var devices = EneDramScan.FindControllers(bus, _log);
                if (devices.Count > 0)
                {
                    found.AddRange(devices);
                    _ramBuses.Add(bus);
                    continue;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }

            bus.Dispose();
        }

        _ramRgb = found;
        var scanned = string.Join(", ", adapters.Select(a => a.DevicePath));
        _ramRgbSummary = found.Count > 0
            ? $"RAM: {found.Count} ENE controller(s): "
                + string.Join(", ", found.Select(d => $"0x{d.Address:X2} on {d.BusName} '{d.Version}' {d.LedCount} LEDs"))
            : $"RAM: no ENE controller on {scanned}";
        if (errors.Count > 0)
        {
            _ramRgbSummary += $" (errors: {string.Join("; ", errors)})";
        }

        _log($"headless: {_ramRgbSummary}");
    }

    private void ApplyRamRgb(HeadlessConfig cfg, List<string> warnings)
    {
        if (_ramRgb.Count == 0 || !cfg.RamRgbEnabled)
        {
            return;
        }

        var (r, g, b) = RgbTarget(cfg);
        var rainbow = cfg.Control.RainbowActive;
        var key = rainbow ? $"rainbow:{cfg.Control.RgbSpeed}" : $"{r},{g},{b}";
        if (_appliedRamRgb != key && Environment.TickCount64 >= _ramRgbRetryAt)
        {
            try
            {
                foreach (var ram in _ramRgb)
                {
                    if (rainbow)
                    {
                        ram.ApplyRainbow(cfg.Control.RgbSpeed, persist: false);
                    }
                    else
                    {
                        ram.ApplyStaticColor(r, g, b, persist: false);
                    }
                }

                _appliedRamRgb = key;
                _ramRgbRetryAt = 0;
                _log($"RGB applied on {_ramRgb.Count} RAM ENE controller(s): {key}");
            }
            catch (Exception ex)
            {
                _ramRgbRetryAt = Environment.TickCount64 + 30_000;
                _appliedRamRgb = null;
                warnings.Add($"RAM RGB failed: {ex.Message} (retry in 30 s)");
            }
        }

        // volatile chips: persist to flash once the color has settled (flash endurance)
        if (_appliedRamRgb == key && _ramPersistedKey != key)
        {
            if (_ramPersistDue == 0)
            {
                _ramPersistDue = Environment.TickCount64 + RgbPersistSettleMs;
            }
            else if (Environment.TickCount64 >= _ramPersistDue)
            {
                try
                {
                    foreach (var ram in _ramRgb)
                    {
                        ram.Persist();
                    }

                    _ramPersistedKey = key;
                    _ramPersistDue = 0;
                    _log("RAM ENE RGB persisted to flash");
                }
                catch (Exception ex)
                {
                    // don't retry the flash save every settle window — it is endurance-limited
                    _ramPersistedKey = key;
                    _ramPersistDue = 0;
                    _log($"RAM ENE persist failed: {ex.Message}");
                }
            }
        }
        else if (_appliedRamRgb != key)
        {
            _ramPersistDue = 0;
        }
    }

    private void ReleaseRamRgb()
    {
        foreach (var bus in _ramBuses)
        {
            try
            {
                bus.Dispose();
            }
            catch
            {
            }
        }

        _ramBuses.Clear();
        _ramRgb = [];
        _ramRgbScanned = false;
        _appliedRamRgb = null;
        _ramPersistedKey = null;
        _ramPersistDue = 0;
        _ramRgbSummary = "RAM: not scanned";
    }
}
