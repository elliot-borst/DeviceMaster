using System.Security.Cryptography;
using System.Text;

namespace DeviceMaster.Control;

/// <summary>
/// One automation allowed to change fleet settings without a signed-in tailnet user (#83): an agent
/// or a scheduled job ("blackout at night"). Its token lives in a file; the hub never logs it.
/// </summary>
public sealed record HubAutomation
{
    /// <summary>Shown in the audit log as "&lt;Name&gt; (automation)".</summary>
    public string Name { get; init; } = "";

    /// <summary>File holding the bearer token (mounted read-only into the hub). Never the token itself.</summary>
    public string TokenFile { get; init; } = "";
}

/// <summary>
/// The automation tokens a hub accepts (#83). A request carrying <c>Authorization: Bearer &lt;token&gt;</c>
/// for one of them counts as that automation's user, with the same reach as a signed-in tailnet user
/// on the hub's own page: fleet settings and power, every change audited under its name. Peer
/// (hub-to-hub) endpoints stay peer-token only. Revoke one by deleting its token file and restarting.
/// </summary>
public sealed class HubAutomations
{
    /// <summary>Tokens shorter than this are refused at load: a guessable token is worse than none.</summary>
    public const int MinimumTokenLength = 32;

    private readonly List<(string User, byte[] Token)> _entries = [];

    /// <summary>Names of the automations whose token loaded (for the startup log).</summary>
    public IReadOnlyList<string> Names => _entries.Select(e => e.User).ToList();

    public int Count => _entries.Count;

    /// <summary>
    /// Loads each automation's token file; entries without a name or a readable, long enough token are
    /// skipped and reported through <paramref name="problem"/>.
    /// </summary>
    public static HubAutomations Load(IEnumerable<HubAutomation> automations, Action<string>? problem = null)
    {
        var result = new HubAutomations();
        foreach (var a in automations)
        {
            string? token = null;
            try
            {
                token = a.TokenFile.Length > 0 && File.Exists(a.TokenFile) ? File.ReadAllText(a.TokenFile).Trim() : null;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (a.Name.Trim().Length == 0 || token is null || token.Length < MinimumTokenLength)
            {
                problem?.Invoke($"hub: automation '{a.Name}' skipped (needs a name and a readable token of {MinimumTokenLength}+ characters)");
                continue;
            }

            result.Add(a.Name, token);
        }

        return result;
    }

    /// <summary>Adds an automation directly (tests, and callers that hold the token already).</summary>
    public void Add(string name, string token) => _entries.Add(($"{name.Trim()} (automation)", Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// The automation's user name for an <c>Authorization</c> header value, or null when it isn't a
    /// bearer token one of them holds. Compared in constant time.
    /// </summary>
    public string? Resolve(string? authorization)
    {
        if (_entries.Count == 0 || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(authorization["Bearer ".Length..].Trim());
        string? match = null;
        foreach (var (user, token) in _entries)
        {
            // check every entry (no early exit) so timing doesn't hint which one exists
            if (CryptographicOperations.FixedTimeEquals(presented, token))
            {
                match ??= user;
            }
        }

        return match;
    }
}
