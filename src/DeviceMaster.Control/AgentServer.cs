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

    // Tailscale often comes up after the app (a logon task right after boot): the address can appear
    // later, so it is re-checked on every network change and every 30 s, and bound when it does (#84)
    private readonly HashSet<string> _bound = [];
    private readonly HashSet<string> _failed = [];
    private Timer? _rescan;
    private int _port;

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

        _port = port;
        lock (_bound)
        {
            _bound.UnionWith(bound);
            Listening = _bound.ToList();
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "DeviceMaster agent api" };
        _thread.Start();
        _log?.Invoke($"agent api listening on {string.Join(", ", bound)}"
            + (bound.Count == 1 ? " (no Tailscale address found yet: localhost only, binding it when it appears)" : ""));
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        _rescan = new Timer(_ => BindNewTailnetAddresses(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return true;
    }

    /// <summary>The prefixes for tailnet addresses that aren't listened on yet (pure; tested).</summary>
    public static IReadOnlyList<string> NewPrefixes(IEnumerable<IPAddress> tailnet, IReadOnlySet<string> known, int port) =>
        tailnet.Where(IsTailnet).Select(a => $"http://{a}:{port}/").Distinct().Where(p => !known.Contains(p)).ToList();

    private void OnAddressChanged(object? sender, EventArgs e) => BindNewTailnetAddresses();

    /// <summary>Adds a prefix for every tailnet address that appeared since the last check (the listener keeps running).</summary>
    private void BindNewTailnetAddresses()
    {
        if (!_listener.IsListening)
        {
            return;
        }

        lock (_bound)
        {
            foreach (var prefix in NewPrefixes(TailnetAddresses(), _bound.Union(_failed).ToHashSet(), _port))
            {
                try
                {
                    _listener.Prefixes.Add(prefix);
                    _bound.Add(prefix);
                    Listening = _bound.ToList();
                    _log?.Invoke($"agent api: Tailscale address appeared, now also listening on {prefix}");
                }
                catch (Exception ex)
                {
                    _failed.Add(prefix); // report once, not every 30 s
                    _log?.Invoke($"agent api: cannot listen on {prefix}: {ex.Message}");
                }
            }
        }
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
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _rescan?.Dispose();
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
