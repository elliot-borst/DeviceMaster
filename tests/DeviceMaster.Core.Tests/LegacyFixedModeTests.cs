using System.Text.Json;
using DeviceMaster.App.Headless;
using DeviceMaster.Control;
using DeviceMaster.Core.Curves;

namespace DeviceMaster.Core.Tests;

/// <summary>
/// The Fixed fan mode was removed (#116): a constant speed is a curve with both points at one duty.
/// Everything saved or sent in Fixed mode ("Mode": "Manual" or 1, with ManualDutyPercent) must load as
/// Curve with a flat curve at that duty on the same source, so no machine's fans change speed.
/// </summary>
public class LegacyFixedModeTests
{
    [Fact]
    public void AFixedWindowsConfigLoadsAsAFlatCurveAtItsDuty()
    {
        var settings = ControlSettings.FromJson("""
            {
              "Mode": "Manual", "ManualDutyPercent": 35, "Source": "Cpu",
              "CurvePoints": [ { "TemperatureC": 50, "DutyPercent": 30 }, { "TemperatureC": 90, "DutyPercent": 100 } ],
              "RgbR": 7
            }
            """);

        Assert.Equal(ControlMode.Curve, settings.Mode);
        Assert.Equal(CurveSource.Cpu, settings.Source);                       // the source it had
        Assert.Equal([new CurvePoint(50, 35), new CurvePoint(90, 35)], settings.CurvePoints); // its curve's temperatures, flat
        Assert.All(new[] { 20.0, 49, 70, 90, 100 }, t => Assert.Equal(35, settings.Curve.EvaluateDuty(t)));
        Assert.Equal(7, settings.RgbR);                                       // the rest of the document still loads
    }

    [Fact]
    public void ARewrittenFixedConfigIsAPlainCurveConfig()
    {
        var json = ControlSettings.FromJson("""{"Mode":"Manual","ManualDutyPercent":60}""").ToJson();

        Assert.Contains("\"Mode\": \"Curve\"", json);
        Assert.DoesNotContain("ManualDutyPercent", json);
        Assert.DoesNotContain("Manual", json);
        var reloaded = ControlSettings.FromJson(json);
        Assert.Equal(ControlMode.Curve, reloaded.Mode);
        Assert.Equal(CurveRules.Flat(60), reloaded.CurvePoints);
    }

    [Theory]
    [InlineData("\"Manual\"")]
    [InlineData("\"manual\"")]
    [InlineData("\"Fixed\"")]
    [InlineData("1")]
    public void EveryFormOfTheFixedModeMigrates(string mode)
    {
        var settings = ControlSettings.FromJson($$"""{"Mode":{{mode}},"ManualDutyPercent":25}""");
        Assert.Equal(ControlMode.Curve, settings.Mode);
        Assert.Equal(CurveRules.Flat(25), settings.CurvePoints);
    }

    [Fact]
    public void AFixedModeWithoutASavedDutyRunsAtTheOldDefault() =>
        Assert.Equal(CurveRules.Flat(50), ControlSettings.FromJson("""{"Mode":"Manual"}""").CurvePoints);

    [Fact]
    public void AFixedDutyOutsideTheFanRangeIsClamped() =>
        Assert.Equal(CurveRules.Flat(100), ControlSettings.FromJson("""{"Mode":"Manual","ManualDutyPercent":140}""").CurvePoints);

    [Fact]
    public void AFixedDutyInCurveModeIsIgnoredAsBefore()
    {
        var settings = ControlSettings.FromJson("""{"Mode":"Curve","ManualDutyPercent":10}""");
        Assert.Equal(ControlMode.Curve, settings.Mode);
        Assert.Equal(CurveRules.Default, settings.CurvePoints);
    }

    [Theory]
    [InlineData("\"Off\"", ControlMode.Off)]
    [InlineData("0", ControlMode.Off)]
    [InlineData("\"Curve\"", ControlMode.Curve)]
    [InlineData("2", ControlMode.Curve)]
    public void TheRemainingModesStillLoad(string json, ControlMode expected)
    {
        var settings = ControlSettings.FromJson($$"""{"Mode":{{json}}}""");
        Assert.Equal(expected, settings.Mode);
        Assert.Equal(CurveRules.Default, settings.CurvePoints);
    }

    [Fact]
    public void AFixedCoolantConfigLandsOnHottestAtItsFixedDuty()
    {
        // both removals at once: coolant source (#96) and Fixed mode (#116)
        var settings = ControlSettings.FromJson("""
            {"Mode":"Manual","ManualDutyPercent":40,"Source":"Coolant",
             "CurvePoints":[{"TemperatureC":25,"DutyPercent":30},{"TemperatureC":38,"DutyPercent":100}]}
            """);

        Assert.Equal(CurveSource.Hottest, settings.Source);
        Assert.Equal(ControlMode.Curve, settings.Mode);
        Assert.Equal(CurveRules.Flat(40), settings.CurvePoints);
    }

    [Fact]
    public void AFixedHeadlessConfigLoadsAsAFlatCurve()
    {
        var config = HeadlessConfig.Deserialize("""
            { "Control": { "Mode": "Manual", "ManualDutyPercent": 55, "Source": "Gpu" }, "I2cDevice": "/dev/i2c-3" }
            """);

        Assert.NotNull(config);
        Assert.Equal(ControlMode.Curve, config.Control.Mode);
        Assert.Equal(CurveSource.Gpu, config.Control.Source);
        Assert.Equal(CurveRules.Flat(55), config.Control.CurvePoints);
        Assert.Equal("/dev/i2c-3", config.I2cDevice);
    }

    // the hub's fleet-state.json with fleet cooling in Fixed mode, one machine with its own Fixed duty
    // and one with its own curve
    private const string FixedFleetState = """
        {
          "Profile": {
            "Mode": "Manual", "ManualDutyPercent": 40, "Source": "Hottest",
            "CurvePoints": [ { "TemperatureC": 45, "DutyPercent": 30 }, { "TemperatureC": 80, "DutyPercent": 100 } ],
            "RgbEnabled": true
          },
          "Managed": [ "Cooling" ],
          "Overrides": {
            "quiet": { "Mode": "Manual", "ManualDutyPercent": 25, "Source": "Hottest", "CurvePoints": null },
            "own": { "Mode": "Curve", "ManualDutyPercent": 90, "Source": "Hottest",
                     "CurvePoints": [ { "TemperatureC": 50, "DutyPercent": 20 }, { "TemperatureC": 90, "DutyPercent": 100 } ] }
          },
          "Version": 7
        }
        """;

    [Fact]
    public void AFixedFleetStateAndItsOverridesLoadAsFlatCurves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dm-fixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "fleet-state.json");
            File.WriteAllText(path, FixedFleetState);

            var state = new FleetStore(path).State;

            Assert.Equal(7, state.Version);
            Assert.Equal(ControlMode.Curve, state.Profile.Mode);
            Assert.Equal([new CurvePoint(45, 40), new CurvePoint(80, 40)], state.Profile.CurvePoints);

            var quiet = state.Overrides["quiet"];
            Assert.Equal(ControlMode.Curve, quiet.Mode);
            Assert.Equal(CurveRules.Flat(25), quiet.CurvePoints);
            Assert.True(FleetOverrides.Has(quiet, FleetSection.Cooling));

            var own = state.Overrides["own"];
            Assert.Equal([new CurvePoint(50, 20), new CurvePoint(90, 100)], own.CurvePoints); // its curve, untouched

            // what each machine is sent keeps its fans where they were
            var current = FleetProfile.From(new ControlSettings { Mode = ControlMode.Curve });
            Assert.All(new[] { 30.0, 60, 95 }, t =>
            {
                Assert.Equal(40, new FanCurve(state.DesiredFor("other", current).CurvePoints).EvaluateDuty(t));
                Assert.Equal(25, new FanCurve(state.DesiredFor("quiet", current).CurvePoints).EvaluateDuty(t));
            });
            Assert.Equal(ControlMode.Curve, state.DesiredFor("quiet", current).Mode);

            var machine = new ControlSettings();
            Assert.True(state.DesiredFor("quiet", FleetProfile.From(machine)).ApplyTo(machine));
            Assert.Equal(ControlMode.Curve, machine.Mode);
            Assert.Equal(CurveRules.Flat(25), machine.CurvePoints);

            // and it saves back without the Fixed mode
            var saved = JsonSerializer.Serialize(state, AgentApi.Json);
            Assert.DoesNotContain("Manual", saved);
            Assert.DoesNotContain("ManualDutyPercent", saved);
            var reloaded = JsonSerializer.Deserialize<FleetState>(saved, AgentApi.Json)!;
            Assert.Equal(state.Profile, reloaded.Profile);
            Assert.Equal(CurveRules.Flat(25), reloaded.Overrides["quiet"].CurvePoints);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AFixedProfilePushFromAnOlderHubAppliesAsAFlatCurve()
    {
        var incoming = JsonSerializer.Deserialize<FleetProfile>("""
            {"Mode":"Manual","ManualDutyPercent":60,"Source":"Hottest",
             "CurvePoints":[{"TemperatureC":40,"DutyPercent":30},{"TemperatureC":85,"DutyPercent":100}],"RgbR":9}
            """, AgentApi.Json)!;
        var machine = new ControlSettings { Mode = ControlMode.Curve, Source = CurveSource.Cpu };

        Assert.Equal(ControlMode.Curve, incoming.Mode);
        Assert.True(incoming.ApplyTo(machine));
        Assert.Equal(ControlMode.Curve, machine.Mode);
        Assert.Equal(CurveSource.Hottest, machine.Source);
        Assert.Equal([new CurvePoint(40, 60), new CurvePoint(85, 60)], machine.CurvePoints);
        Assert.Equal(9, machine.RgbR);
    }

    [Fact]
    public void AFixedProfileFromAnOlderAgentLoadsAsAFlatCurve()
    {
        // the hub reads each agent's profile; an agent still on an older build may report Fixed (numbers or names)
        var profile = JsonSerializer.Deserialize<FleetProfile>("""{"Mode":1,"ManualDutyPercent":30}""")!;
        Assert.Equal(ControlMode.Curve, profile.Mode);
        Assert.Equal(CurveRules.Flat(30), profile.CurvePoints);
    }

    [Fact]
    public void AnOlderAgentsFixedStatusStillReads()
    {
        var status = JsonSerializer.Deserialize<ControlStatus>("""{"Running":true,"Mode":"Manual","TargetDutyPercent":40}""", AgentApi.Json)!;
        Assert.True(status.Running);
        Assert.Equal(ControlMode.Curve, status.Mode);
        Assert.Equal(ControlMode.Curve, JsonSerializer.Deserialize<ControlStatus>("""{"Mode":1}""")!.Mode);
    }

    [Fact]
    public void ProfilesKeepTheirModeOnTheWire()
    {
        var json = JsonSerializer.Serialize(new FleetProfile(), AgentApi.Json);
        Assert.Contains("\"Mode\":\"Curve\"", json);
        Assert.DoesNotContain("ManualDutyPercent", json);
        Assert.Equal(ControlMode.Curve, JsonSerializer.Deserialize<FleetProfile>(json, AgentApi.Json)!.Mode);

        var off = JsonSerializer.Deserialize<FleetOverride>(JsonSerializer.Serialize(new FleetOverride { Mode = ControlMode.Off }, AgentApi.Json), AgentApi.Json)!;
        Assert.Equal(ControlMode.Off, off.Mode);
        Assert.Null(JsonSerializer.Deserialize<FleetOverride>("""{"Mode":null}""", AgentApi.Json)!.Mode);
        Assert.Null(JsonSerializer.Deserialize<FleetOverride>("{}", AgentApi.Json)!.Mode);
    }

    [Fact]
    public void NoCodePathOffersAFixedMode()
    {
        Assert.Equal(["Off", "Curve"], Enum.GetNames<ControlMode>());
        Assert.False(Enum.IsDefined(typeof(ControlMode), ControlModeJsonConverter.LegacyFixed));

        foreach (var page in new[] { DeviceMaster.Hub.AgentDashboardPage.Html, DeviceMaster.Hub.HubWebServer.Page })
        {
            Assert.DoesNotContain(">Fixed<", page);   // no Fixed button or chip
            Assert.DoesNotContain("\"Fixed\"", page);
            Assert.DoesNotContain("Manual", page);
            Assert.DoesNotContain("fanDuty", page);
            Assert.DoesNotContain("modeChips", page);
        }
    }

    [Fact]
    public void AFlatCurveHoldsItsDutyAndStillFailsSafe()
    {
        var flat = CurveRules.Flat(45);
        Assert.Equal(CurveRules.Normalize(flat), flat); // passes the shared curve rules unchanged
        Assert.Equal(45, new FanCurve(flat).EvaluateDuty(20));
        Assert.Equal(45, new FanCurve(flat).EvaluateDuty(100));
        Assert.Equal(100, DeviceMaster.Core.Safety.SafetyGuard.DutyOnSensorFailure()); // no reading: 100 %, not the flat duty
    }
}
