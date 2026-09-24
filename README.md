# SomaMetalTray

A Windows system tray player for [SomaFM](https://somafm.com)'s **Metal
Detector** internet radio channel. Unlike its sibling project
[DeathFmTray](../DeathFmTray) (a WinForms shell wrapping a web player in
WebView2), SomaMetalTray plays the stream directly - no browser engine
involved - through a custom-painted WinForms UI styled after the Death.FM web
player / DeathFmAndroid's portrait layout / DeathFmCastReceiver: album art
with a reflection on the left over a dark gradient background, track info on
the right, play/stop and volume underneath.

This is a v1 scaffold: it builds, plays the stream, and wires up the same
companion integrations DeathFmTray has (SMTC, Last.fm scrobbling, Discord
Rich Presence, toast notifications on track change, start-at-startup,
minimize-to-tray). See "Known rough edges / v2 punch list" below for what's
intentionally left unpolished.

## What it does

- Plays SomaFM's Metal Detector stream via `Windows.Media.Playback.MediaPlayer`.
- Polls the channel's song-history JSON for now-playing metadata.
- Looks up album art through a source chain (fanart.tv, Deezer, iTunes),
  since the channel's own metadata often has no art for underground bands.
- Falls back to the station's own logo when no source has art for a track.
- Drives Windows' System Media Transport Controls (volume flyout widget),
  scrobbles to Last.fm, shows a Discord Rich Presence status, and pops a
  toast notification on track change - all optional/best-effort.

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
- **`albumArt` is very often empty** for the underground/independent bands
  this channel plays - that's the whole reason `ArtworkService` exists rather
  than just trusting the field from SomaFM directly.
- **The `.pls` playlist's `ice*.somafm.com` servers rotate and occasionally
  go down individually** - `AudioPlayerService` rotates to the next `FileN=`
  URL in the playlist on a playback failure, and re-fetches the `.pls`
  entirely once the list is exhausted (the rotation may have changed).
- **`date` in the song history is a Unix-seconds string**, used (along with
  title+artist) to detect a genuine track change rather than just polling on
  a timer and assuming anything new is a new track.

## Album art lookup

`ArtworkService` tries, in order, until one succeeds:

1. **fanart.tv** - highest quality when it has coverage, but keyed by
   MusicBrainz release-group ID rather than free-text search, so this first
   resolves artist+title to a MusicBrainz recording -> release-group (two
   free, unauthenticated MusicBrainz API calls, rate-limited to <=1/sec per
   their usage policy). Entirely optional - skipped outright if no API key is
   configured, and any failure/403 just falls through to Deezer. Get a free
   personal key at [fanart.tv/get-an-api-key](https://fanart.tv/get-an-api-key)
   and paste it into Settings -> Album art (fanart.tv).
2. **Deezer** - `api.deezer.com/search`, no auth needed. The workhorse in
   practice: broad catalog, no key to configure, no MusicBrainz round-trip.
3. **iTunes** - `itunes.apple.com/search`, last-resort fallback. Tops out at
   a lower resolution (upscaled from `100x100bb` to `600x600bb`) and has
   informal rate limits (~20 req/min) and the weakest coverage of underground
   metal of the three sources.
4. **Station logo** - if all three come back empty.

Results (including "no art anywhere" negative results, with a shorter TTL so
they're eventually retried) are cached in memory and on disk under
`%LOCALAPPDATA%\SomaMetalTray\ArtCache`, so a restart doesn't re-spend
rate-limit budget re-fetching tracks already looked up before.

## API keys / secrets

Last.fm, Discord, and fanart.tv credentials are all optional and entered via
the Settings dialog (tray icon -> Settings..., or the titlebar's system
menu). They're written to `%AppData%\SomaMetalTray\settings.json`, well
outside this repo - **never commit that file or paste a real key into
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

## Known rough edges / v2 punch list

- **Visual polish**: the gradient colors and reflection fade are a
  reasonable first pass ("metal-appropriate", not tuned against a real
  design), and the play/stop button and volume `TrackBar` are plain WinForms
  controls sitting on the custom-painted background rather than themed to
  match it.
- **Settings form field cleanup**: it's a straight port of DeathFmTray's
  layout with a fanart.tv field bolted on - could use a cleaner grouping/spacing
  pass, and a "Show Notification on Track Change" / "enable Discord presence"
  checkbox would be more discoverable there than tray-menu-only.
- **Disk art cache has no eviction** - it grows unbounded over time (though
  slowly - one small JPEG or `.miss` marker per unique track ever seen).
- **No automated tests** - this scaffold was built and manually verified to
  compile only; SomaFmService/ArtworkService/AudioPlayerService would
  benefit from unit tests against recorded fixture responses, especially
  given how defensively they have to parse undocumented endpoints.
- **CI**: `.github/workflows/build-check.yml` and `build-release.yml` are
  adapted from DeathFmTray and should work as-is, but haven't been run yet
  (no GitHub remote was created for this repo per the scaffolding task).
- **fanart.tv/MusicBrainz resolution is best-effort and unverified against
  live traffic** - the MusicBrainz recording->release-group lookup and the
  fanart.tv album-cover response shape were implemented from their public API
  docs, not confirmed against a real response in this environment (no
  internet access during scaffolding). Worth a manual smoke test once you
  have a fanart.tv key in hand.
