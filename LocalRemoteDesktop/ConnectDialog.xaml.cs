using System;
using System.Windows;

namespace LocalRemoteDesktop
{
    public partial class ConnectDialog : Window
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string AccessCode { get; private set; }

        public ConnectDialog(string initialHost, int initialPort)
        {
            InitializeComponent();
            HostTextBox.Text = initialHost ?? string.Empty;
            PortTextBox.Text = initialPort.ToString();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(HostTextBox.Text))
                HostTextBox.Focus();
            else
                AccessCodeBox.Focus();
        }

        private void OnConnect(object sender, RoutedEventArgs e)
        {
            var host = HostTextBox.Text.Trim();
            int port;

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show(this, "请输入远程电脑的主机名或 IP 地址。", "连接",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                HostTextBox.Focus();
                return;
            }

            if (!int.TryParse(PortTextBox.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "端口必须是 1 到 65535 之间的整数。", "连接",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                PortTextBox.Focus();
                PortTextBox.SelectAll();
                return;
            }

            if (string.IsNullOrWhiteSpace(AccessCodeBox.Password))
            {
                MessageBox.Show(this, "请输入远程电脑的访问码。", "连接",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                AccessCodeBox.Focus();
                return;
            }

            Host = host;
            Port = port;
            AccessCode = AccessCodeBox.Password;
            DialogResult = true;
        }
    }
}
