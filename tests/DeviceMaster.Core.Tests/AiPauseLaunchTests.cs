using System.Text.Json;
using DeviceMaster.AiPause;
using DeviceMaster.AiPause.Providers;

namespace DeviceMaster.Core.Tests;

public class AiPauseLaunchTests
{
    private sealed class FakeProvider(string id, bool restoreOnLaunch) : IAiProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public bool IsPresent => true;
        public bool RestoreOnLaunch => restoreOnLaunch;
        public string Describe() => "Ready";
        public Task<CaptureResult?> CaptureAsync(CancellationToken token) => Task.FromResult<CaptureResult?>(null);
        public Task UnloadAsync(JsonElement record, CancellationToken token) => Task.CompletedTask;
        public Task RestoreAsync(JsonElement record, CancellationToken token) => throw new InvalidOperationException("must not restore on launch");
    }

    private static string WriteJournal(params string[] providerIds)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dm-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var journal = new RecoveryJournal { PausedAt = DateTimeOffset.Now, Reason = "a game" };
        foreach (var id in providerIds)
        {
            journal.Providers[id] = new ProviderEntry { Stage = ProviderStage.Unloaded, Record = JsonSerializer.SerializeToElement(new { WasRunning = true }) };
        }

        journal.Save(Path.Combine(dir, "ai-pause-state.json"));
        return dir;
    }

    [Fact]
    public void ProviderThatStaysOffOnLaunch_IsDroppedNotRestored()
    {
        var dir = WriteJournal("ninfer");
        try
        {
            using var service = new AiPauseService(dir, new AiPauseSettings(), [new FakeProvider("ninfer", restoreOnLaunch: false)], null);
            Assert.False(service.Snapshot.Holding);
            Assert.False(File.Exists(Path.Combine(dir, "ai-pause-state.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OtherProvidersStillComeBack()
    {
        var dir = WriteJournal("ninfer", "ollama");
        try
        {
            using var service = new AiPauseService(dir, new AiPauseSettings(),
                [new FakeProvider("ninfer", restoreOnLaunch: false), new FakeProvider("ollama", restoreOnLaunch: true)], null);
            Assert.True(service.Snapshot.Holding);
            var kept = RecoveryJournal.Load(Path.Combine(dir, "ai-pause-state.json"))!;
            Assert.Equal(["ollama"], kept.Providers.Keys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
