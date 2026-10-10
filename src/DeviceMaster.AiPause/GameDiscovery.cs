using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DeviceMaster.AiPause;

/// <summary>An installed game: a launcher's install folder, or an exact executable for custom entries.</summary>
public sealed record GameEntry(string Launcher, string Id, string Name, string Path)
{
    /// <summary>A custom entry pointing at one .exe matches only that file, not its folder.</summary>
    public bool IsExecutable => Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads each launcher's own install records — Steam, Epic, GOG, EA, Ubisoft Connect, Battle.net,
/// Xbox — plus the user's custom games. Never scans a drive recursively. Each launcher is isolated:
/// one that throws is reported in <see cref="DiscoveryResult.Errors"/> and the rest still count.
/// Approach ported from GamePause for Local AI (MIT) — see docs/REFERENCES.md.
/// </summary>
public static class GameDiscovery
{
    /// <summary>Launcher entries that are tools rather than games.</summary>
    private static readonly HashSet<string> BackgroundUtilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper engine", "lossless scaling", "steamworks common redistributables", "gog galaxy", "fps monitor",
        "unreal engine", "steamvr", "soundpad", "blender",
    };

    public static DiscoveryResult Scan(AiPauseSettings settings)
    {
        var games = new List<GameEntry>();
        var errors = new Dictionary<string, string>();
        void Run(string launcher, Func<IEnumerable<GameEntry>> body)
        {
            try
            {
                games.AddRange(body());
            }
            catch (Exception ex)
            {
                errors[launcher] = ex.Message;
            }
        }

        Run("Steam", () => Steam(settings.SteamRoots));
        Run("Epic", Epic);
        Run("GOG", Gog);
        Run("EA", Ea);
        Run("Ubisoft", Ubisoft);
        Run("Battle.net", BattleNet);
        Run("Xbox", Xbox);
        Run("Custom", () => Custom(settings));

        var unique = games
            .Where(g => g.Path.Length > 0 && !BackgroundUtilities.Contains(g.Name))
            .GroupBy(g => PathUtil.Canonical(g.Path))
            .Select(group => group.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DiscoveryResult(unique, errors);
    }

    // ---------- Steam ----------

    private static IEnumerable<GameEntry> Steam(IEnumerable<string> extraRoots)
    {
        var roots = new HashSet<string>(extraRoots, StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Env("ProgramFiles(x86)", @"C:\Program Files (x86)"), "Steam"),
        };
        if (RegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") is { } steamPath)
        {
            roots.Add(steamPath.Replace('/', '\\'));
        }

        foreach (var root in roots.ToList())
        {
            var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(file))
            {
                foreach (var library in LauncherParsers.SteamLibraryFolders(ReadText(file)))
                {
                    roots.Add(library);
                }
            }
        }

        foreach (var root in roots)
        {
            var apps = Path.Combine(root, "steamapps");
            if (!Directory.Exists(apps))
            {
                continue;
            }

            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try
                {
                    text = ReadText(manifest);
                }
                catch
                {
                    continue;
                }

                if (LauncherParsers.SteamManifest(text) is { } app)
                {
                    yield return new GameEntry("Steam", app.AppId, app.Name, Path.Combine(apps, "common", app.InstallDir));
                }
            }
        }
    }

    // ---------- Epic ----------

    private static IEnumerable<GameEntry> Epic()
    {
        var dir = Path.Combine(Env("PROGRAMDATA", @"C:\ProgramData"), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            GameEntry? game = null;
            try
            {
                game = LauncherParsers.EpicItem(ReadText(file));
            }
            catch
            {
                // a half-written manifest — skip it
            }

            if (game is not null)
            {
                yield return game;
            }
        }
    }

    // ---------- GOG / EA / Ubisoft / Battle.net (registry) ----------

    private static IEnumerable<GameEntry> Gog()
    {
        foreach (var (id, values) in RegistryChildren(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games")
            .Concat(RegistryChildren(Registry.LocalMachine, @"SOFTWARE\GOG.com\Games")))
        {
            if (LauncherParsers.GogGame(id, values) is { } game)
            {
                yield return game;
            }
        }
    }

    private static IEnumerable<GameEntry> Ea()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var key in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\Electronic Arts\EA Games" })
            {
                foreach (var (name, values) in RegistryChildren(hive, key))
                {
                    var path = Get(values, "Install Dir") ?? Get(values, "InstallDir") ?? Get(values, "InstallLocation");
                    if (path is not null)
                    {
                        yield return new GameEntry("EA", name, Get(values, "DisplayName") ?? name, path);
                    }
                }
            }
        }

        foreach (var (name, values) in Uninstall())
        {
            if (Get(values, "InstallLocation") is { } path
                && (Get(values, "Publisher") ?? "").Contains("electronic arts", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(path, "__Installer")))
            {
                yield return new GameEntry("EA", name, Get(values, "DisplayName") ?? name, path);
            }
        }
    }

    private static IEnumerable<GameEntry> Ubisoft()
    {
        foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
        {
            foreach (var (id, values) in RegistryChildren(Registry.LocalMachine, key))
            {
                if (Get(values, "InstallDir") is { } path)
                {
                    var name = Path.GetFileName(path.Replace('/', '\\').TrimEnd('\\'));
                    yield return new GameEntry("Ubisoft", id, name.Length > 0 ? name : id, path.Replace('/', '\\'));
                }
            }
        }
    }

    private static IEnumerable<GameEntry> BattleNet()
    {
        foreach (var (name, values) in Uninstall())
        {
            if (LauncherParsers.BattleNetGame(name, values) is { } game)
            {
                yield return game;
            }
        }
    }

    // ---------- Xbox / Game Pass (folders on fixed drives) ----------

    private static IEnumerable<GameEntry> Xbox()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var top = drive.RootDirectory.FullName;
            try
            {
                var marker = Path.Combine(top, ".GamingRoot");
                if (File.Exists(marker) && LauncherParsers.GamingRootFolder(File.ReadAllBytes(marker)) is { } folder)
                {
                    roots.Add(Path.Combine(top, folder.TrimStart('\\')));
                }
            }
            catch
            {
                // unreadable marker — fall back to the default folder name
            }

            roots.Add(Path.Combine(top, "XboxGames"));
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var child in SafeDirectories(root))
            {
                var content = Path.Combine(child, "Content");
                var location = Directory.Exists(content) ? content : child;
                var config = Path.Combine(location, "MicrosoftGame.config");
                if (!File.Exists(config))
                {
                    continue;
                }

                string id;
                try
                {
                    id = XDocument.Parse(ReadText(config)).Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value ?? "Xbox game";
                }
                catch
                {
                    id = "Xbox game";
                }

                yield return new GameEntry("Xbox", id, Path.GetFileName(child), location);
            }
        }
    }

    // ---------- custom ----------

    private static IEnumerable<GameEntry> Custom(AiPauseSettings settings)
    {
        foreach (var game in settings.ExtraGames.Where(g => g.Path.Length > 0))
        {
            yield return new GameEntry("Custom", game.Path, game.Name.Length > 0 ? game.Name : Path.GetFileNameWithoutExtension(game.Path), game.Path);
        }

        foreach (var root in settings.GameRoots.Where(Directory.Exists))
        {
            foreach (var child in SafeDirectories(root))
            {
                yield return new GameEntry("Custom", child, Path.GetFileName(child), child);
            }
        }
    }

    // ---------- helpers ----------

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    /// <summary>Launcher metadata is small; anything huge is not what we expect.</summary>
    private static string ReadText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException($"{path} is unexpectedly large");
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch
        {
            return [];
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? RegistryString(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> RegistryChildren(RegistryKey hive, string path)
    {
        var rows = new List<(string, IReadOnlyDictionary<string, string>)>();
        try
        {
            using var parent = hive.OpenSubKey(path);
            if (parent is null)
            {
                return rows;
            }

            foreach (var childName in parent.GetSubKeyNames())
            {
                try
                {
                    using var child = parent.OpenSubKey(childName);
                    if (child is null)
                    {
                        continue;
                    }

                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var valueName in child.GetValueNames())
                    {
                        if (child.GetValue(valueName) is string text)
                        {
                            values[valueName] = text;
                        }
                    }

                    rows.Add((childName, values));
                }
                catch
                {
                    // access denied on one key — keep the rest
                }
            }
        }
        catch
        {
            // key unreadable
        }

        return rows;
    }

    private static IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> Uninstall() =>
        new[] { Registry.LocalMachine, Registry.CurrentUser }.SelectMany(hive =>
            RegistryChildren(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
                .Concat(RegistryChildren(hive, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")));
}

public sealed record DiscoveryResult(IReadOnlyList<GameEntry> Games, IReadOnlyDictionary<string, string> Errors);

/// <summary>Pure parsers for launcher metadata — unit-tested without any launcher installed.</summary>
public static class LauncherParsers
{
    public sealed record SteamApp(string AppId, string Name, string InstallDir);

    /// <summary>Library roots listed in Steam's libraryfolders.vdf (old flat and new nested shapes).</summary>
    public static IReadOnlyList<string> SteamLibraryFolders(string vdfText)
    {
        var root = Vdf.Parse(vdfText);
        var folders = root.TryGetValue("libraryfolders", out var inner) && inner is Dictionary<string, object> map
            ? map
            : root;
        var result = new List<string>();
        foreach (var (key, value) in folders)
        {
            if (!key.All(char.IsAsciiDigit))
            {
                continue;
            }

            var path = value switch
            {
                string flat => flat,
                Dictionary<string, object> nested when nested.TryGetValue("path", out var p) && p is string s => s,
                _ => null,
            };
            if (path is { Length: > 0 })
            {
                result.Add(path.Replace(@"\\", @"\"));
            }
        }

        return result;
    }

    /// <summary>A fully installed Steam app (StateFlags bit 4) with a safe relative installdir.</summary>
    public static SteamApp? SteamManifest(string acfText)
    {
        if (!Vdf.Parse(acfText).TryGetValue("AppState", out var node) || node is not Dictionary<string, object> state)
        {
            return null;
        }

        string Str(string key) => state.TryGetValue(key, out var v) && v is string s ? s : "";
        var folder = Str("installdir");
        _ = ulong.TryParse(Str("StateFlags"), out var flags);
        var unsafePath = folder.Length == 0
            || folder.Contains(':')
            || folder.StartsWith('\\')
            || folder.StartsWith('/')
            || folder.Replace('/', '\\').Split('\\').Contains("..");
        if ((flags & 4) == 0 || unsafePath)
        {
            return null;
        }

        var appId = Str("appid");
        var name = Str("name");
        return new SteamApp(appId.Length > 0 ? appId : folder, name.Length > 0 ? name : folder, folder);
    }

    /// <summary>One Epic Games Launcher .item manifest; incomplete installs and DLC-only entries are skipped.</summary>
    public static GameEntry? EpicItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(string key) =>
            root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (string.IsNullOrEmpty(Str("LaunchExecutable")) || Str("InstallLocation") is not { Length: > 0 } location)
        {
            return null;
        }

        return new GameEntry("Epic", Str("AppName") ?? "unknown", Str("DisplayName") ?? "Epic game", location.Replace('/', '\\'));
    }

    /// <summary>GOG registers each product by id; DLC names its base game in dependsOn and is skipped.</summary>
    public static GameEntry? GogGame(string id, IReadOnlyDictionary<string, string> values)
    {
        string? Filled(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        if (Filled("dependsOn") is not null)
        {
            return null;
        }

        var folder = Filled("path") ?? (Filled("exe") is { } exe ? Path.GetDirectoryName(exe) : null);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        var name = Filled("gameName") ?? Path.GetFileName(folder.TrimEnd('\\'));
        return new GameEntry("GOG", id, string.IsNullOrEmpty(name) ? id : name, folder);
    }

    private static readonly string[] BattleNetSelf = ["bna", "agent", "battle.net", "battlenet"];

    /// <summary>Battle.net games register an uninstaller that runs the launcher with --uid=&lt;game&gt;.</summary>
    public static GameEntry? BattleNetGame(string keyName, IReadOnlyDictionary<string, string> values)
    {
        if (BattleNetSelf.Contains(keyName.ToLowerInvariant()))
        {
            return null;
        }

        var command = (values.TryGetValue("UninstallString", out var c) ? c : "").ToLowerInvariant();
        if (!command.Contains("battle.net") || !command.Contains("--uid=")
            || command.Contains("--uid=bna") || command.Contains("--uid=agent")
            || !values.TryGetValue("InstallLocation", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = values.TryGetValue("DisplayName", out var display) && !string.IsNullOrWhiteSpace(display) ? display : keyName;
        return new GameEntry("Battle.net", keyName, name, path.Trim());
    }

    /// <summary>
    /// The Xbox app's per-drive <c>.GamingRoot</c> marker: an 8-byte header, then the game folder
    /// as NUL-terminated UTF-16LE (e.g. "\XboxGames").
    /// </summary>
    public static string? GamingRootFolder(byte[] bytes)
    {
        if (bytes.Length <= 8)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var i = 8; i + 1 < bytes.Length; i += 2)
        {
            var ch = (char)(bytes[i] | (bytes[i + 1] << 8));
            if (ch == '\0')
            {
                break;
            }

            builder.Append(ch);
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}

/// <summary>Minimal Valve KeyValues (VDF) reader: quoted keys/values and nested braces.</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        var position = 0;
        return ParseObject(text, ref position, topLevel: true);
    }

    private static Dictionary<string, object> ParseObject(string text, ref int position, bool topLevel)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length)
            {
                if (!topLevel)
                {
                    throw new FormatException("Unterminated VDF object");
                }

                return result;
            }

            if (text[position] == '}')
            {
                if (topLevel)
                {
                    throw new FormatException("Unexpected '}' in VDF");
                }

                position++;
                return result;
            }

            var key = ReadToken(text, ref position);
            SkipWhitespaceAndComments(text, ref position);
            if (position < text.Length && text[position] == '{')
            {
                position++;
                result[key] = ParseObject(text, ref position, topLevel: false);
            }
            else
            {
                result[key] = ReadToken(text, ref position);
            }
        }
    }

    private static string ReadToken(string text, ref int position)
    {
        if (position >= text.Length)
        {
            throw new FormatException("Unexpected end of VDF");
        }

        var builder = new StringBuilder();
        if (text[position] == '"')
        {
            position++;
            while (position < text.Length && text[position] != '"')
            {
                if (text[position] == '\\' && position + 1 < text.Length)
                {
                    position++;
                    builder.Append(text[position] switch { 'n' => '\n', 't' => '\t', var c => c });
                }
                else
                {
                    builder.Append(text[position]);
                }

                position++;
            }

            if (position >= text.Length)
            {
                throw new FormatException("Unterminated VDF string");
            }

            position++;
            return builder.ToString();
        }

        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
        {
            builder.Append(text[position++]);
        }

        if (builder.Length == 0)
        {
            throw new FormatException($"Unexpected '{text[position]}' in VDF");
        }

        return builder.ToString();
    }

    private static void SkipWhitespaceAndComments(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            else if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
            }
            else
            {
                return;
            }
        }
    }
}
