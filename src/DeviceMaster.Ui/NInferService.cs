using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeviceMaster.Control;
using DeviceMaster.Sensors;

namespace DeviceMaster.Ui;

public enum NInferState
{
    /// <summary>The WSL distro isn't running — a deliberate state (VRAM freed for gaming).</summary>
    Stopped,

    /// <summary>Distro up but /health not answering yet — boot + model load takes ~15–30 s.</summary>
    Starting,

    /// <summary>Distro up but /health kept failing past the startup grace period.</summary>
    Unhealthy,

    Running,
}

public sealed record NInferRequestRow(DateTime At, NInferRequestSummary Summary);

/// <summary>Immutable snapshot of everything the NInfer tab shows, safe to read from the UI thread.</summary>
public sealed record NInferSnapshot
{
    public NInferState State { get; init; } = NInferState.Stopped;
    public string? ModelName { get; init; }
    public DateTimeOffset? RunningSince { get; init; }
    public NInferThroughput? Throughput { get; init; }
    public IReadOnlyList<double> DecodeHistory { get; init; } = [];
    public IReadOnlyList<double> PrefillHistory { get; init; } = [];
    public IReadOnlyList<NInferRequestRow> RecentRequests { get; init; } = [];
    public bool ActionBusy { get; init; }
    public string? ActionInfo { get; init; }
}

/// <summary>
/// Watches and controls the user's NInfer LLM server (a systemd unit inside a WSL2 distro).
///
/// Two rules this class exists to enforce:
/// 1. Status detection is side-effect-free. Any <c>wsl -d &lt;distro&gt;</c> command (or \\wsl$ path)
///    BOOTS the distro when it's down, and systemd then auto-starts the enabled ninfer unit,
///    silently re-grabbing ~30 GB of VRAM. So: poll the unauthenticated /health endpoint; only
///    when that fails, ask <c>wsl --list --running</c> (which never boots anything) whether the
///    distro is even up; and only spawn the <c>wsl -d … journalctl -f</c> stats stream while the
///    state is RUNNING, killing it the moment the state leaves RUNNING.
/// 2. Start/stop/restart go through the user's ninfer.cmd, never raw wsl/systemctl — the script
///    encodes invariants (stop = service + <c>wsl --terminate</c> so RAM and VRAM come back;
///    start/restart respawn the keep-alive that stops WSL idle-killing the distro).
///
/// NInfer is never auto-started: the only paths that run "start" are the explicit UI buttons.
/// </summary>
public sealed class NInferService : IDisposable
{
    private const int HealthPollMs = 4_000;        // /health is cheap and unauthenticated
    private const int HealthTimeoutMs = 2_000;
    private const int StartingGraceMs = 60_000;    // distro up + health failing reads as "starting…" this long
    private const int ActionGraceMs = 120_000;     // after our own Start/Restart click
    private const int HistoryCap = 120;            // ~10 min of 5 s throughput samples

    private readonly ControlSettings _settings;
    private readonly Action _saveSettings;
    private readonly Action<string>? _log;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _gate = new();
    private readonly object _journalGate = new();

    private volatile NInferSnapshot _snapshot = new();

    // mutable state, all under _gate
    private NInferState _state = NInferState.Stopped;
    private DateTimeOffset? _runningSince;
    private NInferThroughput? _throughput;
    private readonly List<double> _decodeHistory = [];
    private readonly List<double> _prefillHistory = [];
    private readonly List<NInferRequestRow> _requests = [];
    private bool _actionBusy;
    private string? _actionInfo;
    private string? _modelName;

    private long _unhealthySince;    // first tick of a continuous distro-up-but-unhealthy stretch
    private long _startGraceUntil;   // set by our own Start/Restart actions
    private bool _modelFetchAttempted;
    private Process? _journal;       // wsl … journalctl -f — exists ONLY while state == Running

    public string ScriptPath { get; }

    public NInferSnapshot Snapshot => _snapshot;

    public NInferService(ControlSettings settings, Action saveSettings, Action<string>? log, string scriptPath)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _log = log;
        ScriptPath = scriptPath;
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>The ninfer control script, if this machine has one: configured path, then
    /// %USERPROFILE%\bin\ninfer.cmd, then PATH. Null hides the tab entirely.</summary>
    public static string? ResolveScript(ControlSettings settings)
    {
        var candidates = new List<string>();
        if (settings.NInferScript is { Length: > 0 } configured)
        {
            candidates.Add(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
        {
            candidates.Add(Path.Combine(profile, "bin", "ninfer.cmd"));
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir, "ninfer.cmd"));
            }
            catch
            {
                // malformed PATH entry
            }
        }

        try
        {
            return candidates.FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    // ---------- status polling ----------

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"ninfer: poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(HealthPollMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        await _pollGate.WaitAsync(token);
        try
        {
            NInferState state;
            if (await CheckHealthAsync(token))
            {
                state = NInferState.Running;
                _unhealthySince = 0;
            }
            else if (!await IsDistroRunningAsync(token))
            {
                // `wsl --list --running` never boots a distro — the only safe check while down
                state = NInferState.Stopped;
                _unhealthySince = 0;
            }
            else
            {
                var now = Environment.TickCount64;
                if (_unhealthySince == 0)
                {
                    _unhealthySince = now;
                }

                state = now < _startGraceUntil || now - _unhealthySince < StartingGraceMs
                    ? NInferState.Starting
                    : NInferState.Unhealthy;
            }

            lock (_gate)
            {
                if (state == NInferState.Running && _state != NInferState.Running)
                {
                    _runningSince = DateTimeOffset.Now;
                }
                else if (state != NInferState.Running && _state == NInferState.Running)
                {
                    _runningSince = null;
                    _throughput = null;       // live numbers are stale the moment the server is gone
                    _modelFetchAttempted = false;
                }

                _state = state;
                Publish();
            }

            // The journal stream is a `wsl -d` process, so it may exist ONLY while RUNNING —
            // spawned while the distro is down it would boot it and re-grab the VRAM.
            if (state == NInferState.Running)
            {
                EnsureJournal();
            }
            else
            {
                StopJournal();
            }

            if (state == NInferState.Running && _modelName is null && !_modelFetchAttempted)
            {
                _modelFetchAttempted = true;
                await FetchModelNameAsync(token);
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task<bool> CheckHealthAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(HealthTimeoutMs);
            using var response = await _http.GetAsync(
                _settings.NInferBaseUrl.TrimEnd('/') + "/health", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return body.Contains("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsDistroRunningAsync(CancellationToken token)
    {
        var output = await RunHiddenAsync("wsl.exe", "--list --running --quiet", 15_000, token);
        return output is not null && output
            .Split('\n')
            .Any(line => line.Trim().Equals(_settings.NInferDistro, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- control actions (ninfer.cmd only) ----------

    /// <summary>Runs `ninfer.cmd start|stop|restart` hidden; UI buttons are the only callers.</summary>
    public async Task RunActionAsync(string verb)
    {
        lock (_gate)
        {
            if (_actionBusy)
            {
                return;
            }

            _actionBusy = true;
            _actionInfo = $"{verb}…";
            Publish();
        }

        try
        {
            if (verb is "stop" or "restart")
            {
                StopJournal(); // stop's `wsl --terminate` would kill it anyway; restart respawns it
            }

            if (verb is "start" or "restart")
            {
                _startGraceUntil = Environment.TickCount64 + ActionGraceMs; // model load takes 15–30 s
            }
            else
            {
                _startGraceUntil = 0;
            }

            _log?.Invoke($"ninfer: running '{verb}' via {ScriptPath}");
            var output = await RunScriptAsync(verb, 90_000, _cts.Token);
            var summary = LastLine(output) ?? (output is null ? "script timed out / failed to run" : "done");
            lock (_gate)
            {
                _actionInfo = $"{DateTime.Now:HH:mm:ss} {verb}: {summary}";
                Publish();
            }

            _log?.Invoke($"ninfer: '{verb}' → {summary}");
            await PollOnceAsync(_cts.Token); // reflect the new state immediately
        }
        catch (OperationCanceledException)
        {
            // app shutting down
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _actionInfo = $"{verb} failed: {ex.Message}";
                Publish();
            }
        }
        finally
        {
            lock (_gate)
            {
                _actionBusy = false;
                Publish();
            }
        }
    }

    /// <summary>`ninfer.cmd log` — last 40 journal lines (the script refuses safely while down).</summary>
    public async Task<string> ReadLogAsync()
    {
        var output = await RunScriptAsync("log", 30_000, _cts.Token);
        return output is { Length: > 0 } ? output.Trim() : "No output — script timed out or failed to run.";
    }

    private Task<string?> RunScriptAsync(string verb, int timeoutMs, CancellationToken token) =>
        RunHiddenAsync("cmd.exe", $"/d /c \"\"{ScriptPath}\" {verb}\"", timeoutMs, token);

    private static string? LastLine(string? output) => output?
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault();

    /// <summary>Runs a console command hidden, returns stdout+stderr, or null on timeout/failure.</summary>
    private static async Task<string?> RunHiddenAsync(string file, string args, int timeoutMs, CancellationToken token)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["WSL_UTF8"] = "1"; // wsl.exe emits UTF-16LE otherwise

            process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return await stdout + await stderr;
        }
        catch
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort
            }

            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    // ---------- journal stream (inference stats) ----------

    private void EnsureJournal()
    {
        lock (_journalGate)
        {
            if (_journal is { HasExited: false })
            {
                return;
            }

            _journal?.Dispose();
            _journal = null;
            try
            {
                var psi = new ProcessStartInfo("wsl.exe",
                    $"-d {_settings.NInferDistro} -- journalctl -u ninfer -f -n 0 --no-pager -o cat")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["WSL_UTF8"] = "1";
                var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is { } line)
                    {
                        OnJournalLine(line);
                    }
                };
                if (process.Start())
                {
                    process.BeginOutputReadLine();
                    _journal = process;
                    _log?.Invoke("ninfer: journal stats stream started");
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"ninfer: journal stream failed to start: {ex.Message}");
            }
        }
    }

    private void StopJournal()
    {
        lock (_journalGate)
        {
            if (_journal is null)
            {
                return;
            }

            try
            {
                if (!_journal.HasExited)
                {
                    _journal.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // already gone
            }

            _journal.Dispose();
            _journal = null;
            _log?.Invoke("ninfer: journal stats stream stopped");
        }
    }

    private void OnJournalLine(string line)
    {
        if (NInferJournal.TryParseThroughput(line) is { } throughput)
        {
            lock (_gate)
            {
                _throughput = throughput;
                Append(_decodeHistory, throughput.DecodeTokPerS);
                Append(_prefillHistory, throughput.PrefillTokPerS);
                Publish();
            }
        }
        else if (NInferJournal.TryParseRequest(line) is { } request)
        {
            lock (_gate)
            {
                _requests.Insert(0, new NInferRequestRow(DateTime.Now, request));
                if (_requests.Count > 10)
                {
                    _requests.RemoveAt(10);
                }

                Publish();
            }
        }
    }

    private static void Append(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryCap)
        {
            history.RemoveAt(0);
        }
    }

    // ---------- model name via /v1/models (optional garnish; every failure is silent) ----------

    private async Task FetchModelNameAsync(CancellationToken token)
    {
        try
        {
            var key = _settings.NInferApiKey;
            var freshKey = false;
            if (key is not { Length: > 0 })
            {
                key = await ReadApiKeyAsync(token);
                freshKey = true;
            }

            if (key is not { Length: > 0 })
            {
                return;
            }

            var name = await QueryModelsAsync(key, token);
            if (name is null && !freshKey)
            {
                // cached key may be stale — re-read it once (safe: server is RUNNING, distro is up)
                key = await ReadApiKeyAsync(token);
                if (key is { Length: > 0 })
                {
                    name = await QueryModelsAsync(key, token);
                }
            }

            if (name is { Length: > 0 })
            {
                lock (_gate)
                {
                    _modelName = name;
                    Publish();
                }

                if (key != _settings.NInferApiKey)
                {
                    _settings.NInferApiKey = key;
                    _saveSettings();
                }
            }
        }
        catch
        {
            // the journal stream already covers the stats; model name is nice-to-have
        }
    }

    /// <summary>Reads ~/.config/ninfer/api-key inside WSL. Only called while RUNNING — a
    /// `wsl -d` command while the distro is down would boot it. The key is never logged.</summary>
    private async Task<string?> ReadApiKeyAsync(CancellationToken token)
    {
        var output = await RunHiddenAsync("wsl.exe",
            $"-d {_settings.NInferDistro} -- sh -c \"cat ~/.config/ninfer/api-key\"", 15_000, token);
        var key = LastLine(output);
        return key is { Length: > 0 and < 256 } && !key.Contains(' ') && !key.Contains(':') ? key : null;
    }

    private async Task<string?> QueryModelsAsync(string key, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(5_000);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                _settings.NInferBaseUrl.TrimEnd('/') + "/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch
        {
            // wrong key / unexpected shape — skip the garnish
        }

        return null;
    }

    // ---------- snapshot ----------

    /// <summary>Rebuilds the immutable snapshot; must be called under <see cref="_gate"/>.</summary>
    private void Publish()
    {
        _snapshot = new NInferSnapshot
        {
            State = _state,
            ModelName = _modelName,
            RunningSince = _runningSince,
            Throughput = _throughput,
            DecodeHistory = _decodeHistory.ToArray(),
            PrefillHistory = _prefillHistory.ToArray(),
            RecentRequests = _requests.ToArray(),
            ActionBusy = _actionBusy,
            ActionInfo = _actionInfo,
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        StopJournal();
        _http.Dispose();
    }
}
