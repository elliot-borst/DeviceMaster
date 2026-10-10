namespace DeviceMaster.AiPause;

/// <summary>
/// Decides whether a running executable is a game: it must sit inside a discovered game's folder
/// (or be a custom entry's exact exe) and must not be a launcher, helper, installer, crash
/// reporter or anti-cheat service. Pure — the scanner feeds it paths.
/// </summary>
public sealed class GameMatcher
{
    /// <summary>Launchers, overlays, installers, crash reporters and anti-cheat — never a game session.</summary>
    public static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "galaxyclient.exe", "galaxyclientservice.exe", "galaxycommunication.exe", "galaxyclient helper.exe",
        "losslessscaling.exe", "wallpaper32.exe", "wallpaper64.exe", "fpsmonitor.exe",
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe", "eadesktop.exe", "eabackgroundservice.exe", "ealauncher.exe",
        "origin.exe", "ubisoftconnect.exe", "upc.exe", "uplaywebcore.exe", "battle.net.exe", "agent.exe",
        "gamingservices.exe", "gamingservicesnet.exe", "gamelaunchhelper.exe", "redlauncher.exe", "redprelauncher.exe",
        "setup.exe", "uninstall.exe", "unins000.exe", "unitycrashhandler64.exe", "unitycrashhandler32.exe",
        "crashreporter.exe", "crashreportclient.exe", "werfault.exe", "easyanticheat.exe", "easyanticheat_eos.exe",
        "easyanticheat_eos_setup.exe", "beservice.exe", "beservice_x64.exe", "vc_redist.x64.exe", "vc_redist.x86.exe",
        "dxsetup.exe", "dotnetfx.exe", "minecraftlauncher.exe", "launcher.exe", "xboxpcappft.exe",
    };

    /// <summary>Common fullscreen programs that are not games (unknown-game suggestions skip them).</summary>
    public static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
        "vlc.exe", "mpv.exe", "mpc-hc64.exe", "mpc-be64.exe", "wmplayer.exe", "potplayermini64.exe", "spotify.exe",
        "powerpnt.exe", "winword.exe", "excel.exe", "acrobat.exe", "acrord32.exe", "code.exe", "devenv.exe",
        "windowsterminal.exe", "obs64.exe", "mstsc.exe", "vmware.exe", "virtualboxvm.exe", "zoom.exe", "teams.exe",
        "ms-teams.exe", "discord.exe", "slack.exe", "applicationframehost.exe", "searchhost.exe",
        "shellexperiencehost.exe", "lockapp.exe", "textinputhost.exe", "dwm.exe", "lm studio.exe", "ollama app.exe",
        "devicemaster.exe", "startmenuexperiencehost.exe", "photos.exe", "netflix.exe", "plex.exe", "jellyfin.exe",
    };

    private static readonly string[] RedistFolders = ["__installer", "_commonredist", "redist"];

    private readonly List<(GameEntry Game, string Root)> _folders;
    private readonly Dictionary<string, GameEntry> _executables;
    private readonly AiPauseSettings _settings;

    public GameMatcher(IEnumerable<GameEntry> games, AiPauseSettings settings)
    {
        _settings = settings;
        _executables = new Dictionary<string, GameEntry>(StringComparer.Ordinal);
        _folders = [];
        foreach (var game in games)
        {
            var canonical = PathUtil.Canonical(game.Path);
            if (canonical.Length == 0)
            {
                continue;
            }

            if (game.IsExecutable)
            {
                _executables.TryAdd(canonical, game);
            }
            else
            {
                _folders.Add((game, canonical));
            }
        }

        // deepest folder first, so a game nested in another's folder wins over its parent
        _folders.Sort((a, b) => b.Root.Length.CompareTo(a.Root.Length));
    }

    /// <summary>True for launchers, helpers, user exclusions and redistributable installers.</summary>
    public bool IsExcluded(string exePath)
    {
        var name = Path.GetFileName(exePath);
        return Helpers.Contains(name)
            || _settings.ExcludedExecutables.Any(pattern => PathUtil.GlobMatch(name, pattern))
            || _settings.ExcludedPaths.Any(root => PathUtil.IsInside(exePath, root))
            || exePath.Split('\\', '/').Any(part => RedistFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The game this executable belongs to, or null.</summary>
    public GameEntry? Match(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || IsExcluded(exePath))
        {
            return null;
        }

        var canonical = PathUtil.Canonical(exePath);
        if (_executables.TryGetValue(canonical, out var exact))
        {
            return exact;
        }

        foreach (var (game, root) in _folders)
        {
            if (canonical.Length > root.Length && canonical.StartsWith(root, StringComparison.Ordinal) && canonical[root.Length] == '\\')
            {
                return game;
            }
        }

        return null;
    }
}
