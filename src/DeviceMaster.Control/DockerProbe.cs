using System.Net.Sockets;
using System.Text.Json;

namespace DeviceMaster.Control;

/// <summary>Container counts for the fleet display (#49).</summary>
public sealed record DockerInfo(int Running, int Total, IReadOnlyList<string> Stopped);

/// <summary>
/// Reads container counts from the Docker Engine API over its unix socket (read-only GET
/// /containers/json?all=1). Only used where the agent is already privileged on its host, so the
/// socket adds no new reach. Re-read at most every 15 s.
/// </summary>
public sealed class DockerProbe(string socketPath)
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
            return new NetworkStream(socket, ownsSocket: true);
        },
    }) { Timeout = TimeSpan.FromSeconds(3) };

    private volatile DockerInfo? _last;
    private long _due;
    private int _refreshing;

    /// <summary>The latest counts; refreshes in the background so the control tick never waits on Docker.</summary>
    public DockerInfo? Get()
    {
        if (Environment.TickCount64 >= _due && Interlocked.Exchange(ref _refreshing, 1) == 0)
        {
            _due = Environment.TickCount64 + 15_000;
            _ = Task.Run(async () =>
            {
                try
                {
                    _last = Parse(await _http.GetStringAsync("http://docker/containers/json?all=1"));
                }
                catch
                {
                    _last = null; // shown as unavailable
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        }

        return _last;
    }

    /// <summary>Counts from a /containers/json answer (public for tests).</summary>
    public static DockerInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var running = 0;
        var stopped = new List<string>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.TryGetProperty("State", out var s) && s.GetString() == "running")
            {
                running++;
            }
            else if (c.TryGetProperty("Names", out var names) && names.GetArrayLength() > 0)
            {
                stopped.Add(names[0].GetString()?.TrimStart('/') ?? "?");
            }
        }

        return new DockerInfo(running, doc.RootElement.GetArrayLength(), stopped);
    }
}
