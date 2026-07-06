using System.Text.RegularExpressions;

namespace DeviceMaster.Core.Updating;

/// <summary>
/// Whole-number release versioning (1, 2, 3, …). Tags are compared numerically segment by
/// segment ("v2" &gt; "v1", "v2.1" &gt; "v2"), missing segments count as zero.
/// </summary>
public static partial class WholeVersion
{
    public static int[] Parse(string? tag) =>
        tag is null ? [] : NumberPattern().Matches(tag).Select(m => int.Parse(m.Value)).ToArray();

    public static int Compare(int[] a, int[] b)
    {
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }

        return 0;
    }

    [GeneratedRegex("[0-9]+")]
    private static partial Regex NumberPattern();
}
