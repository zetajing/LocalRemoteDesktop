using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using LocalRemoteDesktop.Utils;

namespace LocalRemoteDesktop
{
    public partial class App : Application
    {
        public static string TargetIp { get; set; } = string.Empty;
        public static int Port { get; set; } = 8932;
        public static System.Drawing.Icon AppIcon { get; private set; }

        private ServerRunner _serverRunner;
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.ContextMenuStrip _trayMenu;

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
            _serverRunner = new ServerRunner();
            _serverRunner.Start(Port);

            // 生成动态图标
            GenerateIcon();

            // 创建托盘
            SetupTray();

            // 非自启模式才弹窗提示
            if (!autoStart)
            {
                _trayIcon.ShowBalloonTip(3000, "LocalRemoteDesktop",
                    $"已启动，端口 {Port}\n右键托盘图标连接远程电脑",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void SetupTray()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = AppIcon,
                Text = $"LocalRemoteDesktop (端口 {Port})",
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
                    item.Click += (s2, ev2) => ConnectTo(ip, port);
                    recentMenu.DropDownItems.Add(item);
                }
                _trayMenu.Items.Add(recentMenu);
            }

            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var localIp = GetLocalIp();
            if (!string.IsNullOrEmpty(localIp))
            {
                var ipItem = new System.Windows.Forms.ToolStripMenuItem($"🖥 本机: {localIp}:{Port}");
                ipItem.Enabled = false;
                _trayMenu.Items.Add(ipItem);
                _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            }

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

        private void PromptConnect()
        {
            Dispatcher.Invoke(() =>
            {
                var dialog = new InputDialog("连接远程电脑", "IP 地址:", TargetIp);
                dialog.Owner = null;
                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
                {
                    ConnectTo(dialog.Value.Trim(), Port);
                }
            });
        }

        private void ConnectTo(string ip, int port)
        {
            App.TargetIp = ip;
            App.Port = port;
            ConnectionHistory.Add(ip, port);

            Dispatcher.Invoke(() =>
            {
                var clientWindow = new ClientWindow();
                clientWindow.Show();
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
