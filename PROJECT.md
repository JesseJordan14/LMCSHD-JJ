# LMCSHD-JJ Project Plan

Personal fork of [`TechRandom/LMCSHD-TR`](https://github.com/TechRandom/LMCSHD-TR), which itself is a fork of [`TylerTimoJ/LMCSHD`](https://github.com/TylerTimoJ/LMCSHD). PC-side companion to the firmware in [`JesseJordan14/LED_Display`](https://github.com/JesseJordan14/LED_Display).

## Progress

- [x] **Feature 1: Native multi-panel section support** — Section data model + section-aware serial output + dialog UI + persistence; firmware demux removed
- [x] **Feature 2: Per-section orientation** — covered by Feature 1 (every Section row has its own Orientation/Origin/NewLine)
- [ ] **Feature 3: Direct WebSocket from PC** — in progress (3.1 firmware + 3.2 server done; 3.3–3.6 remaining)
- [x] **Feature 4a: Brightness / gamma controls** — sliders + Reset buttons + LUT in `EmitRegion`, persisted to `display-prefs.dat`; verified on wall
- [ ] **Feature 4b: Dithering** — deferred; would hook in `SerialManager` color-quantization paths
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

### Feature 4a: Brightness / gamma — *done*

`MatrixFrame.Brightness` (0..1) and `MatrixFrame.Gamma` (0.1..5) drive a 256-entry `byte[] _lut` (formula: `out = round(255 * pow((in/255) * brightness, gamma))`). Setters clamp and rebuild the LUT. `EmitRegion` reads through `_lut` for every R/G/B byte it emits — single hook point covers no-sections and sectioned paths, HZ and VT branches. `AppendRegionCoords` is unchanged because it doesn't touch color data; the "keep parallel helpers in sync" rule didn't apply here.

UI: a strip below the tab/preview area in `MainWindow.xaml` (Grid row 2, `ColumnSpan=3`), always visible across tabs. Two `<Slider>`s — Brightness 0–100%, Gamma 0.5–3.0 — with value labels and Reset buttons (defaults exposed as `MatrixFrame.BrightnessDefault` / `GammaDefault` constants). `ValueChanged` writes through to `MatrixFrame` and calls `Refresh()` so static frames update immediately.

Persistence: `%LOCALAPPDATA%\LMCSHD-JJ\display-prefs.dat`, key=value lines (`brightness 0.7` / `gamma 2.2`), invariant culture. Loaded right after `InitializeComponent` and slider values re-synced from the loaded values; saved in `Window_Closing`. Defaults are 1.0/1.0 (no-op) so first launch matches pre-Feature-4 behavior.

Note: brightness/gamma do *not* currently apply to the matrix preview image, only to serial output. Preview is treated as the "source content" view; the wall is the "output" view.

### Feature 4b: Dithering — *deferred*

Spatial (Bayer) or temporal dithering at the bit-depth quantization step inside `SerialManager.PushFrame`'s BPP16 / BPP8 branches. Worth doing if banding shows up after gamma at low brightness; not urgent. Separate hook point from 4a (operates on bytes mid-quantization, not on `Frame[]`).

### Feature 3: Direct WebSocket from PC — *in progress*

Skip the source ESP entirely. **LMCSHD acts as the WebSocket server**; receivers stay as WS clients (just point at the PC instead of the source ESP). This kept the firmware change to one line and preserved all the receiver's existing reconnect logic.

**Substep status:**

- [x] **3.1 — Receiver firmware** points at PC via `secrets.h` (`PC_IP` / `PC_PORT`). LED_Display commit `96ff2b6`.
- [x] **3.2 — `NetworkManager.cs` (Fleck-backed WS server)** with auto-start in `MainWindow` ctor, `Network → Connect... / Disconnect` menu, and `MatrixNetworkConnection` dialog (port + live device list). `"Who?"` / `"Device N"` handshake, `ReceiverIdentified` / `ReceiverDisconnected` events.
- ~~**3.3 — Status display refinement.**~~ Skipped; the existing dialog covers it.
- [x] **3.4 — Per-section frame routing.** `MatrixFrame.GetSectionFrames()` returns per-section RGB888 buffers; `NetworkManager.PushFrame` packs each to BPP16 and sends to the mapped receiver. Section index N → device N+1. Color mode UI is locked to BPP16 (other modes visible-but-disabled in Edit → Color Mode menu and serial-connect dialog) since the receiver firmware only decodes 5-6-5 today. Required a firmware-side `case WStype_BIN` because the legacy text-frame-with-binary-payload hack from the source-ESP path doesn't apply when Fleck sends proper binary frames.
- [x] **3.5 — Per-receiver `0x06` ack gating.** Receiver firmware sends `webSocket.sendBIN(0x06, 1)` after `FastLED.show()`. `NetworkManager` tracks `_deviceReady` per device and only flushes a frame when every connected device is ready; if a frame fires while waiting, it's marked pending and pushed on the final ack. Effect: panels stay locked together and producer is throttled to slowest receiver. Verified end-to-end on wall.
- [ ] **3.6 — Retire source ESP.** Move source firmware to `legacy/`. Drop the `WStype_TEXT` BPP16-decode path in receiver firmware (legacy-only). Update PROJECT.md so network mode is the documented happy path and serial is the fallback.

**Gotchas discovered along the way:**

- *Fleck's `WebSocketServer.Dispose()` only closes the listener socket*, not active connections. `NetworkManager.Disconnect()` walks `_devices` + `_pending` and calls `IWebSocketConnection.Close()` on each before disposing the server, then sleeps 100 ms to let close frames flush. Without this, receivers stay in a half-alive WebSocket state across a Disconnect→Connect cycle and only recover on physical replug.
- *The legacy upstream protocol shipped binary pixel data over WS **text** frames* via the source-ESP firmware's `webSocket.sendTXT(client, (char*)bytes, length)` overload. The receiver was written to decode under `case WStype_TEXT` only. When `NetworkManager` sends proper binary frames via Fleck's `conn.Send(byte[])`, the receiver's switch falls through to `default:` and silently does nothing. Fix was firmware-side: add `case WStype_BIN` and factor the decode into a shared `decodeBPP16AndShow(payload)` helper. Both branches still exist (TEXT for legacy source-ESP, BIN for direct LMCSHD); 3.6 will drop the TEXT branch.
- *No ack timeout in `NetworkManager`.* If a receiver crashes / loses WiFi mid-frame, its ack never arrives and the PC waits forever. Workaround: `Network → Disconnect / Connect`. Add a watchdog if this turns out to bite in practice.

## Implementation reference

### LMCSHD-JJ files (under `LMCSHD/LMCSHD/`)

- `App.xaml.cs` — global structs: `Pixel`, `PixelOrder` (with nested `Orientation`/`StartCorner`/`NewLine` enums), `Section`, `MatrixTitle`.
- `MatrixFrame.cs` — pixel buffer, dimensions, sections list, `GetOrderedSerialFrame`, `GetChainOrderCoords`, `EmitRegion`/`AppendRegionCoords` (parallel traversal helpers — **keep in sync**), `SaveSections`/`LoadSections`/`SectionsFilePath`, `Brightness`/`Gamma` + `_lut` (consumed only in `EmitRegion`), `SaveDisplayPrefs`/`LoadDisplayPrefs`/`DisplayPrefsFilePath`, `LoadEmbeddedBitmap` (replaces former `Properties.Resources.X` usages so the project builds without `Resources.resx`).
- `SerialManager.cs` — protocol handling (`0x05`/`0x41`/`0x42`/`0x06`), color mode → opcode mapping, frame transmission. Buffer sizing in non-RGB888 branches uses `Width * Height` rather than `orderedFrame.Length / 3` — fine while sections cover the full matrix; revisit if partial coverage is ever supported.
- `NetworkManager.cs` — Fleck-backed WebSocket server. `Connect(int port)` / `Disconnect()` / `PushFrame()`, `Devices` map (device# → `IWebSocketConnection`), `ReceiverIdentified` / `ReceiverDisconnected` events. Per-section frame routing (section index N → device N+1), BPP16 packing (5-6-5), per-device `0x06` ack gating with a `_pendingFrame` flag so the most recent state is always shipped on the next all-ready window. Disconnect must close active connections explicitly (Fleck's `Dispose` doesn't fan out closes — see Feature 3 gotchas). Subscribes to `MatrixFrame.FrameChanged` in `Connect`, unsubscribes in `Disconnect`.
- `MatrixNetworkConnection.xaml` + `.xaml.cs` — port-and-status dialog reachable via `Network → Connect...`. Subscribes to NetworkManager events to show live device list.
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
- **Persistence scope:** sections persist per matrix dimension; brightness/gamma persist globally as `display-prefs.dat`. Should we also persist global orientation/startCorner/newLine, color mode, last COM port? Currently those reset to defaults each launch.
- **Upstream sync policy:** the `upstream` git remote points to `TechRandom/LMCSHD-TR`. Decide whether to merge upstream commits or diverge.

## Out of scope (for now)

- Replacing LMCSHD entirely with a custom app
- Web-based UI / Electron rewrite
- Cross-platform builds (Linux/Mac)
- Audio reactivity beyond what upstream already supports

## Continuation prompt for a new Claude session

Paste the block below into a fresh Claude session to bootstrap context and pick up where we left off. Update the "What I want to do now" section to match the current task before sending.

```
I'm continuing work on a personal LED-wall hobby project. Two repos are involved:

1. C:\Users\16jjo\OneDrive\Documents\GitHub\LED_Display
   ESP8266 firmware (Arduino sketches) for a 32×32 WS2812B wall (two side-by-side
   16×32 panels, each driven by its own ESP, plus a "source" ESP that talks to
   the PC over USB and forwards frames over WebSocket).

2. C:\Users\16jjo\OneDrive\Documents\GitHub\LMCSHD-JJ
   My fork of TechRandom/LMCSHD-TR, the screen-mirroring desktop app that streams
   pixels to the source ESP. This is where most of the active work happens.

START BY READING `LMCSHD-JJ\PROJECT.md` — it's the canonical state document.
It covers: hardware, architecture, protocol, what's been built (with code
pointers), what's left, build instructions, test loop, and known scaffolding.

Quick status:
- Features 1, 2, 4a, 5 are done and verified on the actual wall.
- Feature 4b (dithering) is deferred — only do it if banding shows up after
  gamma at low brightness.
- Feature 3 (direct WebSocket from PC) is mostly done. Substeps 3.1, 3.2,
  3.4 (per-section frame routing), and 3.5 (per-receiver ack gating with
  pending-frame flushing) are all verified on the wall. Source ESP can be
  unplugged and the wall still works over network. Only 3.6 left:
  formally retiring the source-ESP firmware (move to legacy/, drop the
  WStype_TEXT BPP16 branch from the receiver, mark serial mode as fallback).

Non-obvious gotchas you must know up front:

- Build LMCSHD-JJ with Visual Studio's MSBuild (path in PROJECT.md), NOT
  `dotnet build`. The latter fails on this old-style .NET Framework 4.7.2 WPF
  project regardless of SDK version. Output is bin\x64\Debug\LMCSHD.exe.
- WiFi credentials live in gitignored secrets.h files in each Arduino sketch
  folder. Templates are in secrets.example.h. Never commit real creds.
- Source ESP is at static IP 10.0.0.121 on a 10.0.0.x network.
- There's intentional scaffolding in MatrixFrame.UseTestSectionsOn32x32 that
  PROJECT.md flags for eventual removal — don't reflexively "clean it up."
- `MatrixFrame.GetOrderedSerialFrame` / `EmitRegion` and `GetChainOrderCoords`
  / `AppendRegionCoords` share traversal logic in parallel helpers. If you
  change one, change the other.

What I want to do now:

Start Feature 4. Read PROJECT.md, glance at MatrixFrame.cs and
SerialManager.cs to confirm the current pipeline, then propose a plan: where
the brightness / gamma transform should hook in, what UI (controls + where
they live), what persistence (if any), and the test plan. Don't write code
yet — get my agreement on the approach first. Keep the proposal focused on
Feature 4. Don't pre-plan Feature 3.
```
