# Metal Detector

A Windows system tray player for [SomaFM](https://somafm.com)'s **Metal
Detector** internet radio channel. Unlike its sibling project
[DeathFmTray](../DeathFmTray) (a WinForms shell wrapping a web player in
WebView2), Metal Detector plays the stream directly - no browser engine
involved - through a fully custom-painted, borderless WinForms UI: album art
with a reflection on the left over a dark-to-red gradient background, track
info on the right, a circular play/stop control, and a pill-shaped volume
slider underneath. The title bar itself is hand-drawn too (see "Custom
borderless window chrome" below) rather than a native caption.

It wires up the same companion integrations DeathFmTray has: System Media
Transport Controls, Last.fm scrobbling, Discord Rich Presence, toast
notifications on track change, start-at-startup, and minimize-to-tray.

## What it does

- Plays SomaFM's Metal Detector stream via `Windows.Media.Playback.MediaPlayer`,
  with automatic failover across the channel's rotating `ice*.somafm.com`
  servers.
- Polls the channel's song-history JSON for now-playing metadata, trimming a
  leading track number some rips carry in their title tag (e.g. "01 Song
  Title" -> "Song Title").
- Looks up real album art through a source chain (fanart.tv, Deezer,
  Bandcamp, iTunes), since the channel's own metadata never actually includes
  art. Each free-text-search source is checked against the artist name it
  actually returned before its art is accepted, to reject an unrelated
  same-titled result rather than trusting relevance ranking alone.
- Falls back to the station's own logo when no source has art for a track,
  shown immediately on launch too rather than leaving the art panel blank
  while the first track resolves.
- Drives Windows' System Media Transport Controls (volume flyout widget),
  scrobbles to Last.fm, shows a Discord Rich Presence status with real
  per-track art, and pops a toast notification (with art) on track change -
  all optional/best-effort.
- A purely cosmetic, ever-creeping progress bar, since there's no real
  per-track duration available anywhere for a live radio stream (see
  `PlayerForm.ComputeFakeProgress`).

## Custom borderless window chrome

The window uses `FormBorderStyle.None` with a hand-painted title bar
(`PlayerForm.DrawTitleBar`) rather than a native caption. This was a
deliberate fix, not a stylistic choice from the start: a standard
`FixedSingle` native title bar always left a faint 1px seam where it met the
custom gradient background, and no `DwmSetWindowAttribute` colour customization
removed it (confirmed via a magenta `DWMWA_BORDER_COLOR` diagnostic - DeathFmTray
reports an identical native frame thickness despite having no visible seam,
so the difference was contrast against DeathFmTray's brighter content, not a
missing DWM attribute here). Dragging the window and the right-click system
menu are both hand-implemented in `WindowChromeHelper`/`PlayerForm.OnMouseDown`
since a borderless window has no native caption for Windows to attach its
usual triggers to. Left-clicking the app icon no longer opens the system menu
(right-click anywhere in the title strip does) - a known, accepted tradeoff.

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

## Album art lookup

`ArtworkService` tries, in order, until one succeeds:

1. **fanart.tv** - highest quality when it has coverage, but keyed by
   MusicBrainz release-group ID rather than free-text search, so this first
   resolves artist+title to a MusicBrainz recording -> release-group (two
   free, unauthenticated MusicBrainz API calls, rate-limited to <=1/sec per
   their usage policy), checking the recording's own artist credit before
   trusting it. Entirely optional - skipped outright if no API key is
   configured, and any failure/403 just falls through to Deezer. Get a free
   personal key at [fanart.tv/get-an-api-key](https://fanart.tv/get-an-api-key)
   and paste it into Settings -> Album art (fanart.tv).
2. **Deezer** - `api.deezer.com/search`, no auth needed. The workhorse in
   practice: broad catalog, no key to configure, no MusicBrainz round-trip.
3. **Bandcamp** - a lot of this station's more obscure/underground bands are
   only on Bandcamp. Uses the same undocumented autocomplete endpoint
   Bandcamp's own site search box calls; **still unverified against a real
   response** (every attempt from the original dev sandbox got IP-blocked) -
   fails soft into iTunes if the guessed response shape is wrong.
4. **iTunes** - `itunes.apple.com/search`, last-resort fallback. Tops out at
   a lower resolution (upscaled from `100x100bb` to `600x600bb`) and has
   informal rate limits (~20 req/min) and the weakest coverage of underground
   metal of the four sources.
5. **Station logo** - if all four come back empty.

Deezer and iTunes each check up to 5 candidate results in order for one whose
own reported artist name actually resembles the one being searched for,
rather than trusting the top relevance-ranked hit blindly - free-text search
can otherwise return a same-titled track by a completely unrelated, more
"popular" artist.

Results (including "no art anywhere" negative results, with a shorter TTL so
they're eventually retried) are cached in memory and on disk under
`%LOCALAPPDATA%\SomaMetalTray\ArtCache`, so a restart doesn't re-spend
rate-limit budget re-fetching tracks already looked up before. The resolved
image is cached alongside the *public URL* it came from (as a `.url` sidecar
file next to the cached `.jpg`), separately from the local file path - SMTC
and toast notifications need a local file, Discord Rich Presence needs a
fetchable public URL, and both are served from this one cache without a
second network round-trip.

## API keys / secrets

Last.fm, Discord, and fanart.tv credentials are all optional and entered via
the Settings dialog (tray icon -> Settings..., or right-click the title bar
-> Settings...). They're written to `%AppData%\SomaMetalTray\settings.json`,
well outside this repo - **never commit that file or paste a real key into
anything that gets committed** (the repo's `.gitignore` also excludes a
stray `settings.json` dropped in the project directory during local testing,
as a second line of defense).

## Building and running

Requires the .NET 10 SDK with the Windows Forms/WinRT workload (matching
DeathFmTray's `net10.0-windows10.0.19041.0` target).

```powershell
dotnet build
dotnet run
```

A debug log is written to `%LOCALAPPDATA%\SomaMetalTray\debug.log` (rolling,
truncated on each launch) covering playback state transitions, SMTC button
presses, and resolved artwork paths - useful for diagnosing anything that
looks wrong without attaching a debugger.

## Known rough edges

- **Bandcamp's response shape is still unverified against live traffic** -
  see "Album art lookup" above. Worth a manual check once you can confirm
  what it actually returns from a residential IP.
- **Visual polish**: the play/stop button, volume slider, and title bar
  buttons are custom-painted to match the theme, but the Settings dialog is
  still a plain port of DeathFmTray's layout with a couple of fields bolted
  on - could use a cleaner grouping/spacing pass, and a "Show Notification on
  Track Change" / "enable Discord presence" checkbox would be more
  discoverable there than tray-menu-only.
- **Disk art cache has no eviction** - it grows unbounded over time (though
  slowly - one small JPEG + URL sidecar, or a `.miss` marker, per unique
  track ever seen).
- **No automated tests** - `SomaFmService`/`ArtworkService`/`AudioPlayerService`
  would benefit from unit tests against recorded fixture responses, especially
  given how defensively they have to parse undocumented endpoints.
