# EQ Avatar — Phase 0 Spike

A small, throwaway Windows app whose only job is to answer the three questions that
decide what the real **EQ Avatar** can do. Nothing here is production code — it's a
feasibility probe. It does **not** modify any game files except the one optional
"Ensure Log=1" button, which edits `eqclient.ini` (with a backup) while the game is closed.

## What we're trying to learn

1. **Does the EQL log carry position?** If it does, the floating zone map with the glowing
   orb + breadcrumb is easy. If it only logs `/loc` on demand, we'll periodically issue
   `/loc`; if it logs no position at all, we fall back to image recognition.
2. **Can we control EQL while it's in the background?** This is the single biggest risk in
   the whole project. If the game only responds when focused, the "run it behind other
   windows" goal changes shape (windowed-but-visible instead of fully hidden).
3. **Does the floating map feel right?** Always-on-top and good-looking, with the session
   breadcrumb trail — that's the priority. (Click-through is now just an optional "ghost"
   toggle; on multi-monitor setups the map simply lives on another screen.)

## Requirements

- Windows 10/11 — that's it. `run.bat` checks for a .NET SDK (8 or newer) and, if none is
  found, **installs it automatically**: it tries `winget` first (may show one UAC prompt),
  then falls back to Microsoft's official `dotnet-install` script, which installs per-user
  with **no admin rights needed**. One-time, roughly a 200 MB download.
- Prefer manual? Grab the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
  or open `EQAvatar.Spike.sln` in Visual Studio 2022.
- Worth knowing: the finished EQ Avatar will ship as a self-contained exe, so end users
  will never need any SDK. This dance is only because the spike ships as source.

## Run it (no console window)

Double-click **`run.bat`**. The first time, it builds a **standalone `EQAvatar.Spike.exe`**
(bundling the runtime) into a `publish\` folder — a small console shows during that one
build, then closes itself when the app opens. Every run after that, `run.bat` just launches
the already-built exe and exits immediately, so **no console stays open**.

Even simpler afterwards: pin/shortcut **`publish\EQAvatar.Spike.exe`** and double-click that
directly — it's a normal windowed app with no console at all, and needs no .NET installed.

(Developer loop only: `dotnet run` still works but keeps a console, as usual.)

## The three tabs

### 1 · Log reader
- Point **Log folder** at wherever EQL writes `eqlog_*.txt` (often the game root or a
  `Logs` subfolder — use **Browse…** to pick any file in that folder).
- Point **eqclient.ini** at the game's `eqclient.ini`.
- **Ensure Log=1** turns logging on (game must be closed; a `.eqavatar.bak` is written).
- **Read whole file** replays an existing log so we can see what's in it right now.
- **Live tail** shows new lines as you play. Watch the **"Location lines seen"** counter and
  anything tagged `[LOC …]` — that's the money question. Also note what combat / loot / xp /
  zone lines actually look like so I can tune the parser to EQL's exact wording.

> **Most useful thing you can send me:** 30–60 seconds of a real `eqlog_*.txt` captured while
> moving around and killing something. That single sample tells me what EQL emits.

### Getting position into the log — the `/loc` plan

EQL never logs your position on its own; `/loc` has to be issued. The trick is making that
automatic-ish and invisible:

1. **Piggyback `/loc` on a macro you already spam.** In the social/macro editor, take your
   bread-and-butter hotbutton (main attack, opener, whatever gets pressed constantly) and
   add `/loc` as its **last line** — socials run multiple lines per press. Every press then
   stamps `Your Location is <y>, <x>, <z>` into the log, which is exactly what the map
   needs. A dedicated "travel" macro (just `/loc`) on a movement bar works too.
2. **Hide the spam.** Create a separate chat window/tab and use the chat filters to route
   the location/system messages there (and unfilter them from your main window), then
   shrink that tab to a sliver or park it behind the game. The log file records every line
   the client emits **regardless of which window displays it**, so hiding the text on
   screen does not hide it from EQ Avatar.
3. **Cadence = trail quality.** A `/loc` every few seconds paints a smooth breadcrumb;
   even one per pull still gives a useful "where have I been" trail.

Two things to confirm on a real client (tab 1 shows both):
- the **exact wording** EQL uses for the location line — if it differs from classic
  `Your Location is a, b, c`, send me one line and I'll match the parser to it;
- whether EQL's chat filters have a category that captures `/loc` output. If not, we live
  with the line in the main window or lean on the dedicated-macro approach.

Down the road, EQ Avatar itself can *send* the `/loc` keystroke on a timer (that's exactly
what the input probe in tab 2 is deciding), making the whole thing hands-free.

### 2 · Input probe  (v2 — a real diagnostic)

v1's single PostMessage and SendInput both did nothing. Since you can drive EQL from the
background with AutoHotkey, it IS possible — this version isolates *why* ours didn't.
Everything you do is echoed to the log box at the bottom; copy that to me.

**Do this in order — stop as soon as your character reacts:**

1. **Run as administrator.** This is the most likely fix. If EQL runs elevated and this app
   doesn't, Windows silently drops both PostMessage and SendInput (UIPI). The yellow banner
   tells you your current state; click **Relaunch as administrator** and retry. The log line
   after each send now reports whether the *target* is elevated vs this app — if it says
   "couldn't query — often means higher-integrity than us," that's the smoking gun.
2. **Refresh window list → select the EverQuest window** (or **Guess EverQuest**). Pick a
   safe key (default `1`, a hotbar slot).
3. Try the methods, watching your character:
   - **PostMessage → target** — background post to the window.
   - **SendMessage → target** — synchronous variant; some windows honor it when Post is ignored.
   - **Attach+SendInput → target** — attaches to the game's input thread; this is the one that
     usually reaches DirectInput/raw-input games without raising the window.
   - **SendInput (focus game in 3s)** — the baseline: after the countdown (focus the game!),
     this confirms the game accepts synthesized input *at all* when focused.
4. If the frame ignores everything, select the game then **List child windows →**, pick a
   child control (often the render surface holds keyboard focus), and repeat the methods —
   they target the selected child when one is highlighted.
5. **Send /loc → target** is the real prize: it posts Enter → `/loc` → Enter in the background.
   Start **Live tail** on tab 1 first; if a new `[LOC …]` line appears, we can trigger
   position logging ourselves with zero player effort.

Tell me which method (and whether elevated, and top-level vs child) made the character react.
That single answer sets the entire input architecture.

### 3 · Map overlay
- **Show overlay** floats a transparent map (top-right) with a glowing orb walking a canned
  path and leaving a breadcrumb trail. It starts **interactive**: drag it by its header
  anywhere you like — including onto a second monitor, which is the expected home for it.
- The **ghost** button toggles click-through mode (clicks pass straight through to
  whatever's underneath) for anyone who wants it floating over the game on one screen.
  Optional, not required.
- What to judge: does it look good, does it stay on top, does the trail read clearly, and
  does it survive the game going windowed/resized? In the real app the trail keeps the
  full session path per zone (the demo trims it just because the demo walks in circles).

## Notes / gotchas
- If EQL runs **as Administrator**, this app must too, or Windows blocks the input (UIPI).
  Right-click → Run as administrator when you test the input probe.
- Overlay position/scale is per-monitor DPI aware; the app manifest opts into PerMonitorV2.
- This is deliberately ugly. Looks come later — right now we only want yes/no answers.
