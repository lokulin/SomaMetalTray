# Blastbeat Player

A Windows system tray player for **[Death.FM](https://death.fm)** and
**[SomaFM](https://somafm.com)'s Metal Detector** - native playback (no browser
engine), Chromecast, likes, play history and Last.fm, in a custom-painted
borderless window.

Blastbeat Player replaces two earlier apps: *DeathFmTray* (a WebView2 wrapper
around the Death.FM web player) and *Metal Detector* (this repo's previous
name, SomaFM only). It carries their settings over on first run.

## Features

- **Two stations**, switched from the drop-down in the bottom-right corner (or
  the tray menu): Death.FM and SomaFM Metal Detector. Streams play directly through
  Windows' media stack, with automatic failover across SomaFM's rotating
  servers and **self-healing playback** - it reconnects after a network drop,
  a stall, or waking from sleep.
- **Chromecast**: the cast icon in the bottom-right corner (or tray menu) sends the
  current station to a Cast device using the
  [DeathFmCastReceiver](../DeathFmCastReceiver). The receiver keeps playing and
  scrobbling on its own, and switching station while casting switches the
  receiver too.
- **Likes**: the heart next to the LIVE pill likes/unlikes the current track.
  Likes are kept locally, loved on your Last.fm account when it's connected,
  and - in a private build only, see [DEVELOPING.md](DEVELOPING.md) - synced
  to your SpaceStation wishlist.
- **History and upcoming**: the clock icon (or tray menu) opens a list under the
  player - or over it, when the window is too low on the screen - with a
  *History* tab (the last 500 tracks you heard, locally or on a Chromecast, each
  with a heart) and, for Death.FM, an *Upcoming* tab showing its real queue.
- **Three layouts**: full player, compact, and a one-line mini strip. Double-click
  the title bar to cycle through them, or use the *View* menu.
- Now-playing metadata, album art with a reflection, a progress bar, Windows
  media-flyout / media-key control (SMTC), Last.fm scrobbling, Discord Rich
  Presence, toast notifications on track change, start with Windows and
  minimize-to-tray.
- A tray notice when a **newer release** is available (it only tells you - it
  never downloads anything; switch it off with *Check for Updates*).
- Click the title, artist or album text to copy the track info to the clipboard.

## Installation

1. Download the latest `BlastbeatPlayer-*-win-x64.zip` from the
   [Releases](../../releases) page.
2. Extract it anywhere and run `BlastbeatPlayer.exe`.
3. Windows SmartScreen may warn that the app is unrecognized, since it isn't
   code-signed - click **More info -> Run anyway**.

Last.fm scrobbling, Discord Rich Presence and album art lookup use this app's
own built-in credentials, so there's nothing to register. Scrobbling and the
Last.fm "love" on like need you to link your own account once via **Settings...**
(tray menu, or right-click the title bar).

Coming from DeathFmTray or the old Metal Detector? Run Blastbeat Player once and
your Last.fm login, volume, window position and station are imported; you can
then delete the old app. If DeathFmTray was set to start with Windows, turn that
off in its tray menu.

## Developing

Requires the .NET 10 SDK; `dotnet build` / `dotnet run` gets you a running
copy and `dotnet test tests/SomaMetalTray.Tests` runs the unit tests. See
[DEVELOPING.md](DEVELOPING.md) for architecture, the private-build setup and the
release process.

## Known issues

- Cast is ported from DeathFmTray, and SomaFM casting depends on the receiver's
  `somafm-metal` station, which has had little real-device testing.
- Bandcamp's art-lookup response shape is unverified against live traffic; it
  fails soft into iTunes.
- The disk art cache has no eviction.
- Not code-signed (SmartScreen warns on first run).

## Ideas

- Global hotkeys (play/stop, like) - media keys already work via SMTC.
- Verify the Bandcamp art lookup; add art-cache eviction.
- Other Death.FM network stations and other SomaFM channels (the station layer
  is built to take more).

## License

MIT - see [LICENSE](LICENSE).
