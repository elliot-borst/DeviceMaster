using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeviceMaster.Control;

/// <summary>
/// A standalone HTTP listener for the agent API (#33), used by the Windows app (the headless
/// build mounts <see cref="AgentApi"/> in its existing web server instead). Listens on localhost
/// and this machine's Tailscale addresses only, never the LAN.
/// </summary>
public sealed class AgentServer : IDisposable
{
    public const int DefaultPort = 27004;

    private readonly HttpListener _listener = new();
    private readonly AgentApi _api;
    private readonly Action<string>? _log;
    private Thread? _thread;

    public AgentServer(AgentApi api, Action<string>? log = null)
    {
        _api = api;
        _log = log;
    }

    /// <summary>The addresses actually listened on (after any that could not be bound were skipped).</summary>
    public IReadOnlyList<string> Listening { get; private set; } = [];

    /// <summary>This machine's Tailscale (CGNAT 100.64.0.0/10) IPv4 addresses.</summary>
    public static IReadOnlyList<IPAddress> TailnetAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(IsTailnet)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static bool IsTailnet(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    /// <summary>Starts listening; returns false when not even localhost could be bound.</summary>
    public bool Start(int port = DefaultPort)
    {
        var hosts = new List<string> { "127.0.0.1" };
        hosts.AddRange(TailnetAddresses().Select(a => a.ToString()));
        var bound = new List<string>();
        foreach (var host in hosts)
        {
            var prefix = $"http://{host}:{port}/";
            try
            {
                _listener.Prefixes.Add(prefix);
                bound.Add(prefix);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"agent api: cannot listen on {prefix}: {ex.Message}");
            }
        }

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"agent api: listener failed to start ({ex.Message}) — the fleet hub cannot reach this machine");
            return false;
        }

        Listening = bound;
        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMaster agent api" };
        _thread.Start();
        _log?.Invoke($"agent api listening on {string.Join(", ", bound)}"
            + (bound.Count == 1 ? " (no Tailscale address found: localhost only)" : ""));
        return true;
    }

    private void Run()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener.GetContext();
            }
            catch
            {
                break; // stopped
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!_api.TryHandle(ctx))
                {
                    try
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                    catch
                    {
                        // client gone
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // shutting down
        }
    }
}
