using System;
using System.Drawing;
using System.Reflection;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace LMCSHD
{
    // Feature 6.6: system tray icon. NotifyIcon is from WinForms (LMCSHD-JJ
    // already references System.Windows.Forms + System.Drawing for other
    // reasons). Hosts a context menu with Show/Hide, manual Wall toggle,
    // Network Connect/Disconnect mirroring the main menu, and Exit. Tooltip
    // shows live status (Wall on/off, N receivers connected).
    //
    // The Exit menu item asks MainWindow to actually shut down via an
    // injected Action, instead of TrayController knowing about MainWindow's
    // close-to-tray flag. Keeps the dependency one-directional.
    public class TrayController : IDisposable
    {
        private readonly Window _window;
        private readonly Action _exitAction;
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ToolStripMenuItem _wallToggleItem;
        private readonly WinForms.ToolStripMenuItem _connectItem;
        private readonly WinForms.ToolStripMenuItem _disconnectItem;
        private bool _disposed = false;

        public TrayController(Window window, Action exitAction)
        {
            _window = window;
            _exitAction = exitAction;

            _icon = new WinForms.NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                Visible = true,
                Text = "LMCSHD-JJ"
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show / Hide window", null, OnShowHideClicked);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            _wallToggleItem = new WinForms.ToolStripMenuItem("Wall: On", null, OnWallToggleClicked);
            menu.Items.Add(_wallToggleItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            _connectItem = new WinForms.ToolStripMenuItem("Connect", null, OnConnectClicked);
            _disconnectItem = new WinForms.ToolStripMenuItem("Disconnect", null, OnDisconnectClicked);
            menu.Items.Add(_connectItem);
            menu.Items.Add(_disconnectItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => _exitAction?.Invoke());
            _icon.ContextMenuStrip = menu;

            _icon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) ToggleWindow();
            };

            NetworkManager.ActiveStateChanged += OnNetworkActiveStateChanged;
            NetworkManager.ReceiverIdentified += OnReceiverChanged;
            NetworkManager.ReceiverDisconnected += OnReceiverChanged;

            UpdateMenuLabels(NetworkManager.IsActive);
            UpdateTooltip();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NetworkManager.ActiveStateChanged -= OnNetworkActiveStateChanged;
            NetworkManager.ReceiverIdentified -= OnReceiverChanged;
            NetworkManager.ReceiverDisconnected -= OnReceiverChanged;
            _icon.Visible = false;
            _icon.Dispose();
        }

        private void OnShowHideClicked(object s, EventArgs e) => ToggleWindow();
        private void OnWallToggleClicked(object s, EventArgs e) =>
            NetworkManager.SetActive(!NetworkManager.IsActive);
        private void OnConnectClicked(object s, EventArgs e) =>
            NetworkManager.Connect(NetworkManager.DefaultPort);
        private void OnDisconnectClicked(object s, EventArgs e) =>
            NetworkManager.Disconnect();

        private void ToggleWindow()
        {
            _window.Dispatcher.Invoke(() =>
            {
                if (_window.WindowState == WindowState.Minimized || !_window.IsVisible)
                {
                    _window.Show();
                    _window.WindowState = WindowState.Normal;
                    _window.ShowInTaskbar = true;
                    _window.Activate();
                }
                else
                {
                    _window.WindowState = WindowState.Minimized;
                    // StateChanged handler in MainWindow flips ShowInTaskbar=false.
                }
            });
        }

        private void OnNetworkActiveStateChanged(bool isActive)
        {
            _window.Dispatcher.Invoke(() =>
            {
                UpdateMenuLabels(isActive);
                UpdateTooltip();
            });
        }

        private void OnReceiverChanged(int deviceNum)
        {
            _window.Dispatcher.Invoke(UpdateTooltip);
        }

        private void UpdateMenuLabels(bool isActive)
        {
            _wallToggleItem.Text = isActive ? "Wall: On (click to turn off)"
                                            : "Wall: Off (click to turn on)";
        }

        private void UpdateTooltip()
        {
            int connected = 0;
            foreach (var _ in NetworkManager.Devices) connected++;
            string state = NetworkManager.IsActive ? "On" : "Off";
            string text = "LMCSHD-JJ — Wall: " + state + ", " + connected +
                          " receiver" + (connected == 1 ? "" : "s") + " connected";
            // NotifyIcon.Text is capped at 63 chars on older Windows; trim defensively.
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            _icon.Text = text;
        }
    }
}
