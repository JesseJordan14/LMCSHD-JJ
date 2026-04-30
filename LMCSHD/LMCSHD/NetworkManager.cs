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

        // Events fire on Fleck's worker threads — subscribers must marshal to
        // the UI dispatcher themselves (same pattern as SerialManager).
        public delegate void ReceiverIdentifiedHandler(int deviceNum);
        public static event ReceiverIdentifiedHandler ReceiverIdentified;

        public delegate void ReceiverDisconnectedHandler(int deviceNum);
        public static event ReceiverDisconnectedHandler ReceiverDisconnected;

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
            MatrixFrame.FrameChanged -= OnFrameChanged;

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
                ReceiverDisconnected?.Invoke(identifiedNum.Value);

                // The departing device may have been the last holdout for a
                // pending frame. Try to flush now that we're no longer waiting
                // on it.
                TryFlushPending();
            }
        }

        private static void OnTextMessage(IWebSocketConnection socket, string msg)
        {
            // Only message we expect right now: "Device N" in reply to "Who?".
            if (msg == null || !msg.StartsWith("Device ")) return;
            int deviceNum;
            if (!int.TryParse(msg.Substring(7).Trim(), out deviceNum)) return;

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

                byte[] bpp16 = PackBPP16(sectionFrames[i]);
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
