using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace SomaMetalTray;

/// <summary>
/// Tells the user when a newer GitHub release exists. Deliberately notify-only: one anonymous GET of the
/// repo's "latest release" (which excludes drafts and pre-releases) shortly after start and then daily;
/// nothing is downloaded or installed, the tray just offers a link to the release page.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly Func<bool> _enabled;
    private readonly Timer _timer;
    private readonly Version _current;
    private int _checking;

    /// <summary>Raised (on a thread-pool thread) when a newer release is found: its version and release-page URL.</summary>
    public event Action<Version, string>? UpdateAvailable;

    public UpdateChecker(Func<bool> enabled, Version? current = null, HttpClient? http = null)
    {
        _enabled = enabled;
        _current = current ?? CurrentVersion();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlastbeatPlayer", _current.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _timer = new Timer(_ => _ = CheckAsync(), null, FirstCheckDelay, CheckInterval);
    }

    public static Version CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public async Task CheckAsync()
    {
        if (!_enabled() || Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        try
        {
            using HttpResponseMessage response = await _http.GetAsync($"https://api.github.com/repos/{AppInfo.GitHubRepo}/releases/latest");
            if (!response.IsSuccessStatusCode)
                return;

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string? tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            string? url = doc.RootElement.TryGetProperty("html_url", out JsonElement u) ? u.GetString() : null;

            if (tag is not null && url is not null && TryParseTag(tag, out Version? latest) && latest > _current)
                UpdateAvailable?.Invoke(latest, url);
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, or GitHub hiccup - try again at the next interval.
            Logger.Log($"UpdateChecker - check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>Parses release tags like "v1.2.3" or "1.2" (any pre-release/build suffix is ignored).</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
            text = text[..cut];

        return Version.TryParse(text, out version!) && version.Major >= 0;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
