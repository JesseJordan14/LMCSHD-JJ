# LMCSHD-JJ Project Plan

Personal fork of [`TechRandom/LMCSHD-TR`](https://github.com/TechRandom/LMCSHD-TR), which itself is a fork of [`TylerTimoJ/LMCSHD`](https://github.com/TylerTimoJ/LMCSHD). PC-side companion to the firmware in [`JesseJordan14/LED_Display`](https://github.com/JesseJordan14/LED_Display).

## Progress

- [x] **Feature 1: Native multi-panel section support** — Section data model + section-aware serial output + dialog UI + persistence; firmware demux removed
- [x] **Feature 2: Per-section orientation** — covered by Feature 1 (every Section row has its own Orientation/Origin/NewLine)
- [x] **Feature 3: Direct WebSocket from PC** — LMCSHD is the WS server; receivers connect directly. Source ESP retired to `legacy/`.
- [x] **Feature 4a: Brightness / gamma controls** — sliders + Reset buttons + LUT in `EmitRegion`, persisted to `display-prefs.dat`; verified on wall
- [ ] **Feature 4b: Dithering** — deferred; would hook in `SerialManager` color-quantization paths
- [x] **Feature 5: Built-in test patterns** — solid color / walking pixel / per-section color
- [ ] **Feature 6: Auto-on/off lifecycle + custom startup content** — wall mirrors PC awake/asleep state (except hibernate, see 6.8), shows configurable content when active
- [ ] **Feature 7: OTA receiver firmware updates** — flash the receiver ESPs over WiFi via Arduino IDE instead of unplugging from the LEDs every time

## Hardware target

- 32×32 WS2812B LED wall: **2× 16×32 panels** side-by-side
- 2× ESP8266 "receiver" boards (one per panel), each running its own WebSocket client and connecting directly to LMCSHD on the PC
- Each 16×32 panel is two 16×16 modules chained vertically
- Network: `10.0.0.x`; PC at DHCP-reserved `10.0.0.150:81` (LMCSHD WebSocket server)
- WiFi credentials and `PC_IP` / `PC_PORT` live in per-sketch `secrets.h` files (gitignored). Templates in each `secrets.example.h`.

## Architecture

**Happy path (Feature 3 done):**

```
PC (LMCSHD-JJ, WS server :81)  <--WebSocket-->  Receiver ESP × N  -->  LEDs
```

Receivers boot, connect to WiFi, dial `PC_IP:PC_PORT`. LMCSHD sends `"Who?"` (text) on each new connection; receiver replies `"Device N"` (text) to claim its slot. From then on, frame bytes flow as binary WS frames (`WStype_BIN`), one section per receiver, BPP16 (5-6-5). Receivers ack each frame with a single `0x06` byte (binary); LMCSHD waits for all-receivers-ready before sending the next, keeping panels in lockstep.

**Fallback path (still works):**

```
PC (LMCSHD-JJ)  --USB serial-->  Source ESP (legacy/)  --WebSocket-->  Receiver ESP × N  -->  LEDs
```

Source ESP firmware is retained under `Massive-LED-Wall-main/Code/legacy/LED_Wall_Source/` for reference and as a diagnostic fallback if you ever need to bypass the network path. Reflash the source ESP from there, point it at port 81, point the receivers back at the source ESP IP (`10.0.0.121`) by editing each receiver's `secrets.h` `PC_IP`. **Not the documented happy path.**

Serial protocol (one-byte opcodes), used only by the fallback source-ESP path:

| Opcode | Direction | Meaning |
|--------|-----------|---------|
| `0x05` | LMCSHD → ESP | "What's your matrix size?" (ESP replies `WIDTH\nHEIGHT\n`) |
| `0x41` | LMCSHD → ESP | Frame data follows — RGB888 (3 bytes/pixel), single-panel format |
| `0x42` | LMCSHD → ESP | Frame data follows — RGB565 (2 bytes/pixel); section-ordered |
| `0x06` | ESP → LMCSHD | Frame received and displayed (ack) |

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

### Feature 6: Auto-on/off lifecycle + custom startup content — *planned*

Goal: leave the wall plugged in 24/7 and have it mirror PC state automatically. PC awake (logged in, unlocked) → wall shows configurable content. PC locked, asleep, or shut down → wall blank. No manual intervention.

**Architecture: events get caught at three layers**

| User action | Protocol-level effect | Who reacts |
|-------------|----------------------|------------|
| PC shuts down | TCP closes hard, receiver fires `WStype_DISCONNECTED` | Firmware (clear LEDs in disconnect handler) |
| PC sleeps | Networking goes down ~immediately, same disconnect path | Firmware. LMCSHD also pushes black proactively for instant response |
| PC locks (no sleep) | TCP stays alive; receiver has no idea | LMCSHD only — `SystemEvents.SessionSwitch` → push black |
| PC wakes / unlocks / logs in | Network alive or coming back | LMCSHD — `SessionUnlock` / `PowerModes.Resume` / app start → push custom content |

Belt-and-suspenders: firmware-side blank-on-disconnect handles hard cases (LMCSHD crash, OS killing the process during sleep) where the PC-side handler couldn't run.

**Substeps:**

- [ ] **6.1** — Receiver firmware blanks LEDs on `WStype_DISCONNECTED`. One-line addition: `FastLED.clear(); FastLED.show();`. Reflash both receivers once. Foundation for everything else.
- [ ] **6.2** — `PowerStateController.cs` in LMCSHD: subscribes to `Microsoft.Win32.SystemEvents.SessionSwitch` (Lock / Unlock / Logon / Logoff) and `PowerModeChanged` (Suspend / Resume). State machine `Active ↔ Idle`. On Idle → push black frame. On Active → push current content. App-start defaults to Active.
- [ ] **6.3** — `Network → Disconnect` blanks the wall before stopping the WS server. Currently it leaves whatever frame was last sent on the panels; new behavior matches the lifecycle model.
- [ ] **6.4** — Manual `Wall: On / Off` toggle on the main window (probably in or near the brightness/gamma strip). Pushes black or pushes content without touching the WS server. Lets the user black the wall briefly without locking the PC.
- [ ] **6.5** — Auto-launch via Startup-folder shortcut. Window opens minimized. May need a `<Window WindowState="Minimized">` startup attribute or a `--minimized` arg.
- [ ] **6.6** — System tray icon (`NotifyIcon` from WinForms). Shows status (server listening, N/2 receivers connected). Right-click menu mirrors the manual override and exits the app cleanly.
- [ ] **6.7** — Custom-content modes. User picks one and configures it via a new settings dialog. Three modes for the first cut; more later.
  - **6.7a** — *Image / GIF file* at a user-configured path. Static PNG → push once on Activate. Animated GIF → loop while Active.
  - **6.7b** — *Screen mirror* — kicks off the existing Screen Recorder pipeline on Activate, stops on Idle.
  - **6.7c** — *Clock display* — render current time onto the matrix. Font + layout TBD; placeholder solid blocks of color is fine until we pick a style.
  - **6.7d** — *Settings UI*: dialog to pick mode and configure params (file path, screen region, clock format). Persisted to `display-prefs.dat` or a new `lifecycle-prefs.dat`.
- ~~**6.8**~~ — *Hibernate handling — ABANDONED 2026-05-01.* Tried two approaches; neither blanked the wall on hibernate.
  - *v1 (WS-protocol heartbeat):* receiver `webSocket.enableHeartbeat(5000, 2000, 2)` to detect dead connections. User waited several minutes hibernated; wall stayed lit. Suspect Windows keeps the WiFi NIC alive enough during hibernate that WS-layer pings still appear to succeed (NIC-level TCP offload, Wake-on-LAN keep-alives, etc.).
  - *v2 (app-level watchdog + LMCSHD keepalive timer):* receiver tracked last-WS-message time, blanked + force-disconnected after 3 s silence; LMCSHD pushed a keepalive frame every 1 s. Same result on hibernate — somehow the receivers kept seeing data, or the OS kept TCP alive enough that pushes still got through. Did fix LMCSHD-crash detection as a side effect, but that wasn't the original target.
  - Both attempts left in the source as commented-out blocks tagged `Feature 6.8 (abandoned 2026-05-01)` so they're a starting point if revisited. Code is in `LED_Wall_Reciever.ino` (top-of-file globals, `webSocketEvent` cases, `loop()`, and `setup()` heartbeat call) and `NetworkManager.cs` (timer field + Connect/Disconnect references).
  - Current behavior on hibernate: wall stays in its last state until something else trips a disconnect (could be hours via default TCP keepalive timeout). Lock and sleep still blank correctly via 6.2; shutdown blanks via 6.1's clean-disconnect path.
- [ ] **6.9** — *Auto-set matrix dimensions from receiver handshake reports.* Today `MatrixFrame.Width/Height` default to 16 and the `Edit → Matrix Dimensions...` dialog overrides, but the override doesn't persist — user has to re-enter dimensions every launch even though the actual wall size doesn't change.

  Approach: extend the existing `"Device N"` handshake to include the receiver's panel dimensions. Firmware reply becomes `"Device N WxH"` (space-separated, e.g. `"Device 1 16x32"`), pulling W/H from the existing `#define WIDTH` / `#define HEIGHT` in the receiver sketch. LMCSHD's `OnTextMessage` parses the extra field and stores per-device panel dimensions. Once a device identifies, LMCSHD computes the total wall (default: side-by-side, so total `Width = Σ panelWidths`, `Height = max(panelHeights)`) and calls `MatrixFrame.SetDimensions(totalW, totalH)`. The existing per-dimension sections file (`sections-WxH.dat`) then auto-loads.

  Forward/backward compatible: if the dims field is absent (old or stripped firmware), parser falls through and dimensions don't get auto-set — user can still use the manual dialog. If panel layout isn't side-by-side (stacked, 2x2 grid, etc.), the user-edited Sections list takes precedence over the auto-derived layout — only the total `Width/Height` get set from the reports, layout itself stays under user control.

  Persistence is *not* sufficient on its own (was the prior plan) — this approach is more robust against config drift: swap a panel, reflash with a different `DEVICE_NUM`, change panel dims, and LMCSHD adapts. Persistence could be added as a complementary fallback for cold-start when no receivers are connected yet, but isn't strictly needed: the wall stays its default size until receivers come online and report.

  Both repos change: receiver firmware sketch (LED_Display) for the new reply format, and `NetworkManager.OnTextMessage` + `MatrixFrame` for the parse + auto-set logic.

**Decisions captured (2026-04-29):**

- **Q: Should `Network → Disconnect` blank the wall?** A: Yes (substep 6.3).
- **Q: Active state when no receivers connected yet?** A: Mark Active anyway; `PushFrame` is a no-op until receivers identify, then any pending state flushes via the existing 3.5 ack-pending mechanism. Nothing extra needed in 6.2.
- **Q: What custom content?** A: Three modes — image/GIF file, screen mirror, clock — user-configurable. More modes can be added later.
- **Q: Lock = blank, or only sleep/shutdown?** A: Lock + sleep + shutdown all blank for now. Customizability (per-event behavior) is a deliberate follow-up for after 6.7 ships.
- **Q: Auto-launch minimized?** A: Yes (substep 6.5).
- **Q: Tray icon?** A: Yes (substep 6.6).
- **Q: Manual wall on/off override?** A: Yes (substep 6.4).

**Added 2026-04-30 (after 6.6 shipped):**

- *Wall must blank on hibernate too.* See substep 6.8 — abandoned 2026-05-01 after two failed attempts.
- *LMCSHD should auto-set the matrix dimensions on launch instead of requiring the user to re-enter them every time.* See substep 6.9. Initial plan was to persist dimensions; revised to have receivers report panel dimensions in the handshake — more robust against config drift, single source of truth (the firmware compile-time constants).

**Future Feature 6 follow-ups (not in initial scope):**

- Per-event blank behavior settings (e.g. "lock doesn't blank, sleep does"). User explicitly deferred this — current default is "all three events blank."
- More content modes (slideshow, audio-reactive, weather, custom shader-style animations).
- "Resume from last frame" mode that snapshots `Frame[]` on Idle and restores on Activate, in addition to the configured content mode.
- Tray-icon affordances beyond status (e.g. live preview, brightness slider).

### Feature 7: OTA receiver firmware updates — *planned*

Goal: stop unplugging receivers from the LEDs every time the firmware needs updating. Push new sketches over WiFi via the Arduino IDE's network-port mechanism instead. Pure firmware-side feature — no LMCSHD changes.

**How it works:** receiver firmware adds `ArduinoOTA.begin()` in setup and `ArduinoOTA.handle()` in the main loop. The board advertises itself via mDNS (`led-receiver-1.local` / `led-receiver-2.local`) and shows up under Arduino IDE's `Tools → Port → Network ports`. Selecting it and clicking Upload pushes the compiled `.bin` over UDP/8266; the bootloader writes the new firmware to the staging partition, swaps, and reboots. Built into the ESP8266 Arduino core — no external dependencies.

**Substeps:**

- [ ] **7.1** — Firmware: `#include <ArduinoOTA.h>`, set hostname per-board (`led-receiver-<DEVICE_NUM>`), set password from `secrets.h`, call `begin()` in `setup()` after WiFi connect, call `handle()` in `loop()` before `webSocket.loop()`. The `handle()` call is cheap when no OTA is in progress.
- [ ] **7.2** — Update `secrets.example.h` template to include `#define OTA_PASSWORD "your_ota_password"`. User updates their gitignored `secrets.h` accordingly.
- [ ] **7.3** — One-time USB reflash of both receivers with the OTA-capable firmware. After this, OTA is the deployment path.
- [ ] **7.4** — Verify OTA: edit a no-op (e.g. tweak a Serial.println), click Upload with the network port selected, watch receiver reboot and reconnect.

**Decisions / defaults:**

- *Authentication.* Password-protected. Without one, anyone on the LAN could push firmware. Password lives in gitignored `secrets.h`, same pattern as WiFi creds.
- *Hostname format.* `led-receiver-<DEVICE_NUM>` — derives from existing `#define DEVICE_NUM`, so each board's hostname is automatically unique. Keeps `secrets.h` symmetric across boards.
- *Discovery.* Arduino IDE auto-discovers via mDNS, which on Windows requires Bonjour for Windows installed once. Workaround if Bonjour isn't present: type `<receiver-ip>:8266` manually into the IDE's port field.
- *No LMCSHD-side OTA UI.* Arduino IDE handles the upload mechanics; no value in re-implementing `espota.py` in C# for this hobby project.

**Constraints worth knowing:**

- *Flash size budget.* OTA stages the new image in the second half of flash before swapping. Current sketch (~few hundred KB) fits comfortably in the ~1 MB usable. Grow with care if the firmware ever bloats.
- *Mid-flash unresponsiveness.* During the ~5–10 s of OTA write + reboot, the receiver doesn't process WS frames. Wall stays on whatever it last had, briefly disconnects, then reconnects on the new firmware. Easiest to flash with `Wall: Off` toggled via the tray menu.
- *Bricking risk.* Low — bootloader rolls back on CRC failure. A genuinely broken sketch (e.g. infinite loop in `setup()` before `ArduinoOTA.begin()`) could brick a board and require USB recovery, but this requires actual code error, not just a network glitch.

### Feature 3: Direct WebSocket from PC — *done*

Source ESP retired. LMCSHD is the WebSocket server (`NetworkManager.cs`, Fleck-backed); receivers stay as WS clients and dial the PC. Per-section frame routing with per-receiver ack gating keeps panels in lockstep.

**Substeps:**

- [x] **3.1** — Receiver firmware points at PC via `secrets.h` (`PC_IP` / `PC_PORT`). LED_Display `96ff2b6`.
- [x] **3.2** — `NetworkManager.cs` Fleck-backed WS server with auto-start in `MainWindow` ctor, `Network → Connect... / Disconnect` menu, and `MatrixNetworkConnection` dialog. `"Who?"` / `"Device N"` handshake, `ReceiverIdentified` / `ReceiverDisconnected` events. LMCSHD-JJ `fdb4550`.
- ~~**3.3**~~ — skipped; the existing dialog covers status display.
- [x] **3.4** — Per-section frame routing. `MatrixFrame.GetSectionFrames()` returns per-section RGB888 buffers; `NetworkManager.PushFrame` packs each to BPP16 and sends to the mapped receiver. Section index N → device N+1. Color mode UI locked to BPP16 (other modes visible-but-disabled). Firmware-side: `WStype_BIN` handler added because Fleck sends proper binary frames, not the legacy TXT-with-binary-payload hack. LED_Display `7be6f6f`, LMCSHD-JJ `e700ea9`.
- [x] **3.5** — Per-receiver `0x06` ack gating. Receiver firmware sends `webSocket.sendBIN(0x06, 1)` after `FastLED.show()`. `NetworkManager` tracks `_deviceReady` per device and only flushes when all are ready; pending frames flush on the final ack. Effect: panels stay locked together, producer throttled to slowest receiver. Same commits as 3.4.
- [x] **3.6** — Source ESP retired to `Massive-LED-Wall-main/Code/legacy/LED_Wall_Source/`. Receiver firmware's `WStype_TEXT` BPP16-decode branch dropped (it was only there for the legacy source-ESP path); TEXT case now exclusively handles the `"Who?"` handshake.

**Gotchas worth remembering:**

- *Fleck's `WebSocketServer.Dispose()` only closes the listener socket*, not active connections. `NetworkManager.Disconnect()` walks `_devices` + `_pending` and calls `IWebSocketConnection.Close()` on each before disposing the server, then sleeps 100 ms to let close frames flush. Without this, receivers stay in a half-alive WebSocket state across a Disconnect→Connect cycle and only recover on physical replug.
- *The legacy upstream protocol shipped binary pixel data over WS **text** frames* via the source-ESP firmware's `webSocket.sendTXT(client, (char*)bytes, length)` overload. Receiver was written to decode under `case WStype_TEXT` only. With Fleck sending proper binary, that switch fell through to `default:` and silently did nothing. Receiver now has both a `WStype_BIN` decoder and a TEXT-handshake-only case; the legacy TXT BPP16 branch is gone in 3.6.
- *No ack timeout in `NetworkManager`.* If a receiver crashes / loses WiFi mid-frame, its ack never arrives and the PC waits forever. Workaround: `Network → Disconnect / Connect`. Add a watchdog if this turns out to bite in practice.
- *Brightness stacking.* Receiver firmware caps at `MAX_BRIGHTNESS = 200` (out of 255), then `map()`s the 5/6-bit channel values into that range. Now that LMCSHD has its own brightness/gamma slider in software, this 78% firmware cap is doing redundant work. Fine for now; consider raising to 255 (or removing the map) as a follow-up.

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

- `LED_Wall_Reciever/LED_Wall_Reciever.ino` — receiver ESP. WebSocket client; dials `PC_IP:PC_PORT` from `secrets.h`. `#define DEVICE_NUM 1` (or 2) per board selects which slot it claims. Pixel frames arrive as `WStype_BIN`, decoded by `decodeBPP16AndShow` which acks with a single `0x06` byte after `FastLED.show()`. Text frames are handshake-only (`"Who?"` → `"Device N"`).
- `legacy/LED_Wall_Source/LED_Wall_Source.ino` — retired source ESP. Kept for diagnostic fallback only; LMCSHD-JJ's `NetworkManager` replaces this device on the happy path.
- `Single_LED_Wall_*` — earlier single-panel sketches kept for reference. Not on the current happy path.
- `*/secrets.h` — WiFi credentials and (for receivers) `PC_IP` / `PC_PORT`, **gitignored**. `*/secrets.example.h` is the committed template.

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
2. Build LMCSHD with VS MSBuild. Re-flash receivers via Arduino IDE if firmware changed.
3. Power the receivers on. They auto-dial `PC_IP:PC_PORT` from their `secrets.h` and retry every 5s if the server isn't up yet.
4. Run `LMCSHD\LMCSHD\bin\x64\Debug\LMCSHD.exe`. The WebSocket server auto-starts on port 81. Within ~5s both receivers should connect.
5. Open `Network → Connect...` to confirm `Device 1` and `Device 2` are listed.
6. Matrix dimensions are 32×32 (set in `MatrixFrame.Width`/`Height`). Sections auto-load from save file or fall back to the test scaffolding.
7. Use any source tab (Screen Recorder / Imaging / Test Patterns) to push content. Verify on the wall — panels stay synced, framerate matches the slowest receiver.

Fallback (serial via legacy source ESP, if you ever need it):

1. Reflash source ESP from `legacy/LED_Wall_Source/`.
2. Reflash receivers with `secrets.h` `PC_IP` pointing at the source ESP's static `10.0.0.121`.
3. `Serial → Connect` in LMCSHD: source ESP's COM port, baud `921600`, color mode `16bpp RGB`.

Common diagnostic moves:

- Wall scrambled → `Test Patterns → Per-Section ID` to confirm the section-to-panel mapping is right.
- Halves swapped → swap the two rows in the Sections dialog (no firmware reflash needed); or re-flash one receiver with `DEVICE_NUM` swapped (1↔2).
- One section's orientation off → Sections dialog → flip its Origin/NewLine for that row.
- Wall freezes with one panel still alive → that receiver crashed or lost WiFi and never sent its `0x06` ack. `Network → Disconnect / Connect` resets state.

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
- Features 1, 2, 3, 4a, 5 are done and verified on the actual wall.
  Source ESP is retired to legacy/; LMCSHD is the WebSocket server and
  receivers connect directly.
- Feature 6 (auto-on/off lifecycle + custom startup content) is in
  progress. 6.1–6.6 + 6.9 are done. 6.8 (hibernate handling) was
  abandoned after two failed attempts — wall stays in its last state
  on hibernate; lock/sleep/shutdown still blank correctly. 6.7
  (configurable content modes — image/GIF, screen mirror, clock) is
  the only remaining piece for Feature 6.
- Feature 7 (OTA receiver firmware updates) is planned for after 6.7.
  Pure firmware-side change so future receiver updates don't need a
  USB reflash.
- Feature 4b (dithering) is deferred — only do it if banding shows up
  after gamma at low brightness.
- Open follow-ups noted in PROJECT.md: receiver firmware MAX_BRIGHTNESS
  cap is now redundant given software brightness; csproj migration to
  SDK-style; persistence scope for global orientation/color mode/COM port.

Non-obvious gotchas you must know up front:

- Build LMCSHD-JJ with Visual Studio's MSBuild (path in PROJECT.md), NOT
  `dotnet build`. The latter fails on this old-style .NET Framework 4.7.2 WPF
  project regardless of SDK version. Output is bin\x64\Debug\LMCSHD.exe.
- WiFi credentials and PC_IP/PC_PORT live in gitignored secrets.h files in
  each Arduino sketch folder. Templates are in secrets.example.h. Never
  commit real creds.
- The PC has a DHCP-reserved IP at 10.0.0.150 on the 10.0.0.x network;
  receivers dial that. Source ESP is retired to
  Massive-LED-Wall-main/Code/legacy/LED_Wall_Source/ and is no longer used
  on the happy path.
- LMCSHD's color mode menu and the serial-connect dialog show non-BPP16
  options as visible-but-disabled — receiver firmware only decodes 5-6-5
  today. Don't re-enable them without firmware support.
- There's intentional scaffolding in MatrixFrame.UseTestSectionsOn32x32 that
  PROJECT.md flags for eventual removal — don't reflexively "clean it up."
- `MatrixFrame.EmitRegion` is the single source of truth for per-pixel
  serpentine ordering and the brightness/gamma LUT; `AppendRegionCoords`
  and `GetSectionFrames` parallel its traversal. If you change one, check
  the others.

What I want to do now:

Continue Feature 6 (auto-on/off lifecycle + custom startup content). Read
PROJECT.md's Feature 6 section for the full plan including the recorded
design decisions. Pick up at the next unchecked substep (6.1 first time
through). Don't re-litigate the design decisions already captured —
proceed unless something has changed.

[Or replace with another task. Other follow-ups noted in PROJECT.md:
Feature 4b dithering, removing the firmware MAX_BRIGHTNESS=200 cap now
that LMCSHD has software brightness, MatrixFrame.UseTestSectionsOn32x32
scaffolding cleanup, persisting global orientation/color mode/COM port,
csproj migration to SDK-style.]
```
