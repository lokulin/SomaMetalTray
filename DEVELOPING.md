# Developing Blastbeat Player

This covers architecture, file-by-file notes, and the release process for
contributors. For what the app does and how to install it, see
[README.md](README.md).

## What's here

| File | Purpose |
|---|---|
| `Program.cs` | Entry point: AUMID, Start Menu shortcut, legacy migration, single-instance mutex. |
| `AppInfo.cs` | App name, data folders, AUMID, mutex name, GitHub repo - the one place the app's identity lives. |
| `Stations.cs` | `IStation` / `INowPlayingSource` and the two stations (Death.FM, Metal Detector), incl. the Death.FM now-playing poller. |
| `PlayerForm.cs` | The main window - custom-painted UI, title bar, controls, artwork, progress, view modes, likes, history, cast wiring. |
| `ViewMode.cs` | The three window layouts and all their pixel metrics. |
| `PlaybackControls.cs` | `UiColors`, play/stop circle, volume slider and the title-bar minimise/close buttons. |
| `AudioPlayerService.cs` | Wraps `Windows.Media.Playback.MediaPlayer`; stream failover and self-healing (stall watchdog, resume from sleep, network back). |
| `SomaFmService.cs` | Polls SomaFM's song-history and channel-branding endpoints. |
| `ArtworkService.cs` | Art lookup: the station's own cover art first, then fanart.tv / Deezer / Bandcamp / iTunes; disk/memory cache. |
| `CastService.cs`, `CastMenuHelper.cs` | Chromecast via Sharpcaster (device discovery, LOAD with `customData.stationId`, Last.fm credential handoff). |
| `Wishlist.cs` | Likes: `WishlistEntry` (name folding), `WishlistRepository` (local-first + retry queue), `RemoteWishlistApi`. |
| `PlayHistory.cs` | The last 500 tracks heard (persisted). |
| `TrackPanel.cs` | The History / Upcoming list under or over the player: owner-drawn rows, thumbnails (`ThumbnailCache`), wheel scroll, row menu. |
| `Upcoming.cs` | Death.FM's queue (`get_db_info` HTML fragments) parser and fetch. |
| `Ui.cs` | Shared look-and-feel pieces: Segoe Fluent glyphs, `IconButton`, `DropdownButton`, `ThemedMenu`. |
| `UpdateChecker.cs` | Daily GitHub "latest release" check; notify-only. |
| `LegacyMigration.cs` | One-time import from SomaMetalTray / DeathFmTray settings and the old autostart entry. |
| `SmtcService.cs` | Drives Windows' System Media Transport Controls. |
| `LastFmScrobbler.cs` | Now-playing, scrobbles, and love/unlove. |
| `DiscordPresenceService.cs` | Discord Rich Presence. |
| `TrackChangeNotifier.cs` | Toast on track change. |
| `TrayAppContext.cs` | `NotifyIcon`, tray menu, update notice, app lifetime. |
| `SettingsForm.cs`, `SettingsStore.cs` | Settings dialog; JSON settings in `%AppData%\BlastbeatPlayer\settings.json`. |
| `StartupManager.cs`, `AumidShortcutHelper.cs` | Run-key autostart; Start Menu shortcut carrying the AUMID. |
| `WindowChromeHelper.cs`, `SystemMenuHelper.cs` | Borderless-window dragging and system menu. |
| `AppCredentials.cs` | Compiled-in Last.fm / Discord / fanart.tv application identifiers. |
| `Logger.cs` | Rolling debug log at `%LOCALAPPDATA%\BlastbeatPlayer\debug.log`. |
| `tools/GeneratePrivateConfig.cs` | MSBuild inline task behind the private-build credentials (not part of the app). |
| `tests/SomaMetalTray.Tests/` | xUnit tests (wishlist, history, update tags, migration). |

## Prerequisites

Requires the .NET 10 SDK with the Windows Forms/WinRT workload (matching
`net10.0-windows10.0.19041.0` target).

## Building and running

```powershell
dotnet build
dotnet run
dotnet test tests/SomaMetalTray.Tests
```

(The project file and namespace are still `SomaMetalTray` - the repo keeps its name; the exe, settings
folder, AUMID and everything user-visible are `BlastbeatPlayer`.)

A debug log is written to `%LOCALAPPDATA%\BlastbeatPlayer\debug.log` (rolling,
truncated on each launch) covering playback state transitions, SMTC button
presses, and resolved artwork paths - useful for diagnosing anything that
looks wrong without attaching a debugger.

## Releasing

Pushing an annotated tag matching `v*.*.*` triggers
`.github/workflows/build-release.yml`, which:

1. Restores and publishes the app (framework-dependent, single-file,
   `win-x64`):

   ```powershell
   dotnet publish SomaMetalTray.csproj -c Release -r win-x64 --self-contained false `
     -p:PublishSingleFile=true `
     -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:DebugType=embedded `
     -p:GenerateDocumentationFile=false `
     -p:CopyDebugSymbolFilesFromPackages=false `
     -p:CopyDocumentationFilesFromPackages=false `
     -o publish
   ```

2. Strips `.pdb`/`.xml` files from the publish output.
3. Zips the output as `BlastbeatPlayer-<version>-win-x64.zip`.
4. Creates a GitHub Release from the tag and attaches the zip, with
   auto-generated release notes.
5. Pings the Death.FM Players site's Cloudflare Deploy Hook so its release
   listing rebuilds (non-fatal if the secret isn't configured).

To cut a release: bump the version, commit, then tag and push:

```powershell
git tag -a v1.2.3 -m "v1.2.3 - ..."
git push origin v1.2.3
```

`.github/workflows/build-check.yml` also runs a plain build (no publish/zip)
on every push/PR to `main`, as a CI sanity check.

## Custom borderless window chrome

The window uses `FormBorderStyle.None` with a hand-painted title bar
(`PlayerForm.DrawTitleBar`) rather than a native caption. A standard
`FixedSingle` native title bar leaves a faint 1px seam where it meets the
custom gradient background; no `DwmSetWindowAttribute` colour customization
removes it, since the native frame itself is the same thickness DeathFmTray
uses - the seam is just more visible against this app's darker background,
not a missing DWM attribute. Dragging the window and the right-click system
menu are both hand-implemented in `WindowChromeHelper`/`PlayerForm.OnMouseDown`
since a borderless window has no native caption for Windows to attach its
usual triggers to. Left-clicking the app icon does not open the system menu;
right-click anywhere in the title strip does.

## SomaFM endpoints used

| Purpose | Endpoint |
|---|---|
| Song history / now playing | `https://somafm.com/songs/metal.json` |
| Channel branding (title, DJ, logo) | `https://somafm.com/channels.json` |
| Stream playlist (AAC, 128kbps) | `https://api.somafm.com/metal130.pls` |
| Station logo (fallback art) | `https://api.somafm.com/logos/512/metal512.png` |

### Gotchas

- **`songs/metal.json` field names/shape aren't formally documented** and
  have shifted before on SomaFM's other undocumented endpoints - `SomaFmService`
  parses defensively (tries a couple of plausible property name variants for
  the art field, accepts either a bare array or an object with a `songs`
  array) rather than assuming one exact shape.
- **`albumArt` is always empty** for this channel - that's the whole reason
  `ArtworkService` exists rather than just trusting the field from SomaFM
  directly, and why SMTC/toast/Discord all read from `ArtworkService`'s
  resolved art rather than `TrackMetadata.ArtUrl`.
- **The `.pls` playlist's `ice*.somafm.com` servers rotate and occasionally
  go down individually** - `AudioPlayerService` rotates to the next `FileN=`
  URL in the playlist on a playback failure, and re-fetches the `.pls`
  entirely once the list is exhausted (the rotation may have changed). It
  also constructs a fresh `MediaPlayer` on every `Play()` rather than reusing
  one across stop/start cycles - reusing a single instance across a
  `Source = null` -> new `Source` cycle was found to sometimes leave it
  silently unable to resume after a Windows SMTC pause.
- **`date` in the song history is a Unix-seconds string**, used (along with
  title+artist) to detect a genuine track change rather than just polling on
  a timer and assuming anything new is a new track.
- **A leading track number some rips carry in their title tag** (e.g. "01
  Song Title") is trimmed before display (e.g. -> "Song Title").

## Album art lookup

`ArtworkService` tries, in order, until one succeeds:

1. **fanart.tv** - highest quality when it has coverage, but keyed by
   MusicBrainz release-group ID rather than free-text search, so this first
   resolves artist+title to a MusicBrainz recording -> release-group (two
   free, unauthenticated MusicBrainz API calls, rate-limited to <=1/sec per
   their usage policy), checking the recording's own artist credit before
   trusting it. Uses this app's own compiled-in fanart.tv API key (see
   "API keys / secrets" below) - nothing to set up - and any failure/403 just
   falls through to Deezer.
2. **Deezer** - `api.deezer.com/search`, no auth needed. The workhorse in
   practice: broad catalog, no key to configure, no MusicBrainz round-trip.
3. **Bandcamp** - a lot of this station's more obscure/underground bands are
   only on Bandcamp. Uses the same undocumented autocomplete endpoint
   Bandcamp's own site search box calls; **still unverified against a real
   response** (the endpoint has IP-blocked every test attempt so far) -
   fails soft into iTunes if the guessed response shape is wrong.
4. **iTunes** - `itunes.apple.com/search`, last-resort fallback. Tops out at
   a lower resolution (upscaled from `100x100bb` to `600x600bb`) and has
   informal rate limits (~20 req/min) and the weakest coverage of underground
   metal of the four sources.
5. **Station logo** - if all four come back empty, shown immediately on
   launch too rather than leaving the art panel blank while the first track
   resolves.

Deezer and iTunes each check up to 5 candidate results in order for one whose
own reported artist name actually resembles the one being searched for,
rather than trusting the top relevance-ranked hit blindly - free-text search
can otherwise return a same-titled track by a completely unrelated, more
"popular" artist.

Results (including "no art anywhere" negative results, with a shorter TTL so
they're eventually retried) are cached in memory and on disk under
`%LOCALAPPDATA%\SomaMetalTray\ArtCache`, so a restart doesn't re-spend
rate-limit budget re-fetching tracks already looked up before. Alongside the
cached `.jpg` itself, two small sidecar files capture data that came along
for free in the same lookup: a `.url` file with the *public URL* the art came
from (SMTC/toast notifications need a local file, Discord Rich Presence needs
a fetchable public URL, and both are served from this one cache without a
second network round-trip), and a `.duration` file with the track's real
length in seconds when Deezer, iTunes, or MusicBrainz reported one (see
"Real vs. fake progress bar" below) - read back via `GetCachedDuration`.

## Real vs. fake progress bar

A progress bar is backed by a real track duration when one's available -
Deezer, iTunes, and MusicBrainz (via the fanart.tv lookup) all report it in
the very same response already being read for the cover art, so it's
essentially free once you're already making that call. Falls back to a
purely cosmetic, ever-creeping curve (see `PlayerForm.ComputeFakeProgress`)
for a Bandcamp-only match or no match at all, since SomaFM's own feed never
provides a duration for a live radio stream. A countdown shows at the right
end of the progress bar only when the real duration is known - left blank on
the fake curve rather than counting down against a made-up total.

## SMTC / Last.fm / Discord integration

`SmtcService` drives Windows' System Media Transport Controls (the volume
flyout widget) from the same now-playing metadata as everything else.
`LastFmScrobbler` scrobbles to Last.fm once the user has connected their own
account. `DiscordPresenceService` shows a Rich Presence status with real
per-track art. All three, along with `TrackChangeNotifier`'s toasts, read
from `ArtworkService`'s resolved art rather than the (always-empty)
`TrackMetadata.ArtUrl` field SomaFM itself returns.

## API keys / secrets

Last.fm's API key/secret, the Discord Rich Presence Client ID/default image
key, and the fanart.tv API key are this app's own registered application
identifiers, compiled in as constants (`AppCredentials.cs`) - there's nothing
to register or paste in to use scrobbling, Discord presence, or album art
lookup. None of these identify or grant access to *your* account: Last.fm
scrobbling still requires you to link your own account once via the
Settings dialog's Connect button (tray icon -> Settings..., or right-click
the title bar -> Settings...), which runs Last.fm's normal browser-based
authorization flow and stores the resulting per-user session key in
`%AppData%\BlastbeatPlayer\settings.json` - **never commit that file** (the
repo's `.gitignore` also excludes a stray `settings.json` dropped in the
project directory during local testing, as a second line of defense).
Discord Rich Presence and album art lookup work automatically with no setup
at all; Discord presence can be turned off if you don't want it.

## Stations

A station (`IStation`) knows how to resolve its stream URLs, how to create its now-playing source, its logo, and the id
the Cast receiver knows it by (`CastStationId`). Adding one is: implement `IStation` (+ an `INowPlayingSource` if the
feed is new), add it to `Stations.All`. `PlayerForm.SwitchStation` handles the rest (stops playback, swaps the
sources, resumes, re-LOADs a connected Chromecast).

**Death.FM** streams `https://death.fm/live` (AAC) and polls its undocumented now-playing JSON directly - no web page.
Quirks handled in `DeathFmNowPlayingSource`: HTML-entity-encoded fields; `PlayStart`/`SystemTime` are on a station
clock that is ~4h off real UTC, so only their *difference* is trusted and elapsed time is anchored to the local clock;
`CoverLink` is the real art; poll no faster than ~30s (the next poll is aimed just after the track should end).

### Death.FM start-up pauses

Played through Windows' `MediaPlayer` (Media Foundation), Death.FM stutters at the start: `death.fm/live` sends ~4s of audio in a
burst and then exactly real time (192kbps = 24KB/s), and Media Foundation wants more than that buffered, so it plays, pauses for
~3.3s (the `Buffering` state), plays again, and repeats 2-3 times in the first ~10s before it settles. The pause length is the
engine's own buffering target - it stayed ~3.3s even when a local relay delivered 7.5s of audio instantly - and a paused player
doesn't read ahead, so a pre-roll before `Play()` doesn't help either. Metal Detector (Icecast) doesn't do it, and neither do
Chromium (the web player, the Cast receiver) or Android's ExoPlayer, which have lower, configurable start thresholds.

What helps, measured on the installed app (a handful of runs each, so treat as indicative):

| | pauses in the first ~10s |
|---|---|
| direct, default | 2-3 |
| direct, `RealTimePlayback = true` (**always on**) | 1 |
| via the receiver proxy while *warm*, with it | 0 (starts in ~0.15s) |
| via the receiver proxy while *cold*, with it | 1 |

The proxy is the Cast receiver's `https://deathfm-cast.l6n.uk/proxy/deathfm-live` (a Durable Object serving the same audio as a ~1GB
"seekable" file; `DeathFmCastReceiver/src/deathfm-live-buffer.js`). It is the owner's own Worker, so it is a **private-build** setting:
`DEATHFM_STREAM_PROXY_URL` in `local.properties`; public builds play directly. It only gives the clean start while its upstream
connection is open - it closes it 90s after the last listener - so `PlayerForm.WarmUpStream` pokes it (a few KB, throttled to once
a minute) when the window is activated, at startup and on switching to Death.FM. `https://death.fm/live` is the fallback URL
(`AudioPlayerService` rotates to it on a failure or a 25s buffering stall). Casting is unaffected: the receiver uses the proxy itself.

A real fix would be a player with a configurable start threshold (e.g. LibVLC's `--network-caching`) instead of Media Foundation.

**Metal Detector** is SomaFM's `metal` channel (`api.somafm.com/metal130.pls`, `somafm.com/songs/metal.json`).

## Chromecast

`CastService` (Sharpcaster, CASTv2) discovers devices over mDNS on demand, launches/joins receiver app `0CD00C8F`
([DeathFmCastReceiver](../DeathFmCastReceiver)) and LOADs the station's stream with
`customData: { stationId }` - the receiver picks its own now-playing feed and branding from that. It then hands the
receiver the Last.fm credentials over `urn:x-cast:com.terraeclectic.deathfm.lastfm` so scrobbling continues without this
PC. Starting a cast stops local playback; stopping only disconnects the sender (the receiver keeps playing).
SomaFM casting relies on the receiver's `somafm-metal` entry and is lightly tested on real devices.

## Likes and the private build

Pressing the heart toggles the track in `WishlistRepository` (local `wishlist.json`, keyed by a name folding identical
to the wishlist server's key normalisation - note it uses the Win32 `NormalizeString`, because
`string.Normalize` does nothing under `InvariantGlobalization`), loves/unloves it on Last.fm if connected, and - only
when built with credentials - queues a `POST`/`DELETE /wishlist` to the wishlist server, with a persisted
retry queue (latest intent per track wins; oldest-first; stops at the first failure; 401/403/408/429/5xx retried, other
4xx dropped). It is a port of DeathFmAndroid's `WishlistRepository`.

The Worker is behind a Cloudflare Access service token, so, as on Android, the sync is a **private build** feature:
copy `local.properties.example` to `local.properties` (gitignored) and fill in the three `WISHLIST_*` values;
`tools/GeneratePrivateConfig.cs` turns them into `PrivateConfig` constants at build time. Public/CI builds have no file,
so the constants are empty and nothing is queued or sent. **Never share an exe built with these set - the token is
inside it.**

## History, layouts, self-healing, updates

- **History and upcoming panel** (`TrackPanel`): `PlayHistory` is newest first, capped at 500, `history.json`; a track is
  recorded when it changes while something is playing it - locally or on a Chromecast - and on starting playback; repeats of
  the newest entry are skipped. The panel (340px) opens under the player, or over it when `PlayerForm.PlanPanel` finds no
  room below: the window then grows upward so the player does not move (`ViewMetrics.For(..., extraTop)` pushes the player
  down). Strip mode has no room for it. The Upcoming tab appears for stations with `HasUpcoming` (Death.FM: `DeathFmQueue`
  fetches `player.php?ajax_action=get_db_info&station=dfm&asin=<now playing>` and parses the `queue_html` rows; the ASIN comes
  from the now-playing `SiteLink`). Death.FM answers 403 to Python's default User-Agent - the app sends its own product token.
- **Controls** use a shared look (`Ui.cs`): the heart and cast are `IconButton`s with Segoe Fluent glyphs, the station
  drop-down is a `DropdownButton` that pops a `ThemedMenu`. Menus are shown with `ThemedMenu.ShowAndDispose`, which defers the
  dispose - disposing from `Closed` crashes WinForms (regression test: `ThemedMenuTests`). Labels set `UseMnemonic = false` so
  an `&` in a track name shows.
- **Double-click** on the title bar cycles the layouts. It is detected by hand in `OnMouseDown` (the first click starts the
  window drag, whose modal loop swallows the usual `DoubleClick`).
- **Layouts** (`ViewMode`): Full / Compact / Strip. All pixel positions come from `ViewMetrics.For(mode)`;
  `PlayerForm.ApplyLayout` applies them. The window stays fixed-size per mode (min = max). The mode is saved.
- **Self-healing** (`AudioPlayerService`): a 5s watchdog restarts a stream stuck buffering > 25s; `SystemEvents.PowerModeChanged`
  (resume) and `NetworkChange.NetworkAvailabilityChanged` trigger a fresh session (URLs re-resolved) after a short delay.
- **Updates** (`UpdateChecker`): one anonymous `GET /repos/<AppInfo.GitHubRepo>/releases/latest` 45s after start and then
  daily; a newer tag adds a tray menu entry and one balloon per version. Nothing is downloaded or installed.

## Identity and migration

The user-visible identity lives in `AppInfo` (name, data folders, AUMID, mutex). The system-menu command ids are masked
by Windows to a multiple of 0x10, so groups of commands (stations, views) step by 0x10. On startup `LegacyMigration`
copies `%AppData%\SomaMetalTray\*.json` to `%AppData%\BlastbeatPlayer` (or imports Last.fm login/volume/window/station
from `%AppData%\DeathFmTray\settings.json` if there is no Soma data), moves the `SomaMetalTray` Run-key entry to the
new name, and `AumidShortcutHelper` replaces the old Start Menu shortcut. Old folders are never deleted.
