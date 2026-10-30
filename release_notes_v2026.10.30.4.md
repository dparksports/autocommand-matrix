# AutoCommand Matrix — v2026.10.30.4

The rain's column count is the truth: one column per alive process,
never ten or more.

## What changed

- **Columns == alive processes.** Every snapshot counts the tracked
  processes that still have at least one live conversation row, and the
  pane lays out exactly that many columns — one leaves the table, its
  column leaves the pane; a new one arrives, a column slides out of the
  docked edge. `maxColumns` drops from 64 to **9** and is clamped 1–9 on
  load, so ten or more can never render regardless of config.
- **The cinema wall spans exactly the data columns**, hugging the docked
  edge (fixing the latent anchoring bug that clustered columns at the
  pane's left when docked right and the grid wasn't full) — the rest of
  the pane stays dark. Nothing alive, nothing rains.
- Same fix as AutoCommand III v2026.10.30.21 — the shared engine, one
  change, both faces.
