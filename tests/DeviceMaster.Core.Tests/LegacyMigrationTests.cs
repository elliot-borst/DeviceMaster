using System.Text.Json;
using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// Pump, coolant and pump/fan LCD support was removed (#96). Configs, the hub's fleet state and
/// profile pushes written before that must still load — never fall back to defaults or fail the
/// whole document — and a coolant curve source must come back as Hottest.
/// </summary>
public class LegacyMigrationTests
{
    // a Windows config.json as an older build wrote it: coolant source with its coolant curve,
    // pump duty, pump/fan LCD settings, and a Hottest curve remembered for later
    private const string OldWindowsConfig = """
        {
          "Mode": "Curve",
          "ManualDutyPercent": 45,
          "PumpDutyPercent": 70,
          "Source": "Coolant",
          "CurvePoints": [ { "TemperatureC": 25, "DutyPercent": 30 }, { "TemperatureC": 38, "DutyPercent": 100 } ],
          "SourceCurves": {
            "Hottest": [ { "TemperatureC": 45, "DutyPercent": 35 }, { "TemperatureC": 80, "DutyPercent": 100 } ],
            "Cpu": [ { "TemperatureC": 50, "DutyPercent": 30 }, { "TemperatureC": 90, "DutyPercent": 100 } ]
          },
          "RgbEnabled": true,
          "RgbR": 10, "RgbG": 20, "RgbB": 30,
          "LcdScreens": "Metrics",
          "LcdBrightness": 40,
          "PumpScreenMetric": "Coolant",
          "FanScreenMetric": "PumpRpm",
          "LcdScreenConfigs": [ { "Id": "pump-lcd", "Group": "", "Metric": "Coolant", "RotationDegrees": 90, "ColorByValue": true } ],
          "TurzxScreen": "Metrics",
          "TurzxMetric": "Coolant",
          "TurzxBrightness": 60,
          "BlackoutPrevLcd": "Metrics",
          "SeenFanIds": [ "fan-1" ]
        }
        """;

    [Fact]
    public void AnOldWindowsConfigLoadsAndRunsOnHottest()
    {
        var settings = ControlSettings.FromJson(OldWindowsConfig);

        Assert.Equal(CurveSource.Hottest, settings.Source);
        // the remembered Hottest curve, not the 25–38 °C coolant curve (that would pin a CPU at 100 %)
        Assert.Equal([new CurvePoint(45, 35), new CurvePoint(80, 100)], settings.CurvePoints);
        Assert.Equal(100, settings.Curve.EvaluateDuty(80));
        Assert.Equal(35, settings.Curve.EvaluateDuty(40));
        Assert.False(settings.SourceCurves.ContainsKey("Coolant"));

        // everything else survives
        Assert.Equal(ControlMode.Curve, settings.Mode); // Curve mode: the old fixed duty (45) is ignored, as it was
        Assert.Equal((10, 20, 30), (settings.RgbR, settings.RgbG, settings.RgbB));
        Assert.Equal(LcdMode.Metrics, settings.TurzxScreen);
        Assert.Equal(LcdMetric.CpuTemp, settings.TurzxMetric); // the coolant metric is gone
        Assert.Equal(60, settings.TurzxBrightness);
        Assert.Equal(["fan-1"], settings.SeenFanIds);

        // switching away and back still finds the per-source curves
        settings.SetSource(CurveSource.Cpu);
        Assert.Equal([new CurvePoint(50, 30), new CurvePoint(90, 100)], settings.CurvePoints);
    }

    [Fact]
    public void ARewrittenOldConfigNoLongerMentionsPumpsOrCoolant()
    {
        var json = ControlSettings.FromJson(OldWindowsConfig).ToJson();

        Assert.DoesNotContain("Coolant", json);
        Assert.DoesNotContain("Pump", json);
        Assert.DoesNotContain("LcdScreens", json);
        Assert.DoesNotContain("ManualDutyPercent", json);
        Assert.Contains("\"Source\": \"Hottest\"", json);
        Assert.Equal(CurveSource.Hottest, ControlSettings.FromJson(json).Source);
    }

    [Fact]
    public void ACoolantSourceWithoutARememberedHottestCurveGetsTheDefault()
    {
        var settings = ControlSettings.FromJson("""
            {"Source":"Coolant","CurvePoints":[{"TemperatureC":25,"DutyPercent":30},{"TemperatureC":38,"DutyPercent":100}]}
            """);

        Assert.Equal(CurveSource.Hottest, settings.Source);
        Assert.Equal(CurveRules.Default, settings.CurvePoints);
    }

    [Theory]
    [InlineData("\"Source\":0")]          // numeric coolant
    [InlineData("\"Source\":\"coolant\"")] // any case
    [InlineData("\"Source\":\"Pump\"")]    // anything unknown
    public void AnyRemovedSourceReadsAsHottest(string source)
    {
        var settings = ControlSettings.FromJson($$"""{{{source}}}""");
        Assert.Equal(CurveSource.Hottest, settings.Source);
    }

    [Fact]
    public void CurrentSourcesStillLoadByNameAndNumber()
    {
        Assert.Equal(CurveSource.Cpu, ControlSettings.FromJson("""{"Source":"Cpu"}""").Source);
        Assert.Equal(CurveSource.Gpu, ControlSettings.FromJson("""{"Source":2}""").Source);
        Assert.Equal(CurveSource.Hottest, ControlSettings.FromJson("""{"Source":"Hottest"}""").Source);
    }

    [Fact]
    public void AnOldHeadlessConfigLoadsAndRunsOnHottest()
    {
        // the Linux agents ran on the coolant source by default
        var config = HeadlessConfig.Deserialize("""
            {
              "Control": { "Mode": "Curve", "Source": "Coolant", "PumpDutyPercent": 80, "LcdScreens": "Metrics",
                           "LcdBrightness": 100, "PumpScreenMetric": "PumpDuty" },
              "I2cDevice": "/dev/i2c-7"
            }
            """);

        Assert.NotNull(config);
        Assert.Equal(CurveSource.Hottest, config.Control.Source);
        Assert.Equal(CurveRules.Default, config.Control.CurvePoints);
        Assert.Equal(ControlMode.Curve, config.Control.Mode);
        Assert.Equal("/dev/i2c-7", config.I2cDevice); // the rest of the document still loads
    }

    // the hub's fleet-state.json from before the change: pump duty in the cooling section, pump/fan
    // LCD fields in the screens section, and a machine override that still says Coolant
    private const string OldFleetState = """
        {
          "Profile": {
            "Mode": "Curve", "ManualDutyPercent": 50, "PumpDutyPercent": 90, "Source": "Hottest",
            "CurvePoints": [ { "TemperatureC": 40, "DutyPercent": 30 }, { "TemperatureC": 85, "DutyPercent": 100 } ],
            "RgbEnabled": true, "RgbR": 1, "RgbG": 2, "RgbB": 3,
            "LcdScreens": "Metrics", "LcdBrightness": 30, "PumpScreenMetric": "Coolant", "FanScreenMetric": "PumpRpm",
            "TurzxScreen": "Metrics", "TurzxMetric": "Coolant", "TurzxBrightness": 80
          },
          "Managed": [ "Lighting", "Screens", "Cooling" ],
          "Overrides": {
            "box": { "Source": "Coolant", "PumpDutyPercent": 60, "LcdScreens": "Off", "PumpScreenMetric": "PumpRpm", "TurzxMetric": "PumpDuty" }
          },
          "Version": 41
        }
        """;

    [Fact]
    public void AnOldFleetStateLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dm-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "fleet-state.json");
            File.WriteAllText(path, OldFleetState);

            var state = new FleetStore(path).State;

            Assert.Equal(41, state.Version);
            Assert.Equal(CurveSource.Hottest, state.Profile.Source);
            Assert.Equal(LcdMode.Metrics, state.Profile.TurzxScreen);
            Assert.Equal(LcdMetric.CpuTemp, state.Profile.TurzxMetric);
            Assert.Equal(80, state.Profile.TurzxBrightness);
            Assert.Equal([FleetSection.Lighting, FleetSection.Screens, FleetSection.Cooling], state.Managed);

            var box = state.Overrides["box"];
            Assert.Equal(CurveSource.Hottest, box.Source);
            Assert.Equal(LcdMetric.CpuTemp, box.TurzxMetric);
            Assert.True(FleetOverrides.Has(box, FleetSection.Cooling));

            // what the machine is then sent runs on Hottest
            var desired = state.DesiredFor("box", FleetProfile.From(new ControlSettings { Source = CurveSource.Cpu }));
            Assert.Equal(CurveSource.Hottest, desired.Source);

            // and it saves back without the removed fields
            var saved = JsonSerializer.Serialize(state, AgentApi.Json);
            Assert.DoesNotContain("Pump", saved);
            Assert.DoesNotContain("Coolant", saved);
            Assert.DoesNotContain("LcdScreens", saved);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AProfilePushFromAnOlderHubAppliesOnHottest()
    {
        var incoming = JsonSerializer.Deserialize<FleetProfile>("""
            {"Mode":"Curve","Source":"Coolant","PumpDutyPercent":60,
             "CurvePoints":[{"TemperatureC":40,"DutyPercent":30},{"TemperatureC":85,"DutyPercent":100}],
             "LcdScreens":"Metrics","LcdBrightness":40,"PumpScreenMetric":"Coolant","FanScreenMetric":"PumpRpm",
             "TurzxScreen":"Metrics","TurzxMetric":"Coolant","TurzxBrightness":70}
            """, AgentApi.Json)!;
        var machine = new ControlSettings { Source = CurveSource.Cpu };

        Assert.Equal(CurveSource.Hottest, incoming.Source);
        Assert.True(incoming.ApplyTo(machine));
        Assert.Equal(CurveSource.Hottest, machine.Source);
        Assert.Equal([new CurvePoint(40, 30), new CurvePoint(85, 100)], machine.CurvePoints);
        Assert.Equal(LcdMetric.CpuTemp, machine.TurzxMetric);
        Assert.Equal(70, machine.TurzxBrightness);
    }

    [Fact]
    public void EnumsKeepTheirWireShape()
    {
        // names where the serializer uses string enums (the fleet API, config files), numbers elsewhere
        var profile = new FleetProfile { Source = CurveSource.Gpu, TurzxMetric = LcdMetric.GpuTemp };
        Assert.Contains("\"Source\":\"Gpu\"", JsonSerializer.Serialize(profile, AgentApi.Json));
        Assert.Contains("\"Source\":2", JsonSerializer.Serialize(profile));
        Assert.Contains("\"TurzxMetric\":2", JsonSerializer.Serialize(profile));

        var roundTrip = JsonSerializer.Deserialize<FleetOverride>(
            JsonSerializer.Serialize(new FleetOverride { Source = CurveSource.Cpu }, AgentApi.Json), AgentApi.Json)!;
        Assert.Equal(CurveSource.Cpu, roundTrip.Source);
        Assert.Null(JsonSerializer.Deserialize<FleetOverride>("{}", AgentApi.Json)!.Source);
        Assert.Null(JsonSerializer.Deserialize<FleetOverride>("""{"Source":null}""", AgentApi.Json)!.Source);
    }
}
