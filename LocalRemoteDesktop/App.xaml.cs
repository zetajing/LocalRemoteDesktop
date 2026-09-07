using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using LocalRemoteDesktop.Security;
using LocalRemoteDesktop.Utils;

namespace LocalRemoteDesktop
{
    public partial class App : Application
    {
        private const int DefaultPort = 8932;
        public static System.Drawing.Icon AppIcon { get; private set; }

        private ServerRunner _serverRunner;
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.ContextMenuStrip _trayMenu;
        private string _accessCode;
        private bool _accessCodeEnabled;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var args = e.Args ?? new string[0];
            bool autoStart = args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);

            // 全局未处理异常捕获
            DispatcherUnhandledException += (s, args2) =>
            {
                MessageBox.Show($"发生未处理错误:\n{args2.Exception.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                args2.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args2) =>
            {
                var ex = args2.ExceptionObject as Exception;
                MessageBox.Show($"后台线程发生未处理异常:\n{ex?.Message}\n\n进程将退出。",
                    "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // 自动启动被控端（后台监听）
            _accessCodeEnabled = AccessCodeStore.IsEnabled();
            _accessCode = AccessCodeStore.GetOrCreate();
            _serverRunner = new ServerRunner();
            _serverRunner.Start(DefaultPort, _accessCode, _accessCodeEnabled);

            // 生成动态图标
            GenerateIcon();

            // 创建托盘
            SetupTray();

            // 非自启模式才弹窗提示
            if (!autoStart)
            {
                _trayIcon.ShowBalloonTip(3000, "LocalRemoteDesktop",
                    $"已启动，端口 {DefaultPort}\n右键托盘图标连接远程电脑",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void SetupTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = AppIcon,
                Text = $"LocalRemoteDesktop (端口 {DefaultPort})",
                Visible = true
            };

            _trayIcon.MouseClick += (s, ev) =>
            {
                if (ev.Button == System.Windows.Forms.MouseButtons.Left)
                    PromptConnect();
            };

            _trayMenu = new System.Windows.Forms.ContextMenuStrip();
            BuildMenu();
            _trayIcon.ContextMenuStrip = _trayMenu;
        }

        private void BuildMenu()
        {
            _trayMenu.Items.Clear();
            var visibleAccessCode = _accessCode ?? AccessCodeStore.GetOrCreate();
            _trayIcon.Text = _accessCodeEnabled
                ? $"LocalRemoteDesktop (访问码 {visibleAccessCode})"
                : "LocalRemoteDesktop (访问码未启用)";

            _trayMenu.Items.Add("🔗 连接远程电脑...", null, (s, ev) => PromptConnect());

            var recent = ConnectionHistory.Load();
            if (recent.Count > 0)
            {
                var recentMenu = new System.Windows.Forms.ToolStripMenuItem("📋 最近连接");
                foreach (var h in recent)
                {
                    var item = new System.Windows.Forms.ToolStripMenuItem($"{h.Ip}:{h.Port}");
                    var ip = h.Ip;
                    var port = h.Port;
                    item.Click += (s2, ev2) => PromptConnect(ip, port);
                    recentMenu.DropDownItems.Add(item);
                }
                _trayMenu.Items.Add(recentMenu);
            }

            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var localIp = GetLocalIp();
            if (!string.IsNullOrEmpty(localIp))
            {
                var ipItem = new System.Windows.Forms.ToolStripMenuItem($"🖥 本机: {localIp}:{DefaultPort}");
                ipItem.Enabled = false;
                _trayMenu.Items.Add(ipItem);
            }
            var accessCodeItem = new System.Windows.Forms.ToolStripMenuItem(
                _accessCodeEnabled
                    ? $"🔑 本机访问码: {visibleAccessCode}"
                    : "🔓 访问码：未启用");
            accessCodeItem.Enabled = false;
            _trayMenu.Items.Add(accessCodeItem);

            var copyAccessCodeItem = new System.Windows.Forms.ToolStripMenuItem("复制本机访问码");
            copyAccessCodeItem.Enabled = _accessCodeEnabled;
            copyAccessCodeItem.Click += (s, ev) => CopyAccessCode();
            _trayMenu.Items.Add(copyAccessCodeItem);
            _trayMenu.Items.Add("重新生成本机访问码...", null, (s, ev) => RegenerateAccessCode());

            var accessCodeEnabledItem = new System.Windows.Forms.ToolStripMenuItem(
                _accessCodeEnabled ? "✅ 启用访问码" : "☐ 启用访问码");
            accessCodeEnabledItem.Click += (s, ev) => ToggleAccessCode();
            _trayMenu.Items.Add(accessCodeEnabledItem);
            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            bool autoStartOn = IsAutoStartEnabled();
            var autoStartItem = new System.Windows.Forms.ToolStripMenuItem(
                autoStartOn ? "✅ 开机自启" : "☐ 开机自启");
            autoStartItem.Click += (s, ev) =>
            {
                ToggleAutoStart();
                BuildMenu();
                _trayIcon.ContextMenuStrip = _trayMenu;
            };
            _trayMenu.Items.Add(autoStartItem);

            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _trayMenu.Items.Add("退出", null, (s, ev) =>
            {
                _trayIcon.Visible = false;
                _serverRunner?.Dispose();
                Shutdown();
            });
        }

        private void PromptConnect(string initialHost = null, int? initialPort = null)
        {
            Dispatcher.Invoke(() =>
            {
                var dialog = new ConnectDialog(initialHost, initialPort ?? DefaultPort);
                if (dialog.ShowDialog() == true)
                {
                    ConnectTo(dialog.Host, dialog.Port, dialog.AccessCode,
                        dialog.AccessCodeEnabled);
                }
            });
        }

        private void ConnectTo(
            string host,
            int port,
            string accessCode,
            bool accessCodeEnabled)
        {
            ConnectionHistory.Add(host, port);

            Dispatcher.Invoke(() =>
            {
                var clientWindow = new ClientWindow(
                    host, port, accessCode, accessCodeEnabled);
                clientWindow.Show();
            });
        }

        private void CopyAccessCode()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (!_accessCodeEnabled)
                    {
                        MessageBox.Show("访问码功能当前未启用。", "复制访问码",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    var accessCode = _accessCode ?? AccessCodeStore.GetOrCreate();
                    if (!ClipboardHelper.TrySetText(accessCode))
                        throw new InvalidOperationException("剪贴板当前被其他程序占用，重试后仍无法打开。");

                    _trayIcon?.ShowBalloonTip(2000, "LocalRemoteDesktop",
                        "本机访问码已复制到剪贴板。",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"复制访问码失败:\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void RegenerateAccessCode()
        {
            Dispatcher.Invoke(() =>
            {
                var result = MessageBox.Show(
                    "重新生成后，当前远程连接会断开，旧访问码将立即失效。是否继续？",
                    "重新生成访问码",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;

                try
                {
                    _accessCode = AccessCodeStore.Regenerate();
                    _serverRunner?.Dispose();
                    _serverRunner = new ServerRunner();
                    _serverRunner.Start(DefaultPort, _accessCode, _accessCodeEnabled);
                    BuildMenu();
                    _trayIcon.ContextMenuStrip = _trayMenu;
                    _trayIcon?.ShowBalloonTip(2500, "LocalRemoteDesktop",
                        "已生成新的本机访问码。可从托盘菜单复制。",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"重新生成访问码失败:\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private void ToggleAccessCode()
        {
            Dispatcher.Invoke(() =>
            {
                var newEnabled = !_accessCodeEnabled;
                if (!newEnabled)
                {
                    var result = MessageBox.Show(
                        "关闭访问码后，任何能访问本机端口 8932 的设备都可以直接连接，是否继续？",
                        "关闭访问码",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                        return;
                }

                var oldEnabled = _accessCodeEnabled;
                try
                {
                    if (newEnabled)
                        _accessCode = _accessCode ?? AccessCodeStore.GetOrCreate();

                    _accessCodeEnabled = newEnabled;
                    AccessCodeStore.SetEnabled(newEnabled);
                    _serverRunner?.Dispose();
                    _serverRunner = new ServerRunner();
                    _serverRunner.Start(DefaultPort, _accessCode, _accessCodeEnabled);
                    BuildMenu();
                    _trayIcon.ContextMenuStrip = _trayMenu;
                    _trayIcon.ShowBalloonTip(2500, "LocalRemoteDesktop",
                        newEnabled ? "访问码已启用。" : "访问码已关闭，当前端口无需访问码即可连接。",
                        newEnabled
                            ? System.Windows.Forms.ToolTipIcon.Info
                            : System.Windows.Forms.ToolTipIcon.Warning);
                }
                catch (Exception ex)
                {
                    _accessCodeEnabled = oldEnabled;
                    try { AccessCodeStore.SetEnabled(oldEnabled); } catch { }
                    MessageBox.Show($"切换访问码功能失败:\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }

        private static string GetLocalIp()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                return host.AddressList
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    ?.ToString();
            }
            catch { return "127.0.0.1"; }
        }

        private static void GenerateIcon()
        {
            try
            {
                int size = 32;
                using (var bmp = new System.Drawing.Bitmap(size, size))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Transparent);

                    // 绿色渐变圆形背景
                    var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new System.Drawing.Point(0, 0), new System.Drawing.Point(size, size),
                        System.Drawing.Color.FromArgb(0x00, 0xC8, 0x53),  // 浅绿
                        System.Drawing.Color.FromArgb(0x1B, 0x5E, 0x20)); // 深绿
                    g.FillEllipse(brush, 2, 2, size - 4, size - 4);

                    // 白色 "MR" 文字
                    using (var font = new System.Drawing.Font("Arial", 14, System.Drawing.FontStyle.Bold))
                    using (var sf = new System.Drawing.StringFormat
                    {
                        Alignment = System.Drawing.StringAlignment.Center,
                        LineAlignment = System.Drawing.StringAlignment.Center
                    })
                    {
                        g.DrawString("MR", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(2, 2, size - 4, size - 4), sf);
                    }

                    // 生成图标
                    AppIcon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch
            {
                // 失败时用默认
                try
                {
                    AppIcon = System.Drawing.Icon.ExtractAssociatedIcon(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                }
                catch { }
            }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key?.GetValue("LocalRemoteDesktop") != null;
                }
            }
            catch { return false; }
        }

        private static void ToggleAutoStart()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (key.GetValue("LocalRemoteDesktop") != null)
                        key.DeleteValue("LocalRemoteDesktop");
                    else
                        key.SetValue("LocalRemoteDesktop",
                            $"\"{System.Reflection.Assembly.GetExecutingAssembly().Location}\" --autostart");
                }
            }
            catch { }
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            _serverRunner?.Dispose();
            _trayIcon?.Dispose();
        }
    }
}
