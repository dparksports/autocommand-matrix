# AutoCommand Matrix v2026.10.27.6 — first release

The holiday-edition process monitor: your PC's network traffic as Matrix
rain, running the same UI-free core engines as
[AutoCommand II](https://github.com/dparksports/autocommand-ii) through a
pinned git submodule.

## What you get

- **One rain column per process PID**, vertical PID digits at the top.
- **Every falling glyph is a transmit** (≈ one TX packet) by that PID;
  fall speed capped at 25 rows/s (configurable).
- **Blocked PIDs rain red** — from the firewall inventory — and every
  connection attempt while blocked still rolls a red glyph.
- **Only the lead blooms**: the single bright lead glyph carries a subtle
  elevation (`zLift` 1.06 — same height as the background unless you watch
  carefully), a faint shadow and a soft glow. Behind it the wake fades on a
  **logarithmic** curve (brightness holds, then plunges) and resolves into
  the identical backdrop glyph etched beneath.
- **A frozen backdrop** — the ambient glyph field never changes on its own;
  the only edits are real ones, characters the falling wakes write in.
  (Drift is opt-in via `ambientDriftSeconds`.)
- **Columns slide, they don't pop** — new PIDs slide out from the docked
  edge; the busiest PID's column always hugs the docked edge, re-ranked
  with smooth glides every ~2 s.
- **Transparent, dockable, click-through-able** — right or left edge by
  default; full desktop only when explicitly configured.
- **Your colors, your font** — `matrix_rain.json` beside the exe,
  hot-reloaded on save: any installed family or your own `.ttf`/`.otf`,
  every color role, glyph size, charset, speeds and densities.
- Hover a column for the story: process name, tx/rx counters, connections,
  block state. Zero outbound requests of its own — a pure observer.

## Getting started

Windows 10/11 x64, administrator (UAC — raw sockets, Sysmon log, firewall
inventory). Unzip, unblock, run `AutoCommand.Matrix.exe`; the rain docks to
the right edge and a green **M** tray icon carries the whole UI.

## Verified this release

Live-pixel verification across the design passes: three-tier brightness
(lead / halo+wake / backdrop), measured bloom elevation around lead cores,
log-fade wakes resolving into the frozen backdrop, edge-anchored activity
ranking, hot-reload of colors, width and fonts.
