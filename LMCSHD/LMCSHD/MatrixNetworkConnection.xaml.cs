using System;
using System.Linq;
using System.Windows;

namespace LMCSHD
{
    public partial class MatrixNetworkConnection : Window
    {
        public MatrixNetworkConnection()
        {
            InitializeComponent();
            PortBox.Text = NetworkManager.ListenPort.ToString();

            NetworkManager.ReceiverIdentified += OnReceiverIdentified;
            NetworkManager.ReceiverDisconnected += OnReceiverDisconnected;

            RefreshFromState();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            NetworkManager.ReceiverIdentified -= OnReceiverIdentified;
            NetworkManager.ReceiverDisconnected -= OnReceiverDisconnected;
        }

        private void OnReceiverIdentified(int deviceNum)
        {
            Dispatcher.Invoke(RefreshFromState);
        }

        private void OnReceiverDisconnected(int deviceNum)
        {
            Dispatcher.Invoke(RefreshFromState);
        }

        private void RefreshFromState()
        {
            StatusText.Text = NetworkManager.IsListening
                ? "Status: listening on port " + NetworkManager.ListenPort
                : "Status: stopped";

            DevicesList.Items.Clear();
            foreach (var deviceNum in NetworkManager.Devices.Keys.OrderBy(k => k))
                DevicesList.Items.Add("Device " + deviceNum + " — connected");

            ConnectBtn.IsEnabled = !NetworkManager.IsListening;
            DisconnectBtn.IsEnabled = NetworkManager.IsListening;
        }

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            int port;
            if (!int.TryParse(PortBox.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Port must be a number between 1 and 65535.");
                return;
            }
            if (!NetworkManager.Connect(port))
                MessageBox.Show("Failed to start WebSocket server on port " + port +
                                ". Is the port already in use?");
            RefreshFromState();
        }

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            NetworkManager.Disconnect();
            RefreshFromState();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
