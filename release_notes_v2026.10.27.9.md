# AutoCommand Matrix v2026.10.27.9

The holiday-edition process monitor as Matrix rain — everything since the
[first release](https://github.com/dparksports/autocommand-matrix/releases/tag/v2026.10.27.6):

## Over the desktop only

New default surface: the pane parents to the wallpaper host, so the rain
paints the desktop and **application windows cover it** — a living
wallpaper, visible exactly where the desktop shows. Click-through is
automatic there (desktop clicks and the icon marquee pass through). The
original always-on-top overlay remains one toggle away — tray →
*Over the desktop only*, or `"surface": "overlay"`.

## Event-driven stillness

Idle is idle: columns sit perfectly still unless something real happens.
Activity re-ranking no longer runs on a timer over decayed scores (the
cause of periodically shifting columns); a re-rank fires only after actual
events — TX packets, blocked connection attempts, arrivals, retirements.
The synthetic blocked-column heartbeat is gone: a blocked-but-silent
process is simply red-labeled and still, and animates the moment it tries
again.

## The wake: same brightness as the lead, dimmer incrementally

Trailing characters share the lead's brightness family — the character
right behind the lead is near-full lead brightness, each further character
one log-step dimmer — keyed to **distance**, never time: an idle stream
freezes mid-fall instead of dissolving. Trail length follows the fall
speed (`wakeFadeSeconds × fallRowsPerSecond` characters).

## Speed is a control

Fall speed adjusts live from the tray — *Fall slower* (×0.75) / *Fall
faster* (×1.33) with a rows/s readout — saved to `fallRowsPerSecond` and
hot-applied.

## A field foreign to humankind

The default glyph set drops roman letters and digits entirely: katakana
plus a few machine symbols. (The mixed `katakana-latin` set remains a
config option, as do `latin`, `digits`, or any literal string.)

## Also in this span

Subtle-by-default lead elevation (`zLift` 1.06 — same height as the
background unless you watch carefully), the frozen backdrop (only falling
wakes edit it), new-id columns sliding out from the docked edge, and the
wallpaper-host discovery by window enumeration (`FindWindow` cannot see
the desktop host on newer shells).

---

Windows 10/11 x64, administrator (raw sockets, Sysmon log, firewall
inventory). Unzip, unblock, run. Zero outbound requests of its own.
