using DeviceMaster.Core.Discovery;
using Serilog;

namespace DeviceMaster.App.Commands;

/// <summary><c>usbhubs</c> — every USB hub port and its state (read-only), flagging failed enumeration (#101).</summary>
internal static class UsbHubCommands
{
    public static int Run(string[] args)
    {
        foreach (var hub in UsbHubPorts.ReadAll().GroupBy(p => p.HubInstanceId))
        {
            Log.Information("{Hub}", hub.Key);
            foreach (var port in hub.Where(p => p.Status != UsbPortStatus.NoDeviceConnected))
            {
                Log.Information("  port {Port,2}: {Status}{Device}{Flag}", port.Port, port.Status,
                    port.Vid != 0 ? $" {port.Vid:X4}:{port.Pid:X4}" : "", port.Failed ? "  <-- FAILED" : "");
            }

            if (hub.Any(p => p.Failed) && UsbHubPorts.UpstreamPortOf(hub.Key) is { } up)
            {
                Log.Information("  heal would cycle port {Port} of {Root} (outermost hub {Top})", up.Port, up.HubInstanceId, up.TopHubInstanceId);
            }
        }

        return 0;
    }
}
