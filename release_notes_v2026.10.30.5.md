# AutoCommand Matrix — v2026.10.30.5

Engine sync from III: the renderer now publishes its alive column count
and cell width (ActiveColumns / CellWidthDip / ActiveColumnsChanged) —
the same API the app's rain pane uses to size itself to its columns.

The overlay's own window keeps its configured `widthPx`: the desktop
pane is a fixture on the wallpaper edge, not a self-sizing panel. Engine
lineage only — no behavior change.
