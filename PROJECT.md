# LMCSHD-JJ Project Plan

Personal fork of [`TechRandom/LMCSHD-TR`](https://github.com/TechRandom/LMCSHD-TR), which itself is a fork of [`TylerTimoJ/LMCSHD`](https://github.com/TylerTimoJ/LMCSHD). PC-side companion to the firmware in [`JesseJordan14/LED_Display`](https://github.com/JesseJordan14/LED_Display).

## Progress

- [x] **Feature 1: Native multi-panel section support** — Section data model + section-aware serial output + dialog UI + persistence; firmware demux removed
- [x] **Feature 2: Per-section orientation** — covered by Feature 1 (every Section row has its own Orientation/Origin/NewLine)
- [ ] **Feature 3: Direct WebSocket from PC** — large architectural change, save for last
- [ ] **Feature 4: Brightness / gamma / dithering controls** — small UX addition, *next up*
- [x] **Feature 5: Built-in test patterns** — solid color / walking pixel / per-section color

## Hardware target

- 32×32 WS2812B LED wall: **2× 16×32 panels** side-by-side
- 2× ESP8266 "receiver" boards (one per panel), connected over WebSocket to one ESP8266 "source" board
- Source ESP talks to LMCSHD on the PC over USB serial @ 921600 baud
- Each 16×32 panel is two 16×16 modules chained vertically
- Network: `10.0.0.x`; source ESP at static `10.0.0.121:81`
- WiFi credentials live in per-sketch `secrets.h` files (gitignored). Templates in each `secrets.example.h`.

## Architecture

```
PC (LMCSHD-JJ)  --USB serial-->  Source ESP  --WebSocket-->  Receiver ESP × N  -->  LEDs
```

Protocol (one-byte opcodes over serial):

| Opcode | Direction | Meaning |
|--------|-----------|---------|
| `0x05` | LMCSHD → ESP | "What's your matrix size?" (ESP replies `WIDTH\nHEIGHT\n`) |
| `0x41` | LMCSHD → ESP | Frame data follows — RGB888 (3 bytes/pixel), single-panel format |
| `0x42` | LMCSHD → ESP | Frame data follows — RGB565 (2 bytes/pixel); **section-ordered** after Feature 1 |
| `0x06` | ESP → LMCSHD | Frame received and displayed (ack) |

Receivers connect to the source ESP's WebSocket server, the source asks `"Who?"` on connect, and each receiver replies `"Device N"` to claim a slot. The source forwards each section's bytes to the matching receiver in list order.

## What was built

### Feature 1: Native multi-panel section support

`Section` (X, Y, W, H, Orientation, StartCorner, NewLine) is a struct in `App.xaml.cs` next to the existing `Pixel` and `PixelOrder`. `MatrixFrame.Sections` is a `List<Section>`. When non-empty, `MatrixFrame.GetOrderedSerialFrame()` walks each section in its own local serpentine and concatenates the bytes. When empty, single-panel mode kicks in (uses global `orientation`/`startCorner`/`newLine` over the full matrix, backward-compatible with upstream).

The Sections dialog (`Edit → Sections...`) is a DataGrid editor: one row per section, columns for X/Y/Width/Height/Orientation/Origin/NewLine, plus Add / Remove / Clear All / OK / Cancel buttons. `SectionVm` wraps `Section` as a class so the DataGrid can two-way bind (the struct doesn't notify on edits). Enum value arrays are exposed as `static Array` properties on `MatrixSections` for `DataGridComboBoxColumn`'s `x:Static` binding.

Persistence: clicking OK saves to `%LOCALAPPDATA%\LMCSHD-JJ\sections-WxH.dat` (one section per line, space-separated `X Y W H Orientation StartCorner NewLine`). On `SetDimensions`, sections auto-load from the file matching the new dimensions; sections are cleared whenever dimensions actually change to avoid out-of-bounds reads.

Firmware impact: with LMCSHD now emitting per-panel-ordered bytes, `LED_Wall_Source.ino` lost its pixel-level demux loop (`commit 95c38a8`) and went back to two simple `Serial.readBytes(panelN, PANEL_BYTES)` calls.

### Feature 2: Per-section orientation

Fell out of Feature 1 for free — every Section row in the dialog has its own Orientation/Origin/NewLine. Flipping a misoriented panel is now a dropdown change with no firmware redeploy.

### Feature 5: Built-in test patterns

New "Test Patterns" tab on `MainWindow` with three modes:

- **Solid Color**: ColorPicker + Fill / Clear. Useful for brightness checks and finding dead LEDs against a known background.
- **Walking Pixel**: a single white pixel walks the LED chain in physical order at 1–200 LED/s. A hop in the chain = a wiring fault.
- **Per-Section ID**: one click fills each section with a distinct color (red, green, blue, …). Instantly reveals section-to-panel mapping.

Walking pixel uses `MatrixFrame.GetChainOrderCoords()`, which mirrors `GetOrderedSerialFrame()`'s traversal but emits `(x, y)` instead of bytes. The two methods share serpentine logic via parallel helpers (`EmitRegion` and `AppendRegionCoords`) — **keep them in sync** if you change one.

## Remaining features

### Feature 4: Brightness / gamma / dithering — *next*

Move brightness scaling out of firmware and into LMCSHD. Add gamma correction (LEDs are perceptually awful at low values without it). Possibly add temporal/spatial dithering for low-brightness regions.

**Acceptance:** brightness slider scales output 0–100% smoothly, gamma slider produces a correct-looking greyscale ramp, dithering visibly reduces banding (if implemented).

**Likely shape:** new properties on `MatrixFrame` (`Brightness`, `Gamma`) plus a transform step somewhere in the pipeline. Two reasonable hook points:

- Apply the transform inside `GetOrderedSerialFrame()` / `EmitRegion()` per pixel as it's emitted to the byte stream.
- Or apply it in `SerialManager.PushFrame()` after the ordered bytes are built but before color-depth conversion.

The first keeps `SerialManager` untouched but means modifying the per-pixel write site twice (HZ + VT branches). The second is more localized but operates on bytes, which is awkward for gamma. Probably go with the first.

UI: a top-level brightness/gamma group near the matrix preview is most discoverable. Or a third row in an existing tab. Persist to user settings.

### Feature 3: Direct WebSocket from PC — *largest architectural change*

Skip the source ESP entirely. LMCSHD opens a WebSocket connection per receiver and pushes frames directly.

**Wins:** higher framerate, no USB tether, one less device to power and configure.

**Costs:** C# WebSocket client (NuGet package), receiver IP discovery/config UI, receiver firmware must implement `"Who?"` handshake from PC instead of source ESP.

**Status:** save for a focused effort of its own. Do not start until Feature 4 is shipped.

## Implementation reference

### LMCSHD-JJ files (under `LMCSHD/LMCSHD/`)

- `App.xaml.cs` — global structs: `Pixel`, `PixelOrder` (with nested `Orientation`/`StartCorner`/`NewLine` enums), `Section`, `MatrixTitle`.
- `MatrixFrame.cs` — pixel buffer, dimensions, sections list, `GetOrderedSerialFrame`, `GetChainOrderCoords`, `EmitRegion`/`AppendRegionCoords` (parallel traversal helpers — **keep in sync**), `SaveSections`/`LoadSections`/`SectionsFilePath`, `LoadEmbeddedBitmap` (replaces former `Properties.Resources.X` usages so the project builds without `Resources.resx`).
- `SerialManager.cs` — protocol handling (`0x05`/`0x41`/`0x42`/`0x06`), color mode → opcode mapping, frame transmission. Buffer sizing in non-RGB888 branches uses `Width * Height` rather than `orderedFrame.Length / 3` — fine while sections cover the full matrix; revisit if partial coverage is ever supported.
- `MainWindow.xaml` — root UI: menu (`File`, `Serial`, `Edit → Matrix Dimensions / Sections... / Pixel Order / Color Mode`, `View`, `About`) and tabs (Screen Recorder, Audio, Imaging, **Test Patterns**, Drawing/disabled).
- `MainWindow.xaml.cs` — partial; menu click handlers, MatrixImage events.
- `MainWindow{Audio,Imaging,ScreenCapture,TestPatterns}.cs` — partials; mode-specific state and handlers.
- `MatrixSections.xaml` + `.xaml.cs` — Sections editor dialog. `SectionVm` is the row binding type. Enum value lists are static for x:Static binding.
- `MatrixDimensions.xaml` + `.xaml.cs` — pre-existing Width/Height dialog (untouched in this fork).

### LED_Display files (firmware) (under `Massive-LED-Wall-main/Code/`)

- `LED_Wall_Source/LED_Wall_Source.ino` — source ESP. Reads opcodes from PC, broadcasts per-section bytes to receivers. Demux loop removed in `commit 95c38a8`.
- `LED_Wall_Reciever/LED_Wall_Reciever.ino` — receiver ESP. WebSocket client. `#define DEVICE_NUM 1` (or 2) selects which slot this board claims — re-flash per board.
- `Single_LED_Wall_*` — earlier single-panel sketches kept for reference.
- `*/secrets.h` — WiFi credentials, **gitignored**. `*/secrets.example.h` is the committed template.

### Scaffolding to remove eventually

- `MatrixFrame.UseTestSectionsOn32x32` (default `true`): when `SetDimensions(32, 32)` runs and no saved sections file exists, auto-populates the author's two-section layout via `UseTestSections_TwoPanels32x32()`. Once persistence has been used at least once, this is dead code. Safe to remove when convenient.

## Building

This is an old-style .NET Framework 4.7.2 WPF project. **`dotnet build` will not work** — that toolchain doesn't carry the WPF compilation targets for this project format. Build with Visual Studio's MSBuild:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" LMCSHD\LMCSHD_WPF.sln
```

(adjust the VS year if you have a different install). Or open `LMCSHD\LMCSHD_WPF.sln` in Visual Studio and Ctrl+B.

Output: `LMCSHD\LMCSHD\bin\x64\Debug\LMCSHD.exe`.

If you try `dotnet build` you'll see either MSB3822/MSB3823 resource errors or WPF target errors (varies by SDK version). Don't waste time — use VS MSBuild.

## Test loop

1. Edit code (LMCSHD-JJ for desktop, LED_Display for firmware).
2. Build LMCSHD with VS MSBuild. Re-flash source ESP via Arduino IDE if firmware changed.
3. Run `LMCSHD\LMCSHD\bin\x64\Debug\LMCSHD.exe`.
4. `Serial → Connect`: source ESP's COM port, baud `921600`, color mode **BPP16RGB** (sends `0x42`).
5. Matrix dimensions auto-detect to 32×32. Sections auto-load from save file or fall back to scaffolding.
6. Use the various tabs (Screen Recorder / Imaging / Test Patterns) to push content. Verify on the wall.

Common diagnostic moves:

- Wall scrambled → `Test Patterns → Per-Section ID` to confirm the section-to-panel mapping is right.
- Halves swapped → don't touch LMCSHD; re-flash one receiver with `DEVICE_NUM` swapped (1↔2).
- One section's orientation off → Sections dialog → flip its Origin/NewLine for that row.

## Open questions

- **Migrate to SDK-style csproj or .NET 8?** Would let `dotnet build` work and unlock cross-platform tooling. Standalone task, not blocking anything else.
- **Persistence scope:** sections persist per matrix dimension. Should we also persist global orientation/startCorner/newLine, color mode, last COM port, brightness/gamma (when they exist)? Currently those reset to defaults each launch.
- **Upstream sync policy:** the `upstream` git remote points to `TechRandom/LMCSHD-TR`. Decide whether to merge upstream commits or diverge.

## Out of scope (for now)

- Replacing LMCSHD entirely with a custom app
- Web-based UI / Electron rewrite
- Cross-platform builds (Linux/Mac)
- Audio reactivity beyond what upstream already supports
