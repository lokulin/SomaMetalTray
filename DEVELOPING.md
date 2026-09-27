# Developing Metal Detector

This covers architecture, file-by-file notes, and the release process for
contributors. For what the app does and how to install it, see
[README.md](README.md).

## What's here

| File | Purpose |
|---|---|
| `Program.cs` | Entry point. |
| `PlayerForm.cs` | The main window - custom-painted UI, title bar, playback controls, artwork panel, progress bar. |
| `PlaybackControls.cs` | The circular play/stop control and pill-shaped volume slider. |
| `WindowChromeHelper.cs` | Hand-implemented window dragging and system menu for the borderless window. |
| `SystemMenuHelper.cs` | Appends custom items to the window's native system menu. |
| `SettingsForm.cs` | Settings dialog - Last.fm Connect/Disconnect and other toggles. |
| `SettingsStore.cs` | Loads/saves user preferences as JSON in `%AppData%\SomaMetalTray\settings.json`. |
| `AudioPlayerService.cs` | Wraps `Windows.Media.Playback.MediaPlayer`, handling stream playback and failover across rotating servers. |
| `SomaFmService.cs` | Polls SomaFM's song-history and channel-branding endpoints for now-playing metadata. |
| `ArtworkService.cs` | Album art lookup source chain (fanart.tv, Deezer, Bandcamp, iTunes) plus disk/memory caching. |
| `SmtcService.cs` | Drives Windows' System Media Transport Controls. |
| `LastFmScrobbler.cs` | Scrobbles now-playing tracks to Last.fm. |
| `DiscordPresenceService.cs` | Shows the current track as a Discord Rich Presence status. |
| `TrackChangeNotifier.cs` | Pops a toast notification (with art) on track change; clears this app's own stale notifications on launch. |
| `TrayAppContext.cs` | Owns the `NotifyIcon`, tray context menu, and overall app lifetime. |
| `StartupManager.cs` | Adds/removes a "run at Windows startup" entry via the per-user registry Run key. |
| `AumidShortcutHelper.cs` | Creates a Start Menu shortcut stamped with the process AUMID so the media flyout shows "Metal Detector" instead of "Unknown app". |
| `AppCredentials.cs` | This app's own compiled-in Last.fm/Discord/fanart.tv application identifiers (see "API keys / secrets" below). |
| `Logger.cs` | Writes a rolling debug log to `%LOCALAPPDATA%\SomaMetalTray\debug.log`. |

## Prerequisites

Requires the .NET 10 SDK with the Windows Forms/WinRT workload (matching
DeathFmTray's `net10.0-windows10.0.19041.0` target).

## Building and running

```powershell
dotnet build
dotnet run
```

A debug log is written to `%LOCALAPPDATA%\SomaMetalTray\debug.log` (rolling,
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
3. Zips the output as `SomaMetalTray-<version>-win-x64.zip`.
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
`%AppData%\SomaMetalTray\settings.json` - **never commit that file** (the
repo's `.gitignore` also excludes a stray `settings.json` dropped in the
project directory during local testing, as a second line of defense).
Discord Rich Presence and album art lookup work automatically with no setup
at all; Discord presence can be turned off if you don't want it.
