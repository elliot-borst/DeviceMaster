using System.Text.Json;
using DeviceMaster.AiPause;
using DeviceMaster.AiPause.Providers;

namespace DeviceMaster.Core.Tests;

public class AiPauseParserTests
{
    [Fact]
    public void Vdf_ParsesNestedLibraryFolders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "0" }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
                "contentstatsid" "123"
            }
            """;
        var folders = LauncherParsers.SteamLibraryFolders(vdf);
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], folders);
    }

    [Fact]
    public void Vdf_ParsesOldFlatLibraryFolders()
    {
        const string vdf = "\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"1\" \"E:\\\\Games\\\\Steam\" }";
        Assert.Equal([@"E:\Games\Steam"], LauncherParsers.SteamLibraryFolders(vdf));
    }

    [Fact]
    public void SteamManifest_InstalledAppIsAGame()
    {
        const string acf = """
            "AppState"
            {
                "appid"		"1091500"
                "name"		"Cyberpunk 2077"
                "StateFlags"		"4"
                "installdir"		"Cyberpunk 2077"
            }
            """;
        var app = LauncherParsers.SteamManifest(acf);
        Assert.NotNull(app);
        Assert.Equal("1091500", app.AppId);
        Assert.Equal("Cyberpunk 2077", app.Name);
        Assert.Equal("Cyberpunk 2077", app.InstallDir);
    }

    [Theory]
    [InlineData("1026", "Game")]          // still downloading (bit 4 clear)
    [InlineData("4", "..\\escape")]       // path traversal
    [InlineData("4", "C:\\Elsewhere")]    // absolute path
    [InlineData("4", "")]
    public void SteamManifest_RejectsIncompleteOrUnsafe(string flags, string installDir)
    {
        var acf = $"\"AppState\" {{ \"appid\" \"1\" \"StateFlags\" \"{flags}\" \"installdir\" \"{installDir.Replace("\\", "\\\\")}\" }}";
        Assert.Null(LauncherParsers.SteamManifest(acf));
    }

    [Fact]
    public void EpicItem_ParsesCompleteInstall()
    {
        const string json = """{"AppName":"Fortnite","DisplayName":"Fortnite","InstallLocation":"C:/Program Files/Epic Games/Fortnite","LaunchExecutable":"FortniteGame/Binaries/Win64/FortniteLauncher.exe","bIsIncompleteInstall":false}""";
        var game = LauncherParsers.EpicItem(json);
        Assert.NotNull(game);
        Assert.Equal("Epic", game.Launcher);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", game.Path);
    }

    [Fact]
    public void EpicItem_SkipsIncompleteAndExecutableLessEntries()
    {
        Assert.Null(LauncherParsers.EpicItem("""{"InstallLocation":"C:/X","LaunchExecutable":"x.exe","bIsIncompleteInstall":true}"""));
        Assert.Null(LauncherParsers.EpicItem("""{"InstallLocation":"C:/X","LaunchExecutable":""}"""));
    }

    [Fact]
    public void Gog_SkipsDlcAndFallsBackToExeFolder()
    {
        var dlc = new Dictionary<string, string> { ["path"] = @"C:\GOG\Game", ["dependsOn"] = "1207658924" };
        Assert.Null(LauncherParsers.GogGame("1", dlc));

        var exeOnly = new Dictionary<string, string> { ["exe"] = @"C:\GOG\Witcher 3\bin\x64\witcher3.exe", ["gameName"] = "The Witcher 3" };
        var game = LauncherParsers.GogGame("1207664663", exeOnly);
        Assert.NotNull(game);
        Assert.Equal(@"C:\GOG\Witcher 3\bin\x64", game.Path);
        Assert.Equal("The Witcher 3", game.Name);
    }

    [Fact]
    public void BattleNet_RecognizesGamesButNotTheLauncher()
    {
        var game = new Dictionary<string, string>
        {
            ["UninstallString"] = "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=fenris --displayname=\"Diablo IV\"",
            ["InstallLocation"] = @"C:\Program Files (x86)\Diablo IV",
            ["DisplayName"] = "Diablo IV",
        };
        Assert.Equal("Diablo IV", LauncherParsers.BattleNetGame("Diablo IV", game)?.Name);

        var launcher = new Dictionary<string, string>(game) { ["UninstallString"] = "Battle.net.exe --uid=bna" };
        Assert.Null(LauncherParsers.BattleNetGame("Battle.net", launcher));
    }

    [Fact]
    public void GamingRoot_ReadsUtf16FolderAfterHeader()
    {
        var bytes = new byte[] { 0x52, 0x47, 0x42, 0x58, 1, 0, 0, 0 }
            .Concat(System.Text.Encoding.Unicode.GetBytes("\\XboxGames\0junk"))
            .ToArray();
        Assert.Equal(@"\XboxGames", LauncherParsers.GamingRootFolder(bytes));
        Assert.Null(LauncherParsers.GamingRootFolder(new byte[8]));
    }
}

public class AiPauseMatcherTests
{
    private static readonly GameEntry Cyberpunk = new("Steam", "1091500", "Cyberpunk 2077", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077");
    private static readonly GameEntry Custom = new("Custom", "x", "Indie", @"E:\Indie\Game.exe");

    [Fact]
    public void Match_ProcessInsideGameFolder()
    {
        var matcher = new GameMatcher([Cyberpunk], new AiPauseSettings());
        Assert.Equal(Cyberpunk, matcher.Match(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"));
        Assert.Equal(Cyberpunk, matcher.Match(@"d:\steamlibrary\STEAMAPPS\common\cyberpunk 2077\bin\x64\cyberpunk2077.exe"));
    }

    [Fact]
    public void Match_SiblingFolderWithSharedPrefixIsNotTheGame()
    {
        var matcher = new GameMatcher([Cyberpunk], new AiPauseSettings());
        Assert.Null(matcher.Match(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077 Mods\tool.exe"));
    }

    [Fact]
    public void Match_HelpersRedistAndExclusionsNeverCount()
    {
        var settings = new AiPauseSettings { ExcludedExecutables = ["*Updater*.exe"] };
        var matcher = new GameMatcher([Cyberpunk], settings);
        var root = Cyberpunk.Path;
        Assert.Null(matcher.Match($@"{root}\EasyAntiCheat\EasyAntiCheat.exe"));
        Assert.Null(matcher.Match($@"{root}\_CommonRedist\vcredist\setup2.exe"));
        Assert.Null(matcher.Match($@"{root}\REDprelauncher.exe"));
        Assert.Null(matcher.Match($@"{root}\GameUpdaterTool.exe"));
    }

    [Fact]
    public void Match_CustomExecutableIsExactOnly()
    {
        var matcher = new GameMatcher([Custom], new AiPauseSettings());
        Assert.Equal(Custom, matcher.Match(@"E:\Indie\Game.exe"));
        Assert.Null(matcher.Match(@"E:\Indie\Editor.exe"));
    }

    [Fact]
    public void Match_DeepestFolderWins()
    {
        var parent = new GameEntry("Custom", "p", "Collection", @"F:\Games");
        var child = new GameEntry("Custom", "c", "Child", @"F:\Games\Child");
        var matcher = new GameMatcher([parent, child], new AiPauseSettings());
        Assert.Equal(child, matcher.Match(@"F:\Games\Child\child.exe"));
    }

    [Theory]
    [InlineData("GameUpdater.exe", "*updater*", true)]
    [InlineData("a.exe", "?.exe", true)]
    [InlineData("ab.exe", "?.exe", false)]
    [InlineData("CrashReporter.exe", "*crash*.exe", true)]
    public void GlobMatch(string name, string pattern, bool expected) =>
        Assert.Equal(expected, PathUtil.GlobMatch(name, pattern));
}

public class PauseCoordinatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static RunningGame Game(int pid, GameRule rule = GameRule.On) =>
        new(pid, 1000 + pid, $"Game{pid}", $@"C:\Games\Game{pid}", rule);

    [Fact]
    public void GameStartPausesAndExitRestoresAfterDelay()
    {
        var c = new PauseCoordinator { RestoreDelay = TimeSpan.FromSeconds(30) };
        Assert.Equal(CoordinatorAction.Pause, c.Decide(T0, [Game(1)]).Action);
        c.Holding = true;

        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddSeconds(2), [Game(1)]).Action);

        var exited = c.Decide(T0.AddSeconds(10), []);
        Assert.Equal(CoordinatorAction.None, exited.Action);
        Assert.Equal(TimeSpan.FromSeconds(30), exited.CountdownRemaining);

        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddSeconds(39), []).Action);
        Assert.Equal(CoordinatorAction.Restore, c.Decide(T0.AddSeconds(40), []).Action);
    }

    [Fact]
    public void NewGameCancelsCountdown()
    {
        var c = new PauseCoordinator { Holding = true, RestoreDelay = TimeSpan.FromSeconds(30) };
        c.Decide(T0, []);                                           // countdown starts
        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddSeconds(20), [Game(2)]).Action);
        var after = c.Decide(T0.AddSeconds(25), []);                // starts over in full
        Assert.Equal(TimeSpan.FromSeconds(30), after.CountdownRemaining);
    }

    [Fact]
    public void OffGamesAreIgnoredAndAskGamesWaitForApproval()
    {
        var c = new PauseCoordinator();
        Assert.Equal(CoordinatorAction.None, c.Decide(T0, [Game(1, GameRule.Off)]).Action);

        var ask = Game(2, GameRule.Ask);
        var decision = c.Decide(T0, [ask]);
        Assert.Equal(CoordinatorAction.None, decision.Action);
        Assert.Single(decision.AskPending);

        c.ApproveAsk(decision.AskPending);
        Assert.Equal(CoordinatorAction.Pause, c.Decide(T0.AddSeconds(2), [ask]).Action);
    }

    [Fact]
    public void AskApprovalIsPerInstance()
    {
        var c = new PauseCoordinator();
        var first = Game(2, GameRule.Ask);
        c.ApproveAsk([first]);
        c.Decide(T0, []); // the approved instance exits — approval is gone
        var relaunched = first with { CreatedTicks = first.CreatedTicks + 1 };
        var decision = c.Decide(T0.AddSeconds(2), [relaunched]);
        Assert.Equal(CoordinatorAction.None, decision.Action);
        Assert.Single(decision.AskPending);
    }

    [Fact]
    public void AutomationOffDoesNotPauseButFinishesExistingSession()
    {
        var c = new PauseCoordinator { Automation = false, RestoreDelay = TimeSpan.Zero };
        Assert.Equal(CoordinatorAction.None, c.Decide(T0, [Game(1)]).Action);

        c.Holding = true; // a session from before automation was turned off
        Assert.Equal(CoordinatorAction.None, c.Decide(T0, [Game(1)]).Action);
        Assert.Equal(CoordinatorAction.Restore, c.Decide(T0.AddSeconds(1), []).Action);
    }

    [Fact]
    public void FailedDetectionNeverRestoresAndRestartsTheDelay()
    {
        var c = new PauseCoordinator { Holding = true, RestoreDelay = TimeSpan.FromSeconds(30) };
        c.Decide(T0, []);
        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddSeconds(29), null).Action);
        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddSeconds(31), null).Action);
        var back = c.Decide(T0.AddSeconds(40), []);
        Assert.Equal(TimeSpan.FromSeconds(30), back.CountdownRemaining);
    }

    [Fact]
    public void FailedDetectionNeverStartsAnAutomaticPause()
    {
        var c = new PauseCoordinator();
        Assert.Equal(CoordinatorAction.None, c.Decide(T0, null).Action);
    }

    [Fact]
    public void ManualPauseHoldsUntilResume()
    {
        var c = new PauseCoordinator { RestoreDelay = TimeSpan.FromSeconds(30) };
        c.ManualPause();
        Assert.Equal(CoordinatorAction.Pause, c.Decide(T0, []).Action);
        c.Holding = true;
        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddMinutes(5), []).Action);

        c.ManualResume([]);
        Assert.Equal(CoordinatorAction.Restore, c.Decide(T0.AddMinutes(5), []).Action); // no delay after an explicit resume
    }

    [Fact]
    public void ResumeDuringGameKeepsAiUntilANewGameStarts()
    {
        var c = new PauseCoordinator();
        var game = Game(1);
        c.Holding = true;
        c.ManualResume([game]);
        Assert.Equal(CoordinatorAction.Restore, c.Decide(T0, [game]).Action);
        c.Holding = false;

        Assert.Equal(CoordinatorAction.None, c.Decide(T0.AddSeconds(2), [game]).Action);
        Assert.Equal(CoordinatorAction.Pause, c.Decide(T0.AddSeconds(4), [game, Game(2)]).Action);
    }
}

public class AiPauseProviderPlanTests
{
    [Fact]
    public void Ollama_KeepAliveIsFrozenAndIndefiniteIsKept()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        const string ps = """
            {"models":[
              {"name":"llama3:8b","model":"llama3:8b","size":5000,"size_vram":4800,"context_length":8192,"expires_at":"2026-10-10T12:04:00Z"},
              {"name":"forever","model":"forever","size_vram":100,"expires_at":"2318-02-20T00:00:00Z"},
              {"name":"stale","model":"stale","expires_at":"2026-10-10T11:59:00Z"},
              {"name":"cloud","model":"cloud","remote_host":"https://ollama.com"}
            ]}
            """;
        var models = OllamaPlan.ParseResident(ps, now);
        Assert.Equal(3, models.Count); // cloud skipped
        Assert.Equal(new OllamaModel("llama3:8b", 4800, 8192, 240, true), models[0]);
        Assert.Equal(-1, models[1].KeepAliveSeconds);
        Assert.Null(models[2].KeepAliveSeconds);
        Assert.False(models[2].Reload);
    }

    [Fact]
    public void Ollama_EmptyOrMissingModelsIsNothing()
    {
        Assert.Empty(OllamaPlan.ParseResident("""{"models":[]}""", DateTimeOffset.UtcNow));
        Assert.Empty(OllamaPlan.ParseResident("{}", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void LmStudio_ParsesPsAndBuildsLoadCommand()
    {
        const string ps = """
            [{"identifier":"qwen-coder","modelKey":"qwen2.5-coder-14b","type":"llm","contextLength":16384,"ttlMs":600000}]
            """;
        var model = Assert.Single(LmStudioPlan.ParsePs(ps));
        Assert.Equal(new LmStudioModel("qwen-coder", "qwen2.5-coder-14b", 16384, 600), model);
        Assert.Equal(
            ["load", "qwen2.5-coder-14b", "--identifier", "qwen-coder", "--yes", "--context-length", "16384", "--ttl", "600"],
            LmStudioPlan.LoadArguments(model));
    }

    [Fact]
    public void LmStudio_RefusesUnidentifiableModels()
    {
        Assert.Throws<InvalidDataException>(() => LmStudioPlan.ParsePs("""[{"type":"llm"}]"""));
        Assert.Empty(LmStudioPlan.ParsePs("No models are currently loaded"));
    }

    [Theory]
    [InlineData("\"C:\\AI\\llama-server.exe\" -m model.gguf --port 8081", "-m model.gguf --port 8081")]
    [InlineData("C:\\AI\\koboldcpp.exe --config x.kcpps", "--config x.kcpps")]
    [InlineData("\"C:\\AI\\server.exe\"", "")]
    public void ProcessApp_ArgumentsDropArgv0(string commandLine, string expected) =>
        Assert.Equal(expected, ProcessAppProvider.ArgumentsOf(commandLine));

    [Fact]
    public void Journal_RoundTripsProviderRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dm-ai-journal-{Guid.NewGuid():N}.json");
        try
        {
            var journal = new RecoveryJournal
            {
                PausedAt = DateTimeOffset.Now,
                Reason = "Cyberpunk 2077",
                Games = [@"D:\Games\Cyberpunk 2077"],
                Providers =
                {
                    ["ollama"] = new ProviderEntry
                    {
                        Stage = ProviderStage.Unloaded,
                        Record = JsonSerializer.SerializeToElement(new OllamaRecord([new OllamaModel("llama3:8b", 4800, 8192, 240, true)])),
                        Summary = "1 model(s)",
                        Bytes = 4800,
                    },
                },
            };
            journal.Save(path);

            var loaded = RecoveryJournal.Load(path)!;
            Assert.Equal("Cyberpunk 2077", loaded.Reason);
            var entry = loaded.Providers["ollama"];
            Assert.Equal(ProviderStage.Unloaded, entry.Stage);
            var record = entry.Record.Deserialize<OllamaRecord>()!;
            Assert.Equal(240, record.Models[0].KeepAliveSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Settings_RuleLookupIsPathInsensitive()
    {
        var settings = new AiPauseSettings();
        settings.SetRule(@"C:\Games\Foo\", GameRule.Ask);
        Assert.Equal(GameRule.Ask, settings.RuleFor(@"c:\games\foo"));
        settings.SetRule(@"C:\Games\Foo", GameRule.On);
        Assert.Empty(settings.GameRules); // On is the default and isn't stored
    }
}
