# EQ Avatar — mobile companion

Watch your EverQuest Legends bot session and steer it from your phone: live status,
the live game stream, role switching, and the full session history with charts.

iOS and Android from one Flutter codebase. The app is a client of the hub's
control-plane API — it never talks to the game directly.

## How it talks to everything

```
phone  ──►  /hub/api/*  ◄──  desktop EQ Avatar app (polls every 4s)
             status · commands · sessions · stream
```

- **Auth** is a per-member bearer token from the members portal
  (`/hub/api/mytoken.php`), sent as `X-EQA-Token`. Paste it once on first run; it is
  stored on the device only.
- **`/hub/api/` is the one path exempt from the site's Cloudflare network lockdown**,
  so the app works on cell data. Portal pages are not exempt.
- **Commands are queued, not pushed.** The desktop app polls every ~4 s and reports the
  outcome back, so the phone shows real results ("EverQuest isn't running on the PC")
  rather than pretending a tap succeeded. Queued commands expire after 10 minutes.
- **The live stream** is Cloudflare Realtime SFU (WebRTC). The player is the hub's own
  `watch.html` driven by `eqstream.js` in a webview — one implementation of the SFU
  handshake shared with the website instead of three. The token is injected by
  JavaScript after load rather than put in the URL, to keep it out of server logs.

## Layout

| Path | What it is |
|---|---|
| `lib/api.dart` | Hub client — every endpoint, token persistence |
| `lib/models.dart` | Wire models, defensively parsed (the agent evolves on its own) |
| `lib/theme.dart` | Palette shared with the desktop app and portal |
| `lib/screens/` | Pair, Live (status + controls), Stream, Sessions |
| `lib/widgets/damage_chart.dart` | Per-minute dealt/taken timeline |
| `lib/preview_main.dart` | Design harness — renders the screens against a fake hub |

## Working on it

```bash
flutter pub get
flutter analyze
flutter test

# See the UI without a gaming PC (or a device):
flutter build web -t lib/preview_main.dart --no-web-resources-cdn
```

Chart colours are validated for the dark surface (lightness band, chroma, colour-vision
separation, contrast) — see the note in `theme.dart` before changing them.

## Releasing

Tag `mobile-vX.Y.Z` — a separate namespace from the desktop app's `v*` tags, so the two
release trains never trigger each other.

- **Android**: signed APK attached to the GitHub release; sideload it.
- **iOS**: signed IPA uploaded to TestFlight automatically.

Signing is entirely in repo secrets (`IOS_DIST_P12_BASE64`, `IOS_PROVISION_PROFILE_BASE64`,
`ASC_KEY_*`, `IOS_TEAM_ID`, `IOS_PROFILE_UUID`); no Mac and no local certificates needed.
Bundle id `com.hokedesigns.eqavatar`.
