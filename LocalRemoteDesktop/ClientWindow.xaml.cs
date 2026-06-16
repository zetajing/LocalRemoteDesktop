using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using LocalRemoteDesktop.Models;
using LocalRemoteDesktop.Network;

namespace LocalRemoteDesktop
{
    public partial class ClientWindow : Window
    {
        private RemoteClient _client;
        private bool _isConnected;
        private volatile bool _isUpdatingImage;
        private bool _isFullscreen = true;
        private System.Windows.Forms.Screen _screen;
        private int _remoteWidth, _remoteHeight; // 远程桌面分辨率
        private bool _statsBarPinned; // F12 固定显示

        // 帧率统计
        private int _frameCount;
        private long _bytesReceived;
        private DateTime _statsResetTime = DateTime.UtcNow;
        private System.Windows.Threading.DispatcherTimer _statsTimer;
        private System.Windows.Threading.DispatcherTimer _hideStatsTimer;
        private System.Diagnostics.Stopwatch _latencyWatch = new System.Diagnostics.Stopwatch();
        private long _estimatedLatencyMs;
        private long _lastHeartbeatSentAt; // 上次心跳发送时 Stopwatch ticks

        public ClientWindow()
        {
            InitializeComponent();
            _screen = System.Windows.Forms.Screen.PrimaryScreen;
            GoFullscreen();
            BtnToggleFullscreen.Content = "⛶ 窗口化";

            // 帧率统计定时器（每 500ms 刷新）
            _statsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _statsTimer.Tick += UpdateStats;
            _statsTimer.Start();

            // 鼠标移入 StatsBar 区域时显示
            MouseMove += (s, e) =>
            {
                var pos = e.GetPosition(this);
                if (pos.Y < 40)
                    ShowStatsBar();
            };

            // 心跳定时器：每 2 秒发一次，测量往返延迟
            _latencyWatch.Start();
            var heartbeatTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            heartbeatTimer.Tick += (s, e) =>
            {
                if (_isConnected)
                {
                    _lastHeartbeatSentAt = _latencyWatch.ElapsedTicks;
                    // payload: 8 字节 Stopwatch ticks
                    var ts = BitConverter.GetBytes(_lastHeartbeatSentAt);
                    _client?.Send(new ProtocolFrame(FrameType.Heartbeat, ts));
                }
            };
            heartbeatTimer.Start();
        }

        #region 全屏 / 窗口化切换

        private void GoFullscreen()
        {
            _isFullscreen = true;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            Left = _screen.Bounds.X;
            Top = _screen.Bounds.Y;
            Width = _screen.Bounds.Width;
            Height = _screen.Bounds.Height;
            Topmost = true;
            StatsBar.Visibility = Visibility.Visible;
            BtnToggleFullscreen.Content = "⛶ 窗口化";
        }

        private void GoWindowed()
        {
            _isFullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            Topmost = false;
            Width = _screen.Bounds.Width * 0.75;
            Height = _screen.Bounds.Height * 0.75;
            Left = (_screen.Bounds.Width - Width) / 2;
            Top = (_screen.Bounds.Height - Height) / 2;
            StatsBar.Visibility = Visibility.Visible;
            BtnToggleFullscreen.Content = "⛶ 全屏";
        }

        private void ToggleFullscreen()
        {
            if (_isFullscreen)
                GoWindowed();
            else
                GoFullscreen();
        }

        #endregion

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Focus();
            ThreadPool.QueueUserWorkItem(_ => ConnectToServer());
        }

        private void ConnectToServer()
        {
            try
            {
                _client = new RemoteClient();
                _client.FrameReceived += OnFrameReceived;
                _client.Disconnected += OnDisconnected;

                var ip = App.TargetIp;
                var port = App.Port;

                if (_client.Connect(ip, port))
                {
                    _isConnected = true;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "已连接 — 远程桌面";
                        StatusText.Visibility = Visibility.Collapsed;
                    });

                    // 启动剪贴板同步（每 500ms 检测一次）
                    StartClipboardSync();
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"无法连接到 {ip}:{port}";
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Client] Connect error: {ex.Message}");
            }
        }

        private void OnFrameReceived(ProtocolFrame frame)
        {
            // 统计（原子操作，所有帧类型）
            _frameCount++;
            _bytesReceived += frame.Payload?.Length ?? 0;

            switch (frame.Type)
            {
                case FrameType.ScreenInfo:
                    HandleScreenInfo(frame);
                    break;

                case FrameType.TileImage:
                    HandleTileImage(frame);
                    break;

                case FrameType.TileEnd:
                    HandleTileEnd();
                    break;

                case FrameType.FileAccept:
                    Dispatcher.BeginInvoke(new Action(() =>
                        FileStatusText.Text = "服务端已接受，开始传输..."));
                    break;

                case FrameType.FileReject:
                    Dispatcher.BeginInvoke(new Action(() =>
                        FileStatusText.Text = "文件传输被拒绝"));
                    break;

                case FrameType.Heartbeat:
                    if (frame.Payload.Length >= 8)
                    {
                        long sentTicks = BitConverter.ToInt64(frame.Payload, 0);
                        long rttTicks = _latencyWatch.ElapsedTicks - sentTicks;
                        _estimatedLatencyMs = rttTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
                    }
                    break;

                case FrameType.ClipboardText:
                    ReceiveClipboard(frame);
                    break;
            }
        }

        private void DisplayImage(ProtocolFrame frame)
        {
            if (_isUpdatingImage) return;
            _isUpdatingImage = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    using (var ms = new MemoryStream(frame.Payload))
                    {
                        var decoder = new System.Windows.Media.Imaging.JpegBitmapDecoder(
                            ms,
                            System.Windows.Media.Imaging.BitmapCreateOptions.None,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        var src = decoder.Frames[0];

                        // 直接设为 Image.Source（比 WriteableBitmap 拼贴更简单高效）
                        src.Freeze();
                        RemoteImage.Source = src;
                        _remoteWidth = src.PixelWidth;
                        _remoteHeight = src.PixelHeight;
                    }
                }
                catch { }
                finally
                {
                    _isUpdatingImage = false;
                }
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }

        private void OnDisconnected()
        {
            _isConnected = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = "连接已断开";
                StatusText.Visibility = Visibility.Visible;
            }));
        }

        #region 全帧 JPEG 画面（DXGI + 全帧编码）

        private void HandleScreenInfo(ProtocolFrame frame)
        {
            if (frame.Payload.Length < 4) return;
            int w = BitConverter.ToUInt16(frame.Payload, 0);
            int h = BitConverter.ToUInt16(frame.Payload, 2);
            if (w <= 0 || h <= 0) return;

            _remoteWidth = w;
            _remoteHeight = h;
        }

        private void HandleTileImage(ProtocolFrame frame)
        {
            // 全帧 JPEG 数据：直接显示
            DisplayImage(frame);
        }

        private void HandleTileEnd()
        {
            // 全帧模式下无需额外操作
        }

        #endregion

        #region 统计栏

        private void ShowStatsBar()
        {
            if (_statsBarPinned) return;
            StatsBar.Opacity = 1;
            _hideStatsTimer?.Stop();
            _hideStatsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _hideStatsTimer.Tick += (s, e) =>
            {
                if (!_statsBarPinned)
                    StatsBar.Opacity = 0;
                _hideStatsTimer.Stop();
            };
            _hideStatsTimer.Start();
        }

        private void UpdateStats(object sender, EventArgs e)
        {
            var elapsed = (DateTime.UtcNow - _statsResetTime).TotalSeconds;
            if (elapsed <= 0) return;

            int fps = (int)(_frameCount / elapsed);
            double kbps = _bytesReceived / elapsed / 1024.0;

            StatsFps.Text = $"FPS: {fps}";
            StatsBandwidth.Text = $"带宽: {kbps:F0} KB/s";
            StatsLatency.Text = $"延迟: ~{_estimatedLatencyMs}ms";
            StatsResolution.Text = $"{_remoteWidth}x{_remoteHeight}";

            // 每秒重置
            if (elapsed >= 1.0)
            {
                _frameCount = 0;
                _bytesReceived = 0;
                _statsResetTime = DateTime.UtcNow;
            }
        }

        #endregion

        #region 剪贴板同步

        private string _lastClipboardText;
        private System.Windows.Threading.DispatcherTimer _clipboardTimer;

        private void StartClipboardSync()
        {
            _clipboardTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _clipboardTimer.Tick += PollClipboard;
            _clipboardTimer.Start();
        }

        private void PollClipboard(object sender, EventArgs e)
        {
            if (!_isConnected) return;
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                    {
                        _lastClipboardText = text;
                        var payload = System.Text.Encoding.UTF8.GetBytes(text);
                        _client?.Send(new ProtocolFrame(FrameType.ClipboardText, payload));
                    }
                }
            }
            catch { }
        }

        private void ReceiveClipboard(ProtocolFrame frame)
        {
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(frame.Payload);
                if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                {
                    _lastClipboardText = text;
                    System.Windows.Clipboard.SetText(text);
                }
            }
            catch { }
        }

        private void StopClipboardSync()
        {
            _clipboardTimer?.Stop();
            _clipboardTimer = null;
        }

        #endregion

        #region 文件传输

        private void OnSendFile(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("未连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 支持选择文件和文件夹
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要发送的文件",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                    SendFile(file);
            }
        }

        private void SendFile(string filePath)
        {
            SendFileInternal(filePath, Path.GetFileName(filePath));
        }

        /// <summary>发送文件/文件夹内文件（relativePath 用于服务端创建子目录）</summary>
        private void SendFileInternal(string filePath, string relativePath)
        {
            var fileInfo = new FileInfo(filePath);

            // payload: [8字节大小][1字节标志：0=独立, 1=文件夹内][UTF8 相对路径]
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relativePath);
            var requestPayload = new byte[9 + pathBytes.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(fileInfo.Length), 0, requestPayload, 0, 8);
            requestPayload[8] = 0; // 默认独立文件标志
            Buffer.BlockCopy(pathBytes, 0, requestPayload, 9, pathBytes.Length);
            _client?.Send(new ProtocolFrame(FrameType.FileRequest, requestPayload));

            FileStatusText.Text = $"发送: {relativePath} ({FormatSize(fileInfo.Length)})";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    const int chunkSize = 256 * 1024;
                    long totalSent = 0;

                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        byte[] buffer = new byte[chunkSize];
                        int seq = 0;

                        while (true)
                        {
                            int read = fs.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;

                            var payload = new byte[4 + read];
                            Buffer.BlockCopy(BitConverter.GetBytes(seq++), 0, payload, 0, 4);
                            Buffer.BlockCopy(buffer, 0, payload, 4, read);

                            _client?.Send(new ProtocolFrame(FrameType.FileData, payload));
                            totalSent += read;

                            var progress = (int)(totalSent * 100 / fileInfo.Length);
                            Dispatcher.BeginInvoke(new Action(() =>
                                FileStatusText.Text = $"发送: {relativePath} {progress}%"));
                        }
                    }

                    _client?.Send(new ProtocolFrame(FrameType.FileEnd, Array.Empty<byte>()));

                    Dispatcher.BeginInvoke(new Action(() =>
                        FileStatusText.Text = $"✅ 发送完成: {relativePath}"));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                        FileStatusText.Text = $"❌ 发送失败: {ex.Message}"));
                }
            });
        }

        private void SendFolder(string folderPath)
        {
            var folderName = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(folderName)) folderName = "files";

            var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
            foreach (var file in allFiles)
            {
                // 计算相对路径（含文件夹名）
                var relativePath = folderName + "\\" +
                    file.Substring(folderPath.Length).TrimStart('\\', '/');
                SendFileInternal(file, relativePath);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        #endregion

        #region 全屏切换按钮

        private void OnToggleFullscreen(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        #endregion

        #region 鼠标事件捕获

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isConnected || _remoteWidth == 0 || _remoteHeight == 0) return;

            // 获取鼠标相对于 RemoteImage 控件的位置
            var pos = e.GetPosition(RemoteImage);
            double imgW = RemoteImage.ActualWidth;
            double imgH = RemoteImage.ActualHeight;
            if (imgW <= 0 || imgH <= 0) return;

            // 计算 Stretch="Uniform" 下实际画面的矩形区域（处理 letterboxing）
            double scale = Math.Min(imgW / _remoteWidth, imgH / _remoteHeight);
            double drawW = _remoteWidth * scale;
            double drawH = _remoteHeight * scale;
            double offsetX = (imgW - drawW) / 2;
            double offsetY = (imgH - drawH) / 2;

            // 光标在画面内的相对位置
            double relX = (pos.X - offsetX) / drawW;
            double relY = (pos.Y - offsetY) / drawH;

            // 裁剪到 [0, 1]（光标可能在 letterbox 黑边区域外）
            relX = Math.Max(0, Math.Min(1, relX));
            relY = Math.Max(0, Math.Min(1, relY));

            var frame = ProtocolFrame.CreateMouseMove((float)relX, (float)relY);
            _client?.Send(frame);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_isConnected) return;

            FrameType type;
            switch (e.ChangedButton)
            {
                case MouseButton.Left: type = FrameType.MouseLeftDown; break;
                case MouseButton.Right: type = FrameType.MouseRightDown; break;
                default: return;
            }
            _client?.Send(new ProtocolFrame(type, Array.Empty<byte>()));
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_isConnected) return;

            FrameType type;
            switch (e.ChangedButton)
            {
                case MouseButton.Left: type = FrameType.MouseLeftUp; break;
                case MouseButton.Right: type = FrameType.MouseRightUp; break;
                default: return;
            }
            _client?.Send(new ProtocolFrame(type, Array.Empty<byte>()));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_isConnected) return;

            _client?.Send(ProtocolFrame.CreateMouseWheel(e.Delta));
        }

        #endregion

        #region 键盘事件

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OnSendFile(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.F12)
            {
                _statsBarPinned = !_statsBarPinned;
                StatsBar.Opacity = _statsBarPinned ? 1 : 0;
                e.Handled = true;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isConnected) return;
            if (e.Key == Key.Escape || e.Key == Key.F11) return;

            var vk = KeyInterop.VirtualKeyFromKey(e.Key);
            _client?.Send(ProtocolFrame.CreateKeyEvent(FrameType.KeyDown, vk));
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (!_isConnected) return;
            if (e.Key == Key.Escape || e.Key == Key.F11) return;

            var vk = KeyInterop.VirtualKeyFromKey(e.Key);
            _client?.Send(ProtocolFrame.CreateKeyEvent(FrameType.KeyUp, vk));
        }

        private void OnTextInput(object sender, TextCompositionEventArgs e) { }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!_isConnected) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    foreach (var path in files)
                    {
                        if (Directory.Exists(path))
                            SendFolder(path);
                        else if (File.Exists(path))
                            SendFile(path);
                    }
                }
            }
        }

        #endregion

        private void OnClosed(object sender, EventArgs e)
        {
            _client?.Dispose();
        }
    }
}
