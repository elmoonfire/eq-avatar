# EQ Avatar — Apple TV

Watch your character live on the big screen, with the session stats laid over the video.

## Why this is a separate native app

Two constraints, both verified rather than assumed:

1. **tvOS has no web view.** There is no WKWebView on Apple TV. The phone app and the
   website play the stream by loading the hub's `watch.html` in a webview — that approach
   simply cannot run here.
2. **Flutter has no official tvOS support** — only a community fork with a custom engine,
   which is not a foundation to put a TV app on.

So this is native SwiftUI. It talks to the same hub API as everything else.

## How video gets here

The phone and website use **WebRTC** (Cloudflare Realtime SFU) for sub-second latency.
Apple TV plays **HLS** instead, via AVPlayer — the format tvOS handles natively and
reliably. `GET /hub/api/stream.php` returns an `hls` URL when Cloudflare Stream is
configured; the app polls it every 5 seconds and starts playing the moment a broadcast
goes live.

If a broadcast is running that has no HLS (for example a browser screen-share published
only to the SFU), the TV says so plainly rather than sitting on a black screen.

## Pairing

Nobody should type a 50-character token on a Siri Remote. The TV shows a six-character
code, the member enters it in the phone app under **⋮ → Pair a TV**, and the hub hands
the TV a token.

The token issued to a TV is **viewer-only**: a screen in the living room can watch the
session but is structurally incapable of starting or stopping the bot.

## Layout

| Path | What it is |
|---|---|
| `EQAvatarTV/EQAvatarTVApp.swift` | App entry; shows pairing or the live view |
| `EQAvatarTV/HubClient.swift` | Hub API client + credential storage |
| `EQAvatarTV/Models.swift` | Defensively-parsed wire models |
| `EQAvatarTV/PairingView.swift` | The six-character code screen |
| `EQAvatarTV/LiveView.swift` | Full-bleed video + the stats overlay |
| `EQAvatarTV/PlayerView.swift` | AVPlayerLayer wrapper and stream lifecycle |
| `project.yml` | XcodeGen spec — the `.xcodeproj` is generated, not committed |
| `tools/make_brand_assets.py` | Regenerates the layered tvOS icon and top-shelf art |

## Remote

- **Play/Pause** — hide or show the stats overlay for a clean picture.
- **Menu** — unpair this Apple TV.

## Building

The project file is generated, so there is nothing to keep in sync by hand:

```bash
brew install xcodegen
xcodegen generate --spec project.yml
open EQAvatarTV.xcodeproj
```

CI builds and ships to TestFlight on a `mobile-v*` tag (or a manual run with `tvos: true`),
using the same distribution certificate and App Store Connect record as the iPhone app —
only the provisioning profile differs.
