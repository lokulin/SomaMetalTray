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
- A progress bar backed by a real track duration when one's available -
  Deezer, iTunes, and MusicBrainz (via the fanart.tv lookup) all report it in
  the very same response already being read for the cover art, so it's
  essentially free once you're already making that call. Falls back to a
  purely cosmetic, ever-creeping curve (see `PlayerForm.ComputeFakeProgress`)
  for a Bandcamp-only match or no match at all, since SomaFM's own feed never
  provides a duration for a live radio stream. A countdown shows at the right
  end of the progress bar only when the real duration is known - left blank
  on the fake curve rather than counting down against a made-up total.
- Click the title, artist, or album text to copy the current track info
  ("Title — Artist — Album") to the clipboard.
- Clears this app's own stale entries from the Windows Notification Center
  on every launch, rather than letting old track-change toasts pile up
  indefinitely.

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
5. **Station logo** - if all four come back empty.

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
"What it does" above) - read back via `GetCachedDuration`.

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
- **Possible future migration: WinForms -> Avalonia**. Would mean rewriting
  the custom-painted UI (`PlayerForm`, `PlaybackControls`, `WindowChromeHelper`,
  `SettingsForm` - ~1800 lines) in Avalonia's XAML/rendering model; everything
  else (SMTC, Discord/Last.fm, WinRT `MediaPlayer`, AUMID/startup) is
  UI-agnostic and would carry over unchanged. Not for cross-platform reach -
  this app is deeply Windows-coupled (WinRT APIs, SMTC, AUMID) so that
  wouldn't buy anything - but Avalonia's VS Code extension has a live XAML
  previewer, which is the closest thing to a visual GUI designer VS Code
  offers today (neither the WinForms nor WPF designer works outside full
  Visual Studio). Would be done as a parallel branch rather than an in-place
  edit given the size of the rewrite.
