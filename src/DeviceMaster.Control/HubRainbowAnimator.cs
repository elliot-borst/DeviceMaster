using DeviceMaster.Core.Lighting;
using DeviceMaster.Devices.CorsairLink;

namespace DeviceMaster.Control;

/// <summary>
/// Streams rainbow frames to Corsair iCUE LINK hubs. The hubs have no built-in effects in
/// software mode, so frames go out from this background thread at ~15 fps while the control
/// loop keeps its 1 Hz duty/telemetry cadence. Each frame is one atomic hub operation
/// (<see cref="LinkHub.ApplyLedColors"/> holds the hub's I/O lock), so frames slot in between
/// the loop's own hub operations without interleaving their commands.
/// Used by the desktop <see cref="ControlLoop"/> and the headless loop alike.
/// </summary>
public sealed class HubRainbowAnimator : IDisposable
{
    private const int FrameMs = 66;
    private const int FailureBackoffMs = 3_000;

    private readonly Func<IReadOnlyList<LinkHub>> _hubs;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, long> _retryAt = [];
    private readonly Thread _thread;
    private volatile Settings? _settings;

    private sealed record Settings(int Speed, int Brightness);

    /// <param name="hubs">The hubs open right now (read every frame; the loop owns the list).</param>
    public HubRainbowAnimator(Func<IReadOnlyList<LinkHub>> hubs, Action<string>? log)
    {
        _hubs = hubs;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMaster rainbow", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>True while frames are being streamed (the loop then leaves hub colours alone).</summary>
    public bool Active => _settings is not null;

    /// <summary>Start, retune or stop the rainbow from the current settings (called every loop tick).</summary>
    public void Update(ControlSettings settings) =>
        _settings = settings.RainbowActive ? new Settings(settings.RgbSpeed, Math.Clamp(settings.RgbBrightness, 0, 100)) : null;

    private void Run()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            if (_settings is { } settings)
            {
                var phase = RainbowEffect.PhaseAt(started, settings.Speed);
                foreach (var hub in _hubs())
                {
                    if (started < _retryAt.GetValueOrDefault(hub.SerialNumber) || hub.TotalLeds == 0)
                    {
                        continue;
                    }

                    try
                    {
                        hub.ApplyLedColors(RainbowEffect.Frame(hub.TotalLeds, phase, settings.Brightness));
                    }
                    catch (Exception ex)
                    {
                        // the loop handles dropped/reopened hubs; just back off this one for a moment
                        _retryAt[hub.SerialNumber] = started + FailureBackoffMs;
                        _log?.Invoke($"rainbow: hub {hub.SerialNumber[..Math.Min(8, hub.SerialNumber.Length)]}… frame failed ({ex.Message}) — retrying in 3 s");
                    }
                }
            }

            // idle: check for a newly enabled rainbow four times a second
            var wait = FrameMs - (int)(Environment.TickCount64 - started);
            token.WaitHandle.WaitOne(_settings is null ? 250 : Math.Max(wait, 5));
        }
    }

    public void Dispose()
    {
        _settings = null;
        _cts.Cancel();
        _thread.Join(1_000);
        _cts.Dispose();
    }
}
