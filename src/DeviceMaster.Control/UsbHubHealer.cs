using DeviceMaster.Core.Discovery;

namespace DeviceMaster.Control;

/// <summary>
/// Watches USB hub ports and re-enumerates a hub whose ports failed to enumerate (#101): a
/// self-powered hub can come up after a reboot with every downstream port stuck at "Device
/// Descriptor Request Failed", and nothing behind it works until its cable is replugged. This
/// does that replug in software — it cycles the PC-side port of the outermost hub in the chain
/// (falling back to disabling and re-enabling that hub). Windows only; needs elevation to act.
/// </summary>
public sealed class UsbHubHealer : IDisposable
{
    private const int ScanMs = 5_000;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly UsbHubHealPolicy _policy = new();
    private readonly Thread _thread;

    public UsbHubHealer(Action<string>? log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMaster USB hub healer" };
        _thread.Start();
    }

    private void Run()
    {
        while (!_cts.Token.WaitHandle.WaitOne(ScanMs))
        {
            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"usb heal: scan failed: {ex.Message}");
            }
        }
    }

    private void Scan()
    {
        var ports = UsbHubPorts.ReadAll();
        var failedHubs = ports.Where(p => p.Failed).Select(p => p.HubInstanceId).Distinct().ToList();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // one cycle per upstream port per round
        foreach (var decision in _policy.Next(failedHubs, Environment.TickCount64))
        {
            var failed = ports.Where(p => p.Failed && p.HubInstanceId.Equals(decision.HubInstanceId, StringComparison.OrdinalIgnoreCase)).ToList();
            var what = $"{decision.HubInstanceId} ({failed.Count} port(s) failed to enumerate: {string.Join(", ", failed.Select(p => $"{p.Port} {p.Status}"))})";
            if (decision.GiveUp)
            {
                _log?.Invoke($"usb heal: giving up on {what} after {decision.Attempt} attempt(s) — replug the hub's cable");
                continue;
            }

            if (UsbHubPorts.UpstreamPortOf(decision.HubInstanceId) is { } up)
            {
                if (done.Add($"{up.HubInstanceId}#{up.Port}"))
                {
                    Heal(what, decision.Attempt, up.HubInstanceId, up.Port, up.TopHubInstanceId);
                }
            }
            else if (UsbHubPorts.IsRootHub(decision.HubInstanceId))
            {
                // a device straight on the PC's port: re-enumerate just that port
                foreach (var port in failed.Where(p => done.Add($"{p.HubInstanceId}#{p.Port}")))
                {
                    Heal(what, decision.Attempt, port.HubInstanceId, port.Port, null);
                }
            }
        }
    }

    private void Heal(string what, int attempt, string hub, int port, string? topHub)
    {
        _log?.Invoke($"usb heal: {what} — attempt {attempt}: cycling port {port} of {hub}{(topHub is null ? "" : $" (re-plugs {topHub})")}");
        try
        {
            UsbHubPorts.CyclePort(hub, port);
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"usb heal: port cycle failed: {ex.Message}");
        }

        if (topHub is null)
        {
            return;
        }

        try
        {
            UsbDeviceRestarter.Restart(topHub, settleMs: 2000);
            _log?.Invoke($"usb heal: restarted {topHub} instead");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"usb heal: hub restart failed too: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}
