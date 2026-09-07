using System;
using System.Windows;

namespace LocalRemoteDesktop
{
    public partial class ConnectDialog : Window
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string AccessCode { get; private set; }
        public bool AccessCodeEnabled { get; private set; }

        public ConnectDialog(string initialHost, int initialPort)
        {
            InitializeComponent();
            HostTextBox.Text = initialHost ?? string.Empty;
            PortTextBox.Text = initialPort.ToString();
            UseAccessCodeCheckBox.Checked += OnUseAccessCodeChanged;
            UseAccessCodeCheckBox.Unchecked += OnUseAccessCodeChanged;
            OnUseAccessCodeChanged(null, null);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(HostTextBox.Text))
                HostTextBox.Focus();
            else if (IsAccessCodeEnabled())
                AccessCodeBox.Focus();
        }

        private void OnUseAccessCodeChanged(object sender, RoutedEventArgs e)
        {
            AccessCodeBox.IsEnabled = IsAccessCodeEnabled();
            if (!AccessCodeBox.IsEnabled)
                AccessCodeBox.Clear();
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

            var accessCodeEnabled = IsAccessCodeEnabled();
            if (accessCodeEnabled && string.IsNullOrWhiteSpace(AccessCodeBox.Text))
            {
                MessageBox.Show(this, "请输入远程电脑的访问码。", "连接",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                AccessCodeBox.Focus();
                return;
            }

            Host = host;
            Port = port;
            AccessCodeEnabled = accessCodeEnabled;
            AccessCode = accessCodeEnabled ? AccessCodeBox.Text.Trim() : null;
            DialogResult = true;
        }

        private bool IsAccessCodeEnabled()
        {
            return UseAccessCodeCheckBox.IsChecked == true;
        }
    }
}
