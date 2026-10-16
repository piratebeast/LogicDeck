# LogicDeck — Design

## 1. Design language

A dark "workbench" aesthetic: near-black surfaces, one saturated mint accent,
monospace micro-labels, and dense instrument-like chrome. The metaphor is a
hardware lab bench — parts on a grid, wires that glow when live.

### 1.1 Tokens (`wwwroot/css/app.css`, `:root`)

| Token | Value | Use |
|---|---|---|
| `--bg-0` | `#0b0f14` | app + canvas background |
| `--bg-1` | `#10151c` | topbar, cards, dialogs |
| `--bg-2` / `--bg-3` | `#151b23` / `#1b232d` | raised surfaces, hovers |
| `--line-0` / `--line-1` | `#212a35` / `#2c3745` | borders, dividers |
| `--ink-0/1/2` | `#dfe7ef` / `#93a1b0` / `#61707f` | primary / secondary / muted text |
| `--acc` | `#45d6a4` | the single accent: live signal, focus, primary action |
| `--acc-bright` | `#63e9ba` | hover on primary |
| `--acc-dim` | `rgba(69,214,164,.12)` | selected/active fills, focus rings |
| `--amber` | `#e8b64c` | warnings, unstable/oscillating signal, RUNNING badge |
| `--red` | `#e0645c` | errors, destructive hover |
| `--sans` | Space Grotesk | body copy |
| `--mono` | JetBrains Mono | labels, readouts, coordinates, badges |

Both fonts are vendored woff2 in `wwwroot/lib/fonts` — the app renders
identically offline. Component styling is hand-rolled CSS (no utility framework);
Bootstrap Icons supplies iconography.

### 1.2 Typographic voice

Micro-labels are `--mono`, ≤11 px, `letter-spacing: .06–.18em`, uppercase —
used for GRID / PARTS / ZOOM readouts, palette group headers, avatars' initials,
toast marks. This gives the instrument-panel feel without extra chrome.

## 2. Screens

### 2.1 Lobby gate + dashboard (`index.html`)

- **Gate card** (first run): logo, "Pick your call sign", one input, footnote
  explaining auto-dedup ("Same name as a teammate? You'll get 'Ada 2'").
- **Dashboard**: header row `OPEN CIRCUITS <n>` + **New circuit** button; a
  responsive grid of room cards. Each card: title, owner-only trash button,
  meta row (`GRID 16 · 42 PARTS · 3M AGO`), footer with overlapping member
  avatars (max 3, then `+N`) and a `● RUNNING` badge when the sim is live.
  Empty state: dashed placeholder art, "No circuits on the bench yet".
- **New circuit dialog**: title input + segmented grid-size picker (8/16/24/32).
- **Connection chip** in the topbar: `LINK OK` (mint dot) / `RECONNECTING`
  (amber, pulsing) / `OFFLINE` (red).

### 2.2 Editor (`circuit.html`)

Four regions:

1. **Topbar** — back button, brand, inline title edit (Enter commits, Escape
   reverts), RUN/STOP toggle, grid select, tool buttons (Duplicate, Package IC,
   Truth table, Tidy, Clear, Export, Import), invite link copy, member avatar
   stack, connection chip. Below 720 px labels collapse to icons; below 560 px
   some buttons (Package IC, Truth table, Tidy/Clear/Export/Import, member stack)
   hide entirely — the editor stays usable on phones.
2. **Palette** (left rail) — grouped `IO` / `GATES` / `CUSTOM ICs` buttons with
   wire-glyph SVG icons and hotkey badges (`1`, `3`, `b`, …). Clicking arms the
   tool (click again disarms). ICs appear here dynamically once packaged.
3. **Canvas stage** — JointJS paper on a dotted grid, overlays: placement ghost,
   place hint ("Empty bench…"), zoom pill (zoom out / % / zoom in / fit),
   reconnect banner.
4. **Statusbar** (bottom) — `X Y ZOOM GRID PARTS` readouts + `N ONLINE`.

## 3. Canvas component design

Parts are drawn as real SVG in `shapes.js`, not images:

- 2-input gates (AND/OR/XOR/XNOR/NAND/NOR) share a 96×76 shape family: input
  lead stubs at y=24/40, body path, label in mono caps; NAND/NOR/XNOR carry the
  inversion **bubble** circle, XOR/XNOR the extra input arc.
- NOT is a triangle + bubble (80×76).
- Input = rounded case with toggle track + `0/1` digit; CLK = square-wave glyph;
  Button = push-plate; Output = ring + crossed core (the **lamp**); Seg7 = case
  with seven hexagonal segment bars and a small hex digit; FlopD = rectangular
  symbol with D input and clock wedge on the left, Q/Q̄ marks on the right
  (marks tint to the accent color while running).
- ICs are generated per definition: case + name + pin labels on both edges,
  size from pin count (min 96×64, max 180 wide).
- Ports: `in` ports are grey (`#4a5866`), `out` ports mint (`#45d6a4`) — the
  color alone tells users where a drag may start. Valid drop targets highlight
  (`.ld-port-target`), invalid ones turn red (`.ld-invalid`).
- Wires are `ld.Wire` links: orthogonal router, rounded corners, z=-1 so they
  sit under parts.

### 3.1 Interaction model

- **Place** — arm from palette or hotkey (`1`–`9`, `0`=XNOR, `b`=Button); a
  dim ghost follows the cursor; click drops at grid snap (centered on cursor).
  Shift-click keeps the tool armed for multi-drop; right-click or Escape disarms.
- **Wire** — drag from an out-port onto an in-port. Direction doesn't matter
  (dragging *from* an input to an output works). Draft link is removed on drop
  and re-created from the server's `WireDto`, so the client can never disagree
  with the server.
- **Select** — click (shift-click to add/toggle), or drag on empty canvas for a
  rubber-band marquee that picks parts *and* wires by bounding box.
- **Move** — drag selected parts; dragging one of a multi-selection drags the
  group; positions snap to grid locally and broadcast 90 ms after the drag ends.
- **Delete** — Delete/Backspace removes the selection; deleting a part cascades
  its wires (server-side) and the `NodesDeleted` event carries both id lists.
- **Pan/zoom** — middle-drag or Space+drag pans; wheel pans, Ctrl/⌘+wheel zooms;
  zoom clamped 30–260 %; fit button frames the content.
- **Direct manipulation** — clicking an Input toggles it (requires RUN), holding
  a Button presses it (releases on pointerup/blur), clicking a wire selects it.

### 3.2 Live-signal visualization

`applySignals(frame)` maps `SignalFrame` to CSS classes — the server decides,
the client only paints:

| State | Wire | Lamp / display |
|---|---|---|
| high | `.sig-high` — mint stroke + green glow (`drop-shadow`) | lit core + `.lamp-core-on` glow (only while running); Seg7 lights its segment bars and shows the hex digit; FlopD's Q mark glows |
| low | `.sig-low` — dim `#33404f` | dark core / blank display |
| oscillating | `.sig-unstable` — amber dashed | — |

Input switches and clocks repaint from `frame.Sources` so every client's
switches/clocks show the same phase. Seg7 displays decode their four pins as a
BCD nibble (bit0 = pin `in0`) and light segments a–g from
`frame.displays` (`bit0=a … bit6=g`); the hex digit under the bars spells out
what the segments say. STOP clears all signal classes and powers every sink
(lamps, displays) dark.

### 3.3 Truth table

**Truth table** in the toolbar asks the server to enumerate every input-switch
combination and opens a modal (`#truthDlg`) with one row per combination. Column
headers are `A`, `B`, … for the switches (top-to-bottom, then left-to-right) and
`OUT1`, … for the lamps; `1` cells tint to the accent, `0` cells stay muted, and
the header row sticks while the body scrolls (capped height). The dialog foot
offers **Copy CSV** and **Download** (a `<name>-truth-table.csv` blob); errors
(no switches, no lamps, > 8 switches) surface as toasts.

## 4. Presence design

- Each member gets a palette color (`LobbyService`, 8 colors, least-used first)
  used for their avatar and cursor.
- Cursors: SVG arrow + name tag, positioned in an overlay div above the paper.
  Movement is sent throttled (70 ms) with 1-decimal rounding; rendering uses a
  rAF lerp (28 % per frame) so remote cursors **glide** instead of jumping.
- A cursor idle > 2.2 s fades out (`opacity: 0`), reactivating on the next move.
- Members panel: overlapping avatars with initials; lobby cards show up to 3,
  editor up to 4, then `+N`.

## 5. Feedback patterns

- **Toasts** (bottom-right stack): info `ℹ`, ok `✓`, warn `▲`, error `✕` with a
  colored left border; all server validation errors arrive here verbatim
  ("That input already has a wire.", "Only the circuit owner can delete it.").
- **Optimistic-first actions** — toggle/button press/wire drop apply instantly
  and reconcile with the hub's return; failures revert and toast.
- **Header lamp** — `dot-status` mirrors connection state on both pages; the
  editor adds an offline banner with reconnecting copy.
- **Focus rings** use `--acc-dim` halos consistently on inputs/buttons/cards.

## 6. Accessibility & responsiveness

- Keyboard reachable: room cards are `role=button` with Enter/Space, dialogs are
  native `<dialog>` (Esc cancels), inputs have labels.
- Full editor keyboard map: hotkeys to arm tools, Space to pan, Ctrl/⌘+D
  duplicate, Delete/Backspace remove, Escape disarm/clear.
- Breakpoints at 720 px (icon-only toolbar, slimmer palette) and 560 px
  (hide secondary buttons, coords readout) — verified by the audit screenshots
  (`docs/screenshots/audit-07-narrow-*.png`).
- The audit script (`audit-ui.cjs`) exercises a 360 px viewport for horizontal
  overflow; keep new UI passing that.

## 7. Known design debts

- Toasts are the only error surface; inline field validation exists only via
  `required`/`maxlength` attributes.
- No reduced-motion handling for the cursor lerp/glow animations.
- Package-IC dialog lists pins in a flat column; long pin lists scroll with the
  dialog instead of their own scroll area.
- Left palette has no collapse control on desktop.
