using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace LocalRemoteDesktop
{
    public partial class InputDialog : Window
    {
        public string Value { get; private set; } = string.Empty;

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            InputBox.Text = defaultValue;
            InputBox.SelectAll();
            Loaded += (s, e) => InputBox.Focus();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Value = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(Value))
            {
                MessageBox.Show("请输入有效的 IP 地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnOk(sender, e);
            if (e.Key == Key.Escape) OnCancel(sender, e);
        }

        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"^[\d\.]+$");
        }
    }
}
