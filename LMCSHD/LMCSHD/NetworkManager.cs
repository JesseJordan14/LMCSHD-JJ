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
                ReceiverDisconnected?.Invoke(identifiedNum.Value);
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
            lock (_pendingLock) { _pending.Remove(socket); }
            ReceiverIdentified?.Invoke(deviceNum);
        }

        private static void OnBinaryMessage(IWebSocketConnection socket, byte[] bytes)
        {
            // 3.5 will look for the 0x06 ack byte here. For now, ignore.
        }
    }
}
