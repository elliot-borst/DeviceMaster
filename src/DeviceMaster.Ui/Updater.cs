using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DeviceMaster.Core.Updating;

namespace DeviceMaster.Ui;

public sealed record UpdateInfo(int[] Version, string Tag, string? SetupUrl, string PageUrl);

/// <summary>
/// GitHub-releases auto-update: query releases/latest, compare whole-number versions,
/// download the -Setup.exe asset to temp and hand off to the installer.
/// </summary>
public static class Updater
{
    private const string Owner = "elliot-borst";
    private const string Repo = "DeviceMaster";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(8));
    private static readonly HttpClient DownloadHttp = CreateClient(TimeSpan.FromMinutes(10));

    public static string LastError { get; private set; } = "";

    public static async Task<UpdateInfo?> CheckLatestAsync()
    {
        LastError = "";
        try
        {
            using var response = await Http.GetAsync(ApiUrl);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                LastError = "Rate-limited — try again later";
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LastError = "Check failed";
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tag.Length == 0)
            {
                LastError = "Check failed";
                return null;
            }

            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;

            string? setupUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (url is not null
                        && url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && url.Contains("setup", StringComparison.OrdinalIgnoreCase))
                    {
                        setupUrl = url;
                        break;
                    }
                }
            }

            return new UpdateInfo(WholeVersion.Parse(tag), tag, setupUrl, page);
        }
        catch (TaskCanceledException)
        {
            LastError = "No connection";
            return null;
        }
        catch (HttpRequestException)
        {
            LastError = "No connection";
            return null;
        }
        catch
        {
            LastError = "Check failed";
            return null;
        }
    }

    /// <summary>Downloads the setup exe to a temp file; returns its path or null on failure.</summary>
    public static async Task<string?> DownloadInstallerAsync(string url)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(),
                $"DeviceMaster-Setup-{Guid.NewGuid().ToString("N")[..8]}.exe");
            using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeviceMaster-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
