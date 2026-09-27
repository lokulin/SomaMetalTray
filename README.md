# Metal Detector

A Windows system tray player for [SomaFM](https://somafm.com)'s **Metal Detector** internet radio channel.

## About

Unlike its sibling project [DeathFmTray](../DeathFmTray) (a WinForms shell
wrapping a web player in WebView2), Metal Detector plays the stream directly -
no browser engine involved - through a fully custom-painted, borderless
WinForms UI: album art with a reflection on the left over a dark-to-red
gradient background, track info on the right, a circular play/stop control,
and a pill-shaped volume slider underneath.

It wires up the same companion integrations DeathFmTray has: System Media
Transport Controls, Last.fm scrobbling, Discord Rich Presence, toast
notifications on track change, start-at-startup, and minimize-to-tray.

- Plays SomaFM's Metal Detector stream, with automatic failover across the
  channel's rotating streaming servers.
- Shows now-playing metadata polled from the channel's song history.
- Looks up real album art through a source chain (fanart.tv, Deezer,
  Bandcamp, iTunes), since the channel's own metadata never actually includes
  art. Falls back to the station's own logo when no source has art for a
  track.
- Drives Windows' System Media Transport Controls (volume flyout widget),
  scrobbles to Last.fm, shows a Discord Rich Presence status with real
  per-track art, and pops a toast notification (with art) on track change -
  all optional/best-effort.
- A progress bar backed by a real track duration when one's available (from
  the same art lookup), falling back to a purely cosmetic, ever-creeping
  curve when no source reports a duration, since SomaFM's own feed never
  provides one for a live radio stream.
- Click the title, artist, or album text to copy the current track info
  ("Title — Artist — Album") to the clipboard.

## Installation

1. Download the latest `SomaMetalTray-*-win-x64.zip` from the
   [Releases](../../releases) page.
2. Extract it anywhere and run `SomaMetalTray.exe`.
3. Windows SmartScreen may warn that the app is unrecognized, since it isn't
   code-signed - click **More info -> Run anyway** to launch it.

Last.fm scrobbling, Discord Rich Presence, and album art lookup all use this
app's own built-in credentials, so there's nothing to register or configure
to use them out of the box. Scrobbling still needs you to link your own
Last.fm account once, via the Settings dialog's **Connect** button (tray icon
-> Settings..., or right-click the title bar -> Settings...), which runs
Last.fm's normal browser-based authorization flow.

## Developing

Requires the .NET 10 SDK with the Windows Forms/WinRT workload; `dotnet build`
/ `dotnet run` gets you a running copy. See [DEVELOPING.md](DEVELOPING.md) for
the full architecture breakdown, file-by-file notes, and release process.

## Known bugs

- Bandcamp's response shape is still unverified against live traffic - the
  endpoint has IP-blocked every test attempt so far, so it fails soft into
  iTunes if the guessed shape turns out to be wrong.
- The Settings dialog is still a plain port of DeathFmTray's layout with a
  couple of fields bolted on, rather than a clean grouping/spacing pass
  tailored to this app.
- The disk art cache has no eviction, so it grows unbounded over time (though
  slowly - one small JPEG + sidecar files per unique track ever seen).

## Planned features

- Verify and, if needed, fix the Bandcamp art lookup against a real response.
- Add cache eviction for the disk art cache.
- Automated tests for `SomaFmService`/`ArtworkService`/`AudioPlayerService`
  against recorded fixture responses.
- Possible migration from WinForms to Avalonia for the custom-painted UI.

## License

MIT - see [LICENSE](LICENSE).
