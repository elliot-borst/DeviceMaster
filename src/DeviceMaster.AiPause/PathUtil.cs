namespace DeviceMaster.AiPause;

/// <summary>Windows path comparisons: case-insensitive, separator-normalized, no trailing slash.</summary>
public static class PathUtil
{
    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var normalized = path.Trim().Trim('"').Replace('/', '\\');
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // relative or malformed — compare as given
        }

        return normalized.TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or anywhere below it.</summary>
    public static bool IsInside(string path, string root)
    {
        var p = Canonical(path);
        var r = Canonical(root);
        return r.Length > 0
            && (p == r || (p.Length > r.Length && p.StartsWith(r, StringComparison.Ordinal) && p[r.Length] == '\\'));
    }

    /// <summary>Case-insensitive glob (* and ?) match against a file name.</summary>
    public static bool GlobMatch(string name, string pattern)
    {
        return Match(0, 0);

        bool Match(int n, int p)
        {
            while (p < pattern.Length)
            {
                var c = pattern[p];
                if (c == '*')
                {
                    for (var skip = n; skip <= name.Length; skip++)
                    {
                        if (Match(skip, p + 1))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (n >= name.Length || (c != '?' && char.ToLowerInvariant(c) != char.ToLowerInvariant(name[n])))
                {
                    return false;
                }

                n++;
                p++;
            }

            return n == name.Length;
        }
    }

    /// <summary>Whether any process with this name (no .exe) is running; disposes what it enumerates.</summary>
    public static bool IsRunning(string processName)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(processName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}
