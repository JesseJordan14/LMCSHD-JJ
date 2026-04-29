# LMCSHD-JJ Project Plan

Personal fork of [`TechRandom/LMCSHD-TR`](https://github.com/TechRandom/LMCSHD-TR), which itself is a fork of [`TylerTimoJ/LMCSHD`](https://github.com/TylerTimoJ/LMCSHD). This is the PC-side companion to the firmware in [`JesseJordan14/LED_Display`](https://github.com/JesseJordan14/LED_Display).

## Progress

- [x] **Feature 1: Native multi-panel section support**
  - [x] Map the current screen-capture → serial-send pipeline in the C# code
  - [x] Design a `Section` data model (X, Y, W, H, origin, orientation, snake)
  - [x] Wire section-aware pixel ordering into the `0x42` frame transmit path
  - [x] Update source firmware to drop the per-pixel demux loop
  - [x] End-to-end test on the 32×32 wall
  - [x] Add a Sections configuration UI (Edit → Sections... menu)
  - [x] Persist sections across launches (per-dimension file in %LOCALAPPDATA%)
- [ ] **Feature 2: Per-section orientation** *(largely falls out of Feature 1)*
- [ ] **Feature 3: Direct WebSocket from PC**
- [ ] **Feature 4: Brightness / gamma / dithering controls**
- [ ] **Feature 5: Built-in test patterns**

## Why fork

The current LMCSHD-TR works for the original Tech Random tutorial setup, but has limitations that hurt for arbitrary panel layouts — most importantly, it has no real concept of multi-panel sections, which forces awkward workarounds in the firmware.

## Hardware target

- 32×32 WS2812B LED wall: **2× 16×32 panels** side-by-side
- 2× ESP8266 "receiver" boards (one per panel), connected over WebSocket to one ESP8266 "source" board
- Source board talks to LMCSHD on the PC over USB serial @ 921600 baud
- Each 16×32 panel is internally two 16×16 modules chained vertically

## Current architecture

```
PC (LMCSHD)  --USB serial-->  Source ESP  --WebSocket-->  Receiver ESP × N  -->  LEDs
```

Protocol (one-byte opcodes over serial):
| Opcode | Direction | Meaning |
|--------|-----------|---------|
| `0x05` | LMCSHD → ESP | "What's your matrix size?" (ESP replies `WIDTH\nHEIGHT\n`) |
| `0x41` | LMCSHD → ESP | Frame data follows — single-panel, RGB888 (3 bytes/pixel) |
| `0x42` | LMCSHD → ESP | Frame data follows — multi-panel, RGB565 (2 bytes/pixel) |
| `0x06` | ESP → LMCSHD | Frame received and displayed (ack) |

## Current limitations

1. **No real multi-panel awareness in the UI.** `0x42` just streams the whole screen as one continuous serpentine and lets the source firmware blindly split the byte stream into N equal chunks. That only happens to work if the panels are arranged so horizontal-snake order naturally segments by panel — which it doesn't for a side-by-side 16×32 + 16×32 layout. Today the firmware does pixel-level demux on the source ESP to compensate.
2. **One global serpentine setting.** Origin / orientation / snake apply to the entire wall, not per panel. If one panel ends up mounted upside-down or rotated, there's no way to fix it without re-soldering.
3. **The source ESP is a required middleman** even though modern WiFi stacks can push pixels straight from the PC to each receiver. The source ESP also requires staying physically tethered to the PC over USB.
4. **No brightness / gamma controls in the UI.** Brightness is hardcoded in firmware (`MAX_BRIGHTNESS 200`).
5. **No built-in test patterns** for debugging wiring without real screen content.

---

## Planned features

Listed roughly in order of impact / value-to-effort.

### 1. Native multi-panel section support — *highest priority*

Replace the "stream the whole screen, let firmware sort it out" approach with proper panel-aware output.

**UI:** A "Sections" panel that lets the user define N sections, each with:
- `X`, `Y` — top-left position on the global matrix
- `Width`, `Height`
- `Origin` (top-left / top-right / bottom-left / bottom-right)
- `Orientation` (horizontal / vertical)
- `Snake` (on / off)
- `Index` — which receiver this section maps to

**Output:** When sending a `0x42` frame, LMCSHD walks each section in its defined local order and emits each section's bytes contiguously. The source firmware can then go back to "read N bytes per panel, broadcast to that receiver, no math" — no demux needed.

**Why this matters:** Removes the firmware-side demux entirely, makes 3+ panels trivial, and supports any physical arrangement the user wants.

**Acceptance:** A user can configure 2 sections, each 16×32 with horizontal/bottom-right/snake, side by side. The source firmware uses unmodified `Serial.readBytes(panelN, PANEL_BYTES)` per panel. The wall renders correctly without any per-pixel mapping in the firmware.

### 2. Per-section orientation

Subset of (1), but worth calling out because it solves a specific real-world problem: panels mounted upside-down or rotated 90°.

**Acceptance:** Rotating one section 180° in the UI causes that panel to render correctly even if it's physically mounted flipped, with no firmware changes.

### 3. Direct WebSocket from PC — *largest architectural change*

Skip the source ESP entirely. LMCSHD opens a WebSocket connection per receiver and pushes pixel data directly from the PC.

**Wins:**
- One fewer hop in the data path → potentially higher framerate, lower latency
- No USB tether to a desk
- No need for the source ESP at all (one fewer thing to power and configure)
- Fewer protocol opcodes — just WebSocket frames

**Costs:**
- Need a WebSocket client implementation in C# (or use an existing NuGet package)
- Discovery / configuration UI for receiver IPs
- Receiver firmware would need to add some panel-identification handshake (currently provided by the source ESP)

**Status:** Aspirational — should ship after (1) is solid.

### 4. Brightness / gamma / dithering controls

Move brightness out of firmware and into LMCSHD. Add gamma correction (LEDs are perceptually awful without it) and optional temporal/spatial dithering for low-brightness regions.

**Acceptance:** A brightness slider in the UI smoothly scales output 0–100%, gamma slider produces correctly-perceived greyscale ramps, dithering reduces color banding visibly.

### 5. Test patterns

Built-in debug patterns selectable from the UI:
- Solid color (with color picker)
- Walking pixel (single white pixel scrolling through every position in chain order — fastest way to find a wiring fault)
- Color bars / gradient
- Per-panel ID display (each section shows its index number — instantly tells you which board is which)

**Why:** Today, debugging wiring requires real screen content, which is noisy. A clean test pattern would have caught the panel demux issue immediately.

---

## Open questions / decisions to make

- **Tooling:** does VS Code / Cursor handle WPF + .NET Framework 4.7.2 well enough, or do we need to install Visual Studio 2022 Community? (Likely the latter for productive XAML editing.)
- **Migrate to .NET 8?** The upstream is on .NET Framework 4.7.2. Migrating to modern .NET would simplify tooling and unlock cross-platform builds, but is its own project.
- **Scope creep guard:** keep features 1–2 self-contained; resist mixing them with (3) until (1) is shipping.
- **Upstream sync:** the `upstream` git remote is wired to `TechRandom/LMCSHD-TR`. Decide policy on pulling in upstream commits vs. diverging.

## Building

The project is old-style .NET Framework 4.7.2 WPF and **does not build with `dotnet build`** — that toolchain doesn't carry the full WPF compilation targets for this project format. Build with Visual Studio's MSBuild:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" LMCSHD\LMCSHD_WPF.sln
```

(adjust the VS year if you have a different install). Or open `LMCSHD\LMCSHD_WPF.sln` in Visual Studio and hit Ctrl+B.

Output ends up in `LMCSHD\LMCSHD\bin\x64\Debug\LMCSHD.exe`.

## Out of scope (for now)

- Replacing LMCSHD entirely with a custom app
- Web-based UI / Electron rewrite
- Cross-platform builds (Linux/Mac)
- Audio reactivity beyond what upstream already supports
