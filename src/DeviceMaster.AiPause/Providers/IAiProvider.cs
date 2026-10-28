using System.Text.Json;

namespace DeviceMaster.AiPause.Providers;

/// <summary>What a provider found loaded and will need to bring back.</summary>
public sealed record CaptureResult(JsonElement Record, string Summary, long? Bytes);

/// <summary>
/// One local AI app GamePause-style control can free VRAM from. The pause is split into
/// capture → (journal saved) → unload, so a crash between steps never loses what to restore.
/// </summary>
public interface IAiProvider
{
    /// <summary>Stable id used as the journal key ("ollama", "lmstudio", "ninfer", "apps").</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Show a status line for this provider (installed / running / configured).</summary>
    bool IsPresent { get; }

    /// <summary>
    /// False = a pause still recorded when DeviceMaster starts is dropped rather than restored,
    /// so the app never brings this provider up on launch (it stays off until started by hand).
    /// Restoring after a game in the same session is unaffected.
    /// </summary>
    bool RestoreOnLaunch => true;

    /// <summary>One line for the status card. Must be side-effect-free and cheap.</summary>
    string Describe();

    /// <summary>What is loaded right now, or null when nothing is (not running = nothing, not an error).</summary>
    Task<CaptureResult?> CaptureAsync(CancellationToken token);

    /// <summary>Free the memory described by <paramref name="record"/>. Throws when it couldn't.</summary>
    Task UnloadAsync(JsonElement record, CancellationToken token);

    /// <summary>Bring back what <paramref name="record"/> describes. Throws to keep recovery pending.</summary>
    Task RestoreAsync(JsonElement record, CancellationToken token);
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T From<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new InvalidDataException($"empty {typeof(T).Name} record");
}
