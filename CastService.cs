using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.ChromecastStatus;
using Sharpcaster.Models.Media;

namespace SomaMetalTray;

public enum CastState
{
    Idle,
    Connecting,
    Casting
}

/// <summary>
/// Casts the selected station to the custom Cast receiver (app id 0CD00C8F, see
/// DeathFmCastReceiver) that the Android app targets. The receiver polls the
/// station's now-playing feed itself, so a sender only needs to point it at the
/// stream URL and tag the LOAD with customData.stationId - no metadata pushed.
/// </summary>
public sealed class CastService : IDisposable
{
    private const string ReceiverAppId = "0CD00C8F";

    // One-way handoff of already-authenticated Last.fm credentials, matching
    // DeathFmAndroid's PlaybackService.sendLastFmCredentialsToReceiver - lets
    // the receiver keep scrobbling on its own after the sender disconnects.
    private const string LastFmNamespace = "urn:x-cast:com.terraeclectic.deathfm.lastfm";

    private readonly AppSettings _settings;
    private ChromecastClient? _client;

    public CastState State { get; private set; } = CastState.Idle;
    public string? CastingDeviceName { get; private set; }

    public event EventHandler? StateChanged;

    public CastService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<ChromecastReceiver>> DiscoverAsync(TimeSpan timeout)
    {
        using var locator = new ChromecastLocator();
        try
        {
            IEnumerable<ChromecastReceiver> receivers = await locator.FindReceiversAsync(fullTimeout: timeout);
            return receivers.ToList();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ChromecastReceiver>();
        }
    }

    public async Task CastToAsync(ChromecastReceiver receiver, IStation station)
    {
        await StopCastingAsync();

        State = CastState.Connecting;
        StateChanged?.Invoke(this, EventArgs.Empty);

        var client = new ChromecastClient();
        try
        {
            client.Disconnected += OnClientDisconnected;

            await client.ConnectChromecast(receiver);

            // The receiver is expected to keep running independently of any
            // one sender (see DeathFmCastReceiver's README), so join an
            // already-running session instead of relaunching it.
            ChromecastStatus? status = await client.LaunchApplicationAsync(ReceiverAppId, joinExistingApplicationSession: true);

            Media media = BuildMedia(station);
            await client.MediaChannel.LoadAsync(media);

            string? transportId = status?.Applications?.FirstOrDefault(a => a.AppId == ReceiverAppId)?.TransportId;
            if (transportId is not null)
            {
                await SendLastFmCredentialsAsync(client, transportId);
            }

            _client = client;
            CastingDeviceName = receiver.Name;
            State = CastState.Casting;
        }
        catch
        {
            client.Disconnected -= OnClientDisconnected;
            await client.Dispose();
            _client = null;
            CastingDeviceName = null;
            State = CastState.Idle;
            throw;
        }
        finally
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static Media BuildMedia(IStation station) => new()
    {
        ContentUrl = station.CastContentUrl,
        ContentType = station.CastContentType,
        StreamType = StreamType.Live,
        Metadata = new MediaMetadata
        {
            MetadataType = MetadataType.Music,
            Title = station.DisplayName,
        },
        // The receiver reads this off the LOAD to pick its now-playing feed and branding.
        CustomData = new Dictionary<string, object> { ["stationId"] = station.CastStationId },
    };

    /// <summary>While casting, points the receiver at another station. No-op when not casting.</summary>
    public async Task SwitchStationAsync(IStation station)
    {
        if (_client is null || State != CastState.Casting)
            return;

        try
        {
            await _client.MediaChannel.LoadAsync(BuildMedia(station));
        }
        catch
        {
            // Best-effort - the cast session itself is still up if only the reload failed.
        }
    }

    private async Task SendLastFmCredentialsAsync(ChromecastClient client, string transportId)
    {
        if (string.IsNullOrEmpty(_settings.LastFmSessionKey))
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(new
            {
                apiKey = AppCredentials.LastFmApiKey,
                apiSecret = AppCredentials.LastFmApiSecret,
                sessionKey = _settings.LastFmSessionKey,
            });
            await client.SendAsync(logger: null, LastFmNamespace, json, transportId);
        }
        catch
        {
            // Best-effort - casting shouldn't fail just because the scrobble handoff did.
        }
    }

    public async Task StopCastingAsync()
    {
        if (_client is null)
            return;

        ChromecastClient client = _client;
        _client = null;
        CastingDeviceName = null;
        State = CastState.Idle;

        client.Disconnected -= OnClientDisconnected;
        try
        {
            // Only disconnects this sender - the receiver keeps playing (see
            // ReceiverAppId's joinExistingApplicationSession usage above).
            await client.DisconnectAsync();
        }
        catch
        {
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnClientDisconnected(object? sender, EventArgs e)
    {
        _client = null;
        CastingDeviceName = null;
        State = CastState.Idle;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _ = _client?.Dispose();
    }
}
