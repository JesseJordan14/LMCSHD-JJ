using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using Fleck;

namespace LMCSHD
{
    // WebSocket server side of the Feature 3 transport. Receivers connect to us;
    // we identify each one via the "Who?" / "Device N" handshake (same protocol
    // that previously ran between the source ESP and receivers — see
    // LED_Wall_Reciever.ino webSocketEvent).
    //
    // Step 3.2 scope: connection management and identification only. Frame
    // pushing and ack handshake live in later steps (3.4 / 3.5).
    public static class NetworkManager
    {
        private static WebSocketServer _server;

        // Connections that have opened but not yet replied to "Who?".
        private static readonly HashSet<IWebSocketConnection> _pending = new HashSet<IWebSocketConnection>();
        private static readonly object _pendingLock = new object();

        // Connections that identified themselves, keyed by device number.
        private static readonly ConcurrentDictionary<int, IWebSocketConnection> _devices =
            new ConcurrentDictionary<int, IWebSocketConnection>();

        // Feature 6.9: panel pixel dimensions reported by each device in its
        // identification reply ("Device N WxH"). Used to compute total wall
        // dimensions when a receiver identifies, so MatrixFrame.SetDimensions
        // can be called automatically. Tuple.Item1 = width, Item2 = height.
        private static readonly ConcurrentDictionary<int, Tuple<int, int>> _devicePanelDims =
            new ConcurrentDictionary<int, Tuple<int, int>>();

        // Per-device "ready for next frame" gate. True after a 0x06 ack arrives
        // (or right after identification, before any frame has been pushed).
        // Cleared per-device when we send that device a frame; set by ack.
        private static readonly ConcurrentDictionary<int, bool> _deviceReady =
            new ConcurrentDictionary<int, bool>();

        // Set when OnFrameChanged fires while waiting for acks. Cleared when
        // we successfully push the queued state. Effectively "the source has
        // newer state we haven't shipped yet"; on the final ack we flush it.
        private static volatile bool _pendingFrame = false;
        private static readonly object _flushLock = new object();

        // Lifecycle gate (Feature 6.2). When false, DoPushFrame ships zeroed
        // bytes per section — wall goes blank regardless of what
        // MatrixFrame.Frame contains. Source events keep firing normally;
        // they just get force-zeroed at send time. Toggled by
        // PowerStateController on lock / sleep / unlock / wake events.
        private static volatile bool _isActive = true;
        public static bool IsActive { get { return _isActive; } }

        /* Feature 6.8 v2 (abandoned 2026-05-01) — keepalive timer companion
         * to the receiver-side app-level watchdog. Pushed the current frame
         * state every 1s so receivers had a steady stream of messages to
         * watchdog against. Worked for LMCSHD-crash detection but didn't
         * solve the original hibernate motivation, so abandoned.
         *
         * private static System.Threading.Timer _keepaliveTimer;
         * private const int KeepaliveIntervalMs = 1000;
         */

        // Events fire on Fleck's worker threads — subscribers must marshal to
        // the UI dispatcher themselves (same pattern as SerialManager).
        public delegate void ReceiverIdentifiedHandler(int deviceNum);
        public static event ReceiverIdentifiedHandler ReceiverIdentified;

        public delegate void ReceiverDisconnectedHandler(int deviceNum);
        public static event ReceiverDisconnectedHandler ReceiverDisconnected;

        // Fires whenever IsActive changes (lock/unlock, sleep/wake, manual
        // toggle). Subscribers can sync UI state without polling.
        public delegate void ActiveStateChangedHandler(bool isActive);
        public static event ActiveStateChangedHandler ActiveStateChanged;

        public const int DefaultPort = 81;
        public static int ListenPort { get; private set; } = DefaultPort;
        public static bool IsListening { get { return _server != null; } }
        public static IReadOnlyDictionary<int, IWebSocketConnection> Devices { get { return _devices; } }

        public static bool Connect(int port)
        {
            // Fleck's default tracing is verbose — Warn keeps the console quiet
            // unless something actually goes wrong.
            FleckLog.Level = LogLevel.Warn;

            Disconnect();
            try
            {
                _server = new WebSocketServer("ws://0.0.0.0:" + port);
                _server.RestartAfterListenError = true;
                _server.Start(socket =>
                {
                    socket.OnOpen = () => OnSocketOpen(socket);
                    socket.OnClose = () => OnSocketClose(socket);
                    socket.OnMessage = msg => OnTextMessage(socket, msg);
                    socket.OnBinary = bytes => OnBinaryMessage(socket, bytes);
                });
                ListenPort = port;

                MatrixFrame.FrameChanged -= OnFrameChanged;
                MatrixFrame.FrameChanged += OnFrameChanged;

                /* Feature 6.8 v2 (abandoned 2026-05-01).
                 * _keepaliveTimer?.Dispose();
                 * _keepaliveTimer = new System.Threading.Timer(
                 *     _ => PushFrame(), null, KeepaliveIntervalMs, KeepaliveIntervalMs);
                 */
                return true;
            }
            catch (SocketException)
            {
                if (_server != null) { try { _server.Dispose(); } catch { } _server = null; }
                return false;
            }
        }

        public static void Disconnect()
        {
            // Unsubscribe first so no in-flight FrameChanged from a live
            // source races our cleanup blank push below.
            MatrixFrame.FrameChanged -= OnFrameChanged;

            /* Feature 6.8 v2 (abandoned 2026-05-01).
             * _keepaliveTimer?.Dispose();
             * _keepaliveTimer = null;
             */

            // Feature 6.3: push a blank frame to every connected device
            // before tearing down. The firmware-side blank-on-disconnect
            // (6.1) blanks the wall when receivers see the close frame
            // anyway, but pushing black explicitly here makes the
            // transition feel instant rather than waiting on the close-
            // frame round-trip + receiver render. Best-effort: no ack
            // waiting (we're tearing down anyway). _isActive is left alone
            // — that flag belongs to PowerStateController, not Disconnect.
            var sectionFrames = MatrixFrame.GetSectionFrames();
            for (int i = 0; i < sectionFrames.Count; i++)
            {
                int deviceNum = i + 1;
                IWebSocketConnection conn;
                if (!_devices.TryGetValue(deviceNum, out conn)) continue;
                int bppByteLen = (sectionFrames[i].Length / 3) * 2; // RGB888 → BPP16
                byte[] blank = new byte[bppByteLen];
                try { conn.Send(blank); } catch { }
            }

            // Fleck's WebSocketServer.Dispose() closes only the listener socket
            // — established client connections are NOT torn down. If we don't
            // close them ourselves, the receiver firmware never sees a close
            // frame or TCP RST and stays stuck in its half-alive WebSocket
            // until you replug the board. Walk both maps and close each one
            // explicitly, then sleep briefly to let the close frames flush
            // over the wire before we kill the server thread.
            var connsToClose = new List<IWebSocketConnection>();
            foreach (var c in _devices.Values) connsToClose.Add(c);
            lock (_pendingLock)
            {
                foreach (var c in _pending) connsToClose.Add(c);
            }
            foreach (var c in connsToClose) { try { c.Close(); } catch { } }
            if (connsToClose.Count > 0)
                System.Threading.Thread.Sleep(100);

            var server = _server;
            _server = null;
            if (server != null) { try { server.Dispose(); } catch { } }

            lock (_pendingLock) { _pending.Clear(); }
            var nums = new List<int>(_devices.Keys);
            _devices.Clear();
            _deviceReady.Clear();
            _devicePanelDims.Clear();
            _pendingFrame = false;
            foreach (var n in nums) ReceiverDisconnected?.Invoke(n);
        }

        private static void OnSocketOpen(IWebSocketConnection socket)
        {
            lock (_pendingLock) { _pending.Add(socket); }
            try { socket.Send("Who?"); } catch { /* socket already gone */ }
        }

        private static void OnSocketClose(IWebSocketConnection socket)
        {
            lock (_pendingLock) { _pending.Remove(socket); }

            int? identifiedNum = null;
            foreach (var kv in _devices)
            {
                if (ReferenceEquals(kv.Value, socket)) { identifiedNum = kv.Key; break; }
            }
            if (identifiedNum.HasValue)
            {
                IWebSocketConnection _;
                _devices.TryRemove(identifiedNum.Value, out _);
                bool _b;
                _deviceReady.TryRemove(identifiedNum.Value, out _b);
                Tuple<int, int> _d;
                _devicePanelDims.TryRemove(identifiedNum.Value, out _d);
                ReceiverDisconnected?.Invoke(identifiedNum.Value);

                // The departing device may have been the last holdout for a
                // pending frame. Try to flush now that we're no longer waiting
                // on it.
                TryFlushPending();
            }
        }

        private static void OnTextMessage(IWebSocketConnection socket, string msg)
        {
            // Identification reply formats:
            //   "Device N"         (legacy / dimensions unreported)
            //   "Device N WxH"     (Feature 6.9 — receiver also reports its
            //                       panel pixel dimensions for auto-sizing)
            // Anything else is ignored (text frames are exclusively handshake
            // messages now; pixel data flows via WStype_BIN).
            if (msg == null || !msg.StartsWith("Device ")) return;
            var parts = msg.Substring(7).Trim().Split(' ');
            if (parts.Length < 1) return;
            int deviceNum;
            if (!int.TryParse(parts[0], out deviceNum)) return;

            // Optional dimensions field: "WxH". Forward-compatible — absence
            // (parts.Length < 2) just skips the auto-SetDimensions side-effect.
            int panelW = 0, panelH = 0;
            if (parts.Length >= 2)
            {
                var dims = parts[1].Split('x');
                if (dims.Length == 2)
                {
                    int.TryParse(dims[0], out panelW);
                    int.TryParse(dims[1], out panelH);
                }
            }

            // If another connection already claimed this slot, kick it. The most
            // recent claimant wins so reflashed/restarted boards reclaim cleanly.
            IWebSocketConnection prior;
            if (_devices.TryGetValue(deviceNum, out prior) && !ReferenceEquals(prior, socket))
            {
                try { prior.Close(); } catch { }
                ReceiverDisconnected?.Invoke(deviceNum);
            }
            _devices[deviceNum] = socket;
            // Newly-identified devices start ready: we haven't sent them
            // anything yet, so they don't owe us an ack.
            _deviceReady[deviceNum] = true;
            lock (_pendingLock) { _pending.Remove(socket); }
            ReceiverIdentified?.Invoke(deviceNum);

            if (panelW > 0 && panelH > 0)
            {
                _devicePanelDims[deviceNum] = Tuple.Create(panelW, panelH);
                UpdateMatrixDimensionsFromReports();
            }
        }

        // Feature 6.9: aggregate reported panel dimensions into a total wall
        // size and call MatrixFrame.SetDimensions if the result differs from
        // the current dims. Default layout assumption: side-by-side, so total
        // Width = Σ panelWidths, Height = max(panelHeights). Layout itself
        // stays under user control via the Sections dialog — only the total
        // dimensions get auto-set from receiver reports. SetDimensions
        // mutates static state and fires events that subscribers expect on
        // the UI thread, so we marshal via Application.Current.Dispatcher.
        private static void UpdateMatrixDimensionsFromReports()
        {
            int totalW = 0;
            int maxH = 0;
            foreach (var kv in _devicePanelDims)
            {
                totalW += kv.Value.Item1;
                if (kv.Value.Item2 > maxH) maxH = kv.Value.Item2;
            }
            if (totalW <= 0 || maxH <= 0) return;
            if (totalW == MatrixFrame.Width && maxH == MatrixFrame.Height) return;

            var app = System.Windows.Application.Current;
            if (app != null)
                app.Dispatcher.Invoke(() => MatrixFrame.SetDimensions(totalW, maxH));
            else
                MatrixFrame.SetDimensions(totalW, maxH);
        }

        // Receivers send a single 0x06 byte (binary frame) after FastLED.show()
        // to signal "ready for the next frame". Mark the sending device ready,
        // then try to flush any pending frame that was held back while we were
        // waiting on this ack.
        private static void OnBinaryMessage(IWebSocketConnection socket, byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1 || bytes[0] != 0x06) return;

            int? deviceNum = null;
            foreach (var kv in _devices)
            {
                if (ReferenceEquals(kv.Value, socket)) { deviceNum = kv.Key; break; }
            }
            if (!deviceNum.HasValue) return;

            _deviceReady[deviceNum.Value] = true;
            TryFlushPending();
        }

        private static void OnFrameChanged()
        {
            PushFrame();
        }

        // Per-section frame routing with all-receivers-ready ack gating.
        //
        // Section index N → device N+1 (firmware DEVICE_NUM is 1-based; the
        // Sections list is 0-indexed). Sections beyond the connected receiver
        // count are silently skipped.
        //
        // Wire format is always BPP16 (5-6-5) regardless of SerialManager.ColorMode
        // — receiver firmware (LED_Wall_Reciever.ino) only decodes 5-6-5 today.
        // Edit → Color Mode menu and the serial-connect dialog disable other
        // options to make this visible. Lift when firmware grows other decoders.
        //
        // Ack gating: we only push when every connected device is ready (last
        // frame's 0x06 ack received). If a frame fires while we're still waiting
        // on acks, we mark _pendingFrame and re-check on each incoming ack —
        // that way the wall ends up showing the most recent state even when
        // the producer outpaces the receivers, while panels stay locked together
        // because we never start frame N+1 until both finished frame N.
        public static void PushFrame()
        {
            lock (_flushLock)
            {
                if (_server == null) return;
                if (_devices.IsEmpty) return;

                if (AllDevicesReady_LockHeld())
                {
                    _pendingFrame = false;
                    DoPushFrame_LockHeld();
                }
                else
                {
                    _pendingFrame = true;
                }
            }
        }

        // Lifecycle gate. If `active` flips, force a push so the new state
        // (current content vs. blank) takes effect immediately rather than
        // waiting for the next FrameChanged from the source. Notify
        // subscribers (e.g. the Wall toggle button label) of the change
        // outside the lock to avoid reentrancy.
        public static void SetActive(bool active)
        {
            bool changed;
            lock (_flushLock)
            {
                changed = (_isActive != active);
                _isActive = active;
            }
            if (changed)
            {
                PushFrame();
                ActiveStateChanged?.Invoke(active);
            }
        }

        private static void TryFlushPending()
        {
            lock (_flushLock)
            {
                if (!_pendingFrame) return;
                if (_server == null) return;
                if (_devices.IsEmpty) return;
                if (!AllDevicesReady_LockHeld()) return;

                _pendingFrame = false;
                DoPushFrame_LockHeld();
            }
        }

        private static bool AllDevicesReady_LockHeld()
        {
            foreach (var deviceNum in _devices.Keys)
            {
                bool ready;
                if (!_deviceReady.TryGetValue(deviceNum, out ready) || !ready)
                    return false;
            }
            return true;
        }

        private static void DoPushFrame_LockHeld()
        {
            var sectionFrames = MatrixFrame.GetSectionFrames();
            for (int i = 0; i < sectionFrames.Count; i++)
            {
                int deviceNum = i + 1;
                IWebSocketConnection conn;
                if (!_devices.TryGetValue(deviceNum, out conn)) continue;

                // When inactive, send zeroed bytes the same shape as the section
                // would have produced. PackBPP16 of all zeros = all zeros = blank.
                byte[] rgb = _isActive
                    ? sectionFrames[i]
                    : new byte[sectionFrames[i].Length];
                byte[] bpp16 = PackBPP16(rgb);
                try
                {
                    conn.Send(bpp16);
                    _deviceReady[deviceNum] = false;
                }
                catch { /* OnClose will fire and clean up if the socket is dead */ }
            }
        }

        // RGB888 → 5-6-5 BPP16 packing. Byte0 = RRRRRGGG (top 5 of R, top 3 of G);
        // Byte1 = GGGBBBBB (low 3 of G, top 5 of B). Matches the unpack in
        // LED_Wall_Reciever.ino's webSocketEvent. Same bit layout as the BPP16
        // branch in SerialManager.PushFrame; kept independent here because the
        // serial path operates on the concatenated buffer and this one operates
        // per-section. If a third caller ever needs BPP16, extract a shared helper.
        private static byte[] PackBPP16(byte[] rgb888)
        {
            int pixels = rgb888.Length / 3;
            byte[] outBuf = new byte[pixels * 2];
            for (int i = 0; i < pixels; i++)
            {
                byte r = (byte)(rgb888[i * 3]     & 0xF8);
                byte g = (byte)(rgb888[i * 3 + 1] & 0xFC);
                byte b = (byte)(rgb888[i * 3 + 2] & 0xF8);
                outBuf[i * 2]     = (byte)(r | (g >> 5));
                outBuf[i * 2 + 1] = (byte)((g << 3) | (b >> 3));
            }
            return outBuf;
        }
    }
}
