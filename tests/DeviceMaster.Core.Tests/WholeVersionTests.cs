using DeviceMaster.Core.Updating;

namespace DeviceMaster.Core.Tests;

public class WholeVersionTests
{
    [Theory]
    [InlineData("v1", new[] { 1 })]
    [InlineData("2", new[] { 2 })]
    [InlineData("v2.1", new[] { 2, 1 })]
    [InlineData("release-55", new[] { 55 })]
    [InlineData("", new int[0])]
    [InlineData(null, new int[0])]
    public void Parse_ExtractsNumericSegments(string? tag, int[] expected) =>
        Assert.Equal(expected, WholeVersion.Parse(tag));

    [Theory]
    [InlineData("v2", "v1", 1)]
    [InlineData("v1", "v2", -1)]
    [InlineData("v3", "v3", 0)]
    [InlineData("v2", "v1.9", 1)]     // 2 > 1.9 numerically by segment
    [InlineData("v2.1", "v2", 1)]     // extra segment beats missing (zero)
    [InlineData("v2.0", "v2", 0)]     // trailing zero equals missing
    [InlineData("v10", "v9", 1)]      // numeric, not lexicographic
    public void Compare_OrdersWholeNumberTags(string a, string b, int expected) =>
        Assert.Equal(expected, WholeVersion.Compare(WholeVersion.Parse(a), WholeVersion.Parse(b)));
}
