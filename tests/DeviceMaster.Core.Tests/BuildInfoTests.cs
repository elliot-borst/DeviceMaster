using DeviceMaster.Control;

namespace DeviceMaster.Core.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.0.0+e92828bc20271b04e2daa2786d1a279532a5c55b", "e92828b")]
    [InlineData("101.0.0+abc12", "abc12")]
    [InlineData("1.0.0", null)]
    [InlineData("1.0.0+", null)]
    public void ParseCommit_TakesTheShortShaAfterPlus(string version, string? expected) =>
        Assert.Equal(expected, BuildInfo.ParseCommit(version));

    [Fact]
    public void StatusCarriesTheBuildVersion() =>
        Assert.Equal(BuildInfo.Version, new ControlStatus().Version);
}
