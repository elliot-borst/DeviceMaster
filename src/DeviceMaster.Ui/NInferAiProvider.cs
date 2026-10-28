using System.Text.Json;
using DeviceMaster.AiPause.Providers;

namespace DeviceMaster.Ui;

/// <summary>
/// AI Pause provider for the NInfer WSL server. Goes through <see cref="NInferService"/> only, so
/// its rules hold: stop/start run ninfer.cmd (stop = service + wsl --terminate, which frees RAM and
/// VRAM), and status is read from the service's side-effect-free poll — never a `wsl -d` that
/// would boot the distro. A server that was stopped when the game started stays stopped.
/// </summary>
internal sealed class NInferAiProvider(NInferService service) : IAiProvider
{
    private sealed record Record(bool WasRunning, string? Model);

    public string Id => "ninfer";

    public string DisplayName => "NInfer";

    public bool IsPresent => true;

    /// <summary>NInfer is off by default: DeviceMaster never starts it on launch, even if a game
    /// had paused it when the app last closed. Only the Start button (or a restore after a game
    /// in the same session) starts it.</summary>
    public bool RestoreOnLaunch => false;

    public string Describe() => service.Snapshot.State switch
    {
        NInferState.Running => "Ready",
        NInferState.Starting => "Starting",
        NInferState.Unhealthy => "Unhealthy",
        _ => "Not running",
    };

    public Task<CaptureResult?> CaptureAsync(CancellationToken token)
    {
        var snap = service.Snapshot;
        if (snap.State == NInferState.Stopped)
        {
            return Task.FromResult<CaptureResult?>(null);
        }

        var record = JsonSerializer.SerializeToElement(new Record(true, snap.ModelName));
        return Task.FromResult<CaptureResult?>(new CaptureResult(record, $"server{(snap.ModelName is { } m ? $" ({m})" : "")}", null));
    }

    public async Task UnloadAsync(JsonElement record, CancellationToken token)
    {
        await RunAsync("stop", token);
        await WaitForAsync(state => state == NInferState.Stopped, "still running after stop", token);
    }

    public async Task RestoreAsync(JsonElement record, CancellationToken token)
    {
        if (service.Snapshot.State != NInferState.Stopped)
        {
            return; // already up (or never went down)
        }

        await RunAsync("start", token);
        await WaitForAsync(state => state != NInferState.Stopped, "didn't come back after start", token);
    }

    private async Task RunAsync(string verb, CancellationToken token)
    {
        // RunActionAsync ignores a request while another action is in flight
        for (var i = 0; i < 120 && service.Snapshot.ActionBusy; i++)
        {
            await Task.Delay(500, token);
        }

        await service.RunActionAsync(verb);
    }

    private async Task WaitForAsync(Func<NInferState, bool> done, string failure, CancellationToken token)
    {
        for (var i = 0; i < 30; i++)
        {
            if (done(service.Snapshot.State))
            {
                return;
            }

            await Task.Delay(1000, token);
        }

        throw new InvalidOperationException(failure);
    }
}
