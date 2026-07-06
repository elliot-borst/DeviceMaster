using System.Diagnostics;
using System.ServiceProcess;

namespace DeviceMaster.Core.Conflicts;

public sealed record SoftwareConflict(string Kind, string Name, string Detail);

/// <summary>
/// Startup check for vendor software (iCUE, L-Connect, Turing/Turzx tools) that may hold our
/// HID/serial devices open or keep overwriting whatever DeviceMaster sets.
/// </summary>
public static class ConflictingSoftwareChecker
{
    private static readonly string[] Patterns =
        ["corsair", "icue", "lconnect", "l-connect", "lianli", "lian li", "turing", "turzx"];

    public static IReadOnlyList<SoftwareConflict> FindConflicts()
    {
        var conflicts = new List<SoftwareConflict>();

        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                try
                {
                    if (service.Status == ServiceControllerStatus.Running
                        && (Matches(service.ServiceName) || Matches(service.DisplayName)))
                    {
                        conflicts.Add(new SoftwareConflict("service", service.ServiceName,
                            $"running service '{service.DisplayName}'"));
                    }
                }
                catch
                {
                    // Some system services cannot be queried without elevation — irrelevant to us.
                }
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (Matches(process.ProcessName))
                    conflicts.Add(new SoftwareConflict("process", process.ProcessName, $"PID {process.Id}"));
            }
        }

        return conflicts;
    }

    private static bool Matches(string? name) =>
        name is not null && Patterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
}
