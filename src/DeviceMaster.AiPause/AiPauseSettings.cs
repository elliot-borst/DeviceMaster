using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceMaster.AiPause;

/// <summary>What a recognized game does to the local AI.</summary>
public enum GameRule
{
    /// <summary>Pause AI automatically while the game runs.</summary>
    On = 0,

    /// <summary>Leave AI running and offer a one-click pause.</summary>
    Ask,

    /// <summary>Ignore the game entirely.</summary>
    Off,
}

/// <summary>A game the user registered by hand: an exact executable or an install folder.</summary>
public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Another local AI server (llama-server, KoboldCpp, …) stopped for gaming by exe path.</summary>
public sealed class ProcessApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Start it again with the same command line after gaming; false = only stop it.</summary>
    public bool Relaunch { get; set; } = true;
}

/// <summary>
/// AI Pause settings — kept in their own file next to DeviceMaster's config so the fan-control
/// settings and this feature never block each other's saves.
/// </summary>
public sealed class AiPauseSettings
{
    /// <summary>Pause automatically when a game starts. Off still finishes an existing session.</summary>
    public bool AutomationEnabled { get; set; } = true;

    /// <summary>Grace period after the last game exits before AI comes back.</summary>
    public int RestoreDelaySeconds { get; set; } = 30;

    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Point out fullscreen programs no launcher knows (a suggestion only; never pauses).</summary>
    public bool SuggestUnknownGames { get; set; } = true;

    public bool OllamaEnabled { get; set; } = true;
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";

    public bool LmStudioEnabled { get; set; } = true;

    /// <summary>Path to lms.exe; null/empty = auto-detect.</summary>
    public string? LmsPath { get; set; }

    /// <summary>Stop/start the NInfer WSL server (only on machines with its control script).</summary>
    public bool NInferEnabled { get; set; } = true;

    public List<ProcessApp> ProcessApps { get; set; } = [];

    public List<CustomGame> ExtraGames { get; set; } = [];

    /// <summary>Extra Steam roots (folders containing steamapps).</summary>
    public List<string> SteamRoots { get; set; } = [];

    /// <summary>Folders whose immediate subfolders are each a game.</summary>
    public List<string> GameRoots { get; set; } = [];

    /// <summary>Per-game rule keyed by the game's canonical path; absent = On.</summary>
    public Dictionary<string, GameRule> GameRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Executable names/globs (* and ?) that never count as games.</summary>
    public List<string> ExcludedExecutables { get; set; } = [];

    /// <summary>Exact executable paths or folders that never count as games.</summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>Executables answered with "Not a game" — never suggested again.</summary>
    public List<string> DismissedSuggestions { get; set; } = [];

    public GameRule RuleFor(string gamePath) =>
        GameRules.TryGetValue(PathUtil.Canonical(gamePath), out var rule) ? rule : GameRule.On;

    public void SetRule(string gamePath, GameRule rule)
    {
        var key = PathUtil.Canonical(gamePath);
        if (rule == GameRule.On)
        {
            GameRules.Remove(key);
        }
        else
        {
            GameRules[key] = rule;
        }
    }

    // ---------- persistence ----------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AiPauseSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AiPauseSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    // the dictionary comparer doesn't survive deserialization
                    loaded.GameRules = new Dictionary<string, GameRule>(loaded.GameRules, StringComparer.OrdinalIgnoreCase);
                    loaded.RestoreDelaySeconds = Math.Clamp(loaded.RestoreDelaySeconds, 0, 3600);
                    return loaded;
                }
            }
        }
        catch
        {
            // corrupt file — defaults (the next save rewrites it)
        }

        return new AiPauseSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

internal static class AtomicFile
{
    /// <summary>Write-then-rename so a crash mid-write never leaves a torn file behind.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
