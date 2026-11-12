using System.Reflection;

namespace DeviceMaster.Control;

/// <summary>
/// Identity of the running build, from the entry assembly's informational version. The .NET
/// SDK appends the source commit when building from a git checkout ("101.0.0+&lt;sha&gt;"), so a
/// deployment can be matched to the exact commit it runs.
/// </summary>
public static class BuildInfo
{
    /// <summary>Full informational version, e.g. "1.0.0+e92828bc…"; "unknown" when unavailable.</summary>
    public static string Version { get; } = Read();

    /// <summary>Short (7-char) source commit, or null when the build carried no commit.</summary>
    public static string? Commit { get; } = ParseCommit(Version);

    /// <summary>The commit part after '+', shortened to 7 characters; null when absent.</summary>
    public static string? ParseCommit(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus == informationalVersion.Length - 1)
        {
            return null;
        }

        var sha = informationalVersion[(plus + 1)..];
        return sha.Length > 7 ? sha[..7] : sha;
    }

    private static string Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
