using DeviceMaster.Control;

namespace DeviceMaster.Core.Tests;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aabbccddeeff")]
    public void ParsesTheUsualMacSpellings(string mac) =>
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, WakeOnLan.ParseMac(mac));

    [Theory]
    [InlineData("aa:bb:cc:dd:ee")]
    [InlineData("zz:bb:cc:dd:ee:ff")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsWhatIsNotAMac(string? mac) => Assert.Null(WakeOnLan.ParseMac(mac));

    [Fact]
    public void TheMagicPacketIsSixFfThenTheMacSixteenTimes()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var packet = WakeOnLan.MagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(mac, packet[(6 + i * 6)..(12 + i * 6)]);
        }
    }
}

public class ShutdownRequestTests
{
    [Fact]
    public void WritesAndWithdrawsTheRequestFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dm-power-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(ShutdownRequest.Write(dir, "fleet hub, owner@example.com"));
            var file = Path.Combine(dir, ShutdownRequest.FileName);
            Assert.Contains("owner@example.com", File.ReadAllText(file));
            Assert.True(ShutdownRequest.Cancel(dir));
            Assert.False(File.Exists(file));
            Assert.False(ShutdownRequest.Cancel(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoSharedFolderMeansNoShutdown() =>
        Assert.False(ShutdownRequest.Write(Path.Combine(Path.GetTempPath(), $"dm-missing-{Guid.NewGuid():N}"), "x"));
}

public class HomeDataTests
{
    [Fact]
    public void DockerCountsRunningAndNamesTheStoppedOnes()
    {
        var info = DockerProbe.Parse("""
            [{"Names":["/plex"],"State":"running"},{"Names":["/sab"],"State":"running"},{"Names":["/old-db"],"State":"exited"}]
            """);
        Assert.Equal(2, info.Running);
        Assert.Equal(3, info.Total);
        Assert.Equal(["old-db"], info.Stopped);
    }

    [Fact]
    public void SabnzbdQueueIsRead()
    {
        var s = SabnzbdStatus.Parse("""
            {"queue":{"status":"Downloading","paused":false,"kbpersec":"48213.5","sizeleft":"38.2 GB","timeleft":"0:13:31","noofslots":6,
              "slots":[{"filename":"Some.File"}]}}
            """);
        Assert.True(s.Reachable);
        Assert.Equal(48213.5, s.KBps);
        Assert.Equal(6, s.Items);
        Assert.Equal("Some.File", s.Current);
        Assert.False(s.Paused);
    }

    [Fact]
    public void StoragePathsTakeAnOptionalLabel()
    {
        var temp = Path.GetTempPath();
        var disks = SystemInfo.Storage([$"Temp={temp}", Path.Combine(temp, $"missing-{Guid.NewGuid():N}")]);
        var d = Assert.Single(disks); // a missing mount just isn't listed
        Assert.Equal("Temp", d.Name);
        Assert.True(d.TotalBytes > 0);
    }

    [Fact]
    public void UptimeIsPositive() => Assert.True(SystemInfo.UptimeSeconds() > 0);
}

public class HubPowerTests
{
    [Fact]
    public async Task PowerOnNeedsAMacAndAWakingMachine()
    {
        using var hub = new FleetHub(new HubConfig
        {
            Agents = [new HubAgent { Name = "A", Url = "http://localhost:1", TokenFile = "x" },
                      new HubAgent { Name = "B", Url = "http://localhost:1", TokenFile = "x", Mac = "aa:bb:cc:dd:ee:ff" }],
        });

        Assert.False(hub.CanWake("B"));                                // no WakeVia configured
        Assert.Equal(409, (await hub.PowerAsync("B", "on", "t")).Status);
        Assert.Equal(400, (await hub.PowerAsync("B", "explode", "t")).Status);

        using var withVia = new FleetHub(new HubConfig { WakeVia = "A", Agents = [.. hub.Machines.Select(m => new HubAgent { Name = m.Name, Url = m.Url, TokenFile = "x", Mac = m.Name == "B" ? "aa:bb:cc:dd:ee:ff" : null })] });
        Assert.True(withVia.CanWake("B"));
        Assert.False(withVia.CanWake("A")); // the waking machine can't wake itself
    }
}

public class RebootRequestTests
{
    [Fact]
    public void ARebootRequestUsesItsOwnFileAndCancelClearsEither()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dm-reboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(ShutdownRequest.Write(dir, "t", reboot: true));
            Assert.True(File.Exists(Path.Combine(dir, ShutdownRequest.RebootFileName)));
            Assert.False(File.Exists(Path.Combine(dir, ShutdownRequest.FileName)));
            Assert.True(ShutdownRequest.Cancel(dir));
            Assert.False(File.Exists(Path.Combine(dir, ShutdownRequest.RebootFileName)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheHubKnowsReboot()
    {
        using var hub = new FleetHub(new HubConfig { Agents = [new HubAgent { Name = "A", Url = "http://localhost:1", TokenFile = "missing" }] });
        Assert.Equal(502, (await hub.PowerAsync("A", "reboot", "t")).Status); // forwarded (agent unreachable), not rejected as unknown
    }
}

public class ColorPaletteTests
{
    public static TheoryData<string> Pages() =>
        [DeviceMaster.App.Headless.HeadlessWebServer.DashboardHtml, DeviceMaster.App.Headless.HubWebServer.Page, DeviceMaster.App.Headless.HubDisplayPage.Html];

    [Fact]
    public void TheOwnersColoursAreVividAndNamed()
    {
        var names = DeviceMaster.Core.Lighting.ColorPalette.All.Select(c => c.Name).ToList();
        Assert.Equal(["White", "Red", "Orange", "Yellow", "Green", "Teal", "Blue", "Purple", "Pink"], names);
        Assert.Equal("ff0000", DeviceMaster.Core.Lighting.ColorPalette.All.Single(c => c.Name == "Red").Hex); // pure red, not a dull mix
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryPageGetsTheOnePaletteAndNoColourPicker(string page)
    {
        Assert.Contains(DeviceMaster.Core.Lighting.ColorPalette.Placeholder, page); // filled when served, never hard-coded
        var served = DeviceMaster.Core.Lighting.ColorPalette.Inject(page);
        Assert.Contains("[\"ff0000\",\"Red\",\"fff\"]", served); // white tick on red: the selection shows on every colour
        Assert.DoesNotContain("type=\"color\"", served);
        Assert.DoesNotContain("ff2222", served); // the old dull "red"
    }
}
