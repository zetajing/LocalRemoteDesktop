using System;
using System.IO;
using System.Threading;
using LocalRemoteDesktop.Capture;
using LocalRemoteDesktop.Input;
using LocalRemoteDesktop.Models;
using LocalRemoteDesktop.Network;
using LocalRemoteDesktop.Utils;

namespace LocalRemoteDesktop
{
    /// <summary>
    /// 被控端后台运行器 — 监听连接、发送屏幕、接收输入
    /// </summary>
    public class ServerRunner : IDisposable
    {
        private RemoteServer _server;
        private ScreenCapture _capture;
        private Timer _sendTimer;
        private volatile bool _running;
        private volatile bool _isSending;
        private int _frameIntervalMs;
        private const int FrameIntervalMs = 20; // 最高 ~50 FPS
        private volatile bool _screenInfoSent;  // 首次连接时先发分辨信息
        private readonly object _disposeLock = new object();

        public void Start(int port, int jpegQuality = 70, int frameIntervalMs = 20)
        {
            Start(port, jpegQuality, frameIntervalMs, 0);
        }

        public void Start(int port, int jpegQuality, int frameIntervalMs, int monitorIndex)
        {
            _server = new RemoteServer();
            _capture = new ScreenCapture(jpegQuality, monitorIndex);
            _running = true;
            _frameIntervalMs = Math.Max(16, frameIntervalMs);
            _screenInfoSent = false;

            _server.FrameReceived += OnFrameReceived;
            _server.Start(port);

            // 首次立即触发，之后每帧完成后才安排下一帧（绝不重叠）
            _sendTimer = new Timer(SendScreenFrame, null, 0, Timeout.Infinite);

            // 启动剪贴板同步
            StartClipboardSync();
        }

        /// <summary>运行时切换捕获的显示器</summary>
        public void SwitchToMonitor(int monitorIndex)
        {
            _capture?.SetMonitor(monitorIndex);
            _screenInfoSent = false;
        }

        private void SendScreenFrame(object state)
        {
            try
            {
                if (!_running)
                    return;

                if (!_server.IsConnected)
                {
                    _screenInfoSent = false;
                    ScheduleNextFrame();
                    return;
                }

                if (_isSending) return;
                _isSending = true;

                try
                {
                    var tiles = _capture.CaptureChangedTiles();

                    // 首次或重连: 先发 ScreenInfo 告知分辨率
                    if (!_screenInfoSent)
                    {
                        var (w, h) = _capture.GetScreenSize();
                        var info = new byte[4];
                        Buffer.BlockCopy(BitConverter.GetBytes(w), 0, info, 0, 2);
                        Buffer.BlockCopy(BitConverter.GetBytes(h), 0, info, 2, 2);
                        _server.Send(new ProtocolFrame(FrameType.ScreenInfo, info));
                        _screenInfoSent = true;
                    }

                    // 逐个发送变化的 Tile
                    foreach (var tile in tiles)
                    {
                        var frame = ProtocolFrame.CreateTile(tile.X, tile.Y, tile.Width, tile.Height, tile.JpegData);
                        _server.Send(frame);
                    }

                    // 发结尾标记，告知客户端可以刷新画面
                    _server.Send(new ProtocolFrame(FrameType.TileEnd, Array.Empty<byte>()));
                }
                finally
                {
                    _isSending = false;
                    if (_running)
                        ScheduleNextFrame();
                }
            }
            catch
            {
                // 任何未预期异常也被吞掉，绝不崩溃
            }
        }

        /// <summary>线程安全地安排下一帧发送</summary>
        private void ScheduleNextFrame()
        {
            lock (_disposeLock)
            {
                _sendTimer?.Change(_frameIntervalMs, Timeout.Infinite);
            }
        }

        private void OnFrameReceived(ProtocolFrame frame)
        {
            try
            {
                switch (frame.Type)
                {
                    case FrameType.MouseMove:
                        if (frame.Payload.Length >= 8)
                        {
                            var x = BitConverter.ToSingle(frame.Payload, 0);
                            var y = BitConverter.ToSingle(frame.Payload, 4);
                            InputSimulator.MoveMouse(x, y);
                        }
                        break;

                    case FrameType.MouseLeftDown:
                        InputSimulator.MouseLeftDown();
                        break;

                    case FrameType.MouseLeftUp:
                        InputSimulator.MouseLeftUp();
                        break;

                    case FrameType.MouseRightDown:
                        InputSimulator.MouseRightDown();
                        break;

                    case FrameType.MouseRightUp:
                        InputSimulator.MouseRightUp();
                        break;

                    case FrameType.MouseWheel:
                        if (frame.Payload.Length >= 4)
                        {
                            var delta = BitConverter.ToInt32(frame.Payload, 0);
                            InputSimulator.MouseWheel(delta);
                        }
                        break;

                    case FrameType.KeyDown:
                        if (frame.Payload.Length >= 4)
                        {
                            var vk = BitConverter.ToInt32(frame.Payload, 0);
                            InputSimulator.KeyDown(vk);
                        }
                        break;

                    case FrameType.KeyUp:
                        if (frame.Payload.Length >= 4)
                        {
                            var vk = BitConverter.ToInt32(frame.Payload, 0);
                            InputSimulator.KeyUp(vk);
                        }
                        break;

                    // ---- 心跳回显 ----
                    case FrameType.Heartbeat:
                        _server?.Send(new ProtocolFrame(FrameType.Heartbeat, frame.Payload));
                        break;

                    // ---- 客户端分辨率 ----
                    case FrameType.ClientResolution:
                        if (frame.Payload.Length >= 4)
                        {
                            int cw = BitConverter.ToUInt16(frame.Payload, 0);
                            int ch = BitConverter.ToUInt16(frame.Payload, 2);
                            if (cw > 0 && ch > 0)
                            {
                                // 调整虚拟显示器分辨率匹配客户端
                                VirtualDisplayManager.SetResolution(cw, ch);
                            }
                        }
                        break;

                    // ---- 剪贴板同步 ----
                    case FrameType.ClipboardText:
                        SetServerClipboard(frame);
                        break;

                    // ---- 文件传输 ----
                    case FrameType.FileRequest:
                        HandleFileRequest(frame);
                        break;

                    case FrameType.FileData:
                        HandleFileData(frame);
                        break;

                    case FrameType.FileEnd:
                        HandleFileEnd();
                        break;
                }
            }
            catch
            {
                // 忽略异常
            }
        }

        #region 文件接收

        private string _receivingFileName;
        private FileStream _receivingFileStream;

        private long _receivingFileExpectedSize;
        private long _receivingFileBytesWritten;

        private void HandleFileRequest(ProtocolFrame frame)
        {
            if (frame.Payload.Length < 10) // 至少 8字节大小 + 1字节标志 + 1字节文件名
            {
                _server?.Send(new ProtocolFrame(FrameType.FileReject, Array.Empty<byte>()));
                return;
            }

            // 解析：前8字节为文件大小，第9字节为标志，后面为相对路径/文件名
            _receivingFileExpectedSize = BitConverter.ToInt64(frame.Payload, 0);
            _receivingFileBytesWritten = 0;
            bool isFolderFile = frame.Payload[8] != 0;
            var rawPath = System.Text.Encoding.UTF8.GetString(frame.Payload, 9, frame.Payload.Length - 9);

            // 安全处理：提取安全的相对路径
            var safePath = Path.GetFileName(rawPath);
            var rawDir = Path.GetDirectoryName(rawPath);
            if (!string.IsNullOrWhiteSpace(rawDir))
            {
                var dirParts = rawDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                var safeParts = new System.Collections.Generic.List<string>();
                foreach (var p in dirParts)
                {
                    if (string.IsNullOrWhiteSpace(p) || p.Contains("..")) continue;
                    safeParts.Add(p);
                }
                if (safeParts.Count > 0)
                    safePath = Path.Combine(string.Join("\\", safeParts), safePath);
            }

            if (string.IsNullOrWhiteSpace(safePath))
            {
                _server?.Send(new ProtocolFrame(FrameType.FileReject, Array.Empty<byte>()));
                return;
            }

            // 保存到桌面下的子目录
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            _receivingFileName = Path.Combine(desktop, safePath);

            // 如果是文件夹传输，确保目录存在
            var dir = Path.GetDirectoryName(_receivingFileName);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 避免重名
            int count = 1;
            var baseName = _receivingFileName;
            while (File.Exists(_receivingFileName))
            {
                var name = Path.GetFileNameWithoutExtension(baseName);
                var ext = Path.GetExtension(baseName);
                _receivingFileName = Path.Combine(desktop, $"{name} ({count++}){ext}");
            }

            try
            {
                // 关闭上一个可能未结束的文件流，防止泄漏
                _receivingFileStream?.Dispose();
                _receivingFileStream = new FileStream(_receivingFileName, FileMode.Create, FileAccess.Write);
                _server?.Send(new ProtocolFrame(FrameType.FileAccept, Array.Empty<byte>()));
                System.Diagnostics.Debug.WriteLine($"[Server] Receiving file: {_receivingFileName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Server] File reject: {ex.Message}");
                _server?.Send(new ProtocolFrame(FrameType.FileReject, Array.Empty<byte>()));
            }
        }

        private void HandleFileData(ProtocolFrame frame)
        {
            if (_receivingFileStream == null || frame.Payload.Length < 4) return;

            // 跳过4字节序号
            var dataLen = frame.Payload.Length - 4;
            _receivingFileStream?.Write(frame.Payload, 4, dataLen);
            _receivingFileBytesWritten += dataLen;
        }

        private void HandleFileEnd()
        {
            try
            {
                _receivingFileStream?.Flush();
                _receivingFileStream?.Dispose();
                _receivingFileStream = null;

                // 校验接收大小
                if (_receivingFileExpectedSize > 0 && _receivingFileBytesWritten != _receivingFileExpectedSize)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Server] File size mismatch: expected {_receivingFileExpectedSize}, got {_receivingFileBytesWritten}");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Server] File saved: {_receivingFileName} ({_receivingFileBytesWritten} bytes)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Server] File save error: {ex.Message}");
            }
        }

        #endregion

        #region 剪贴板同步

        private string _lastServerClipboard;
        private Timer _clipboardTimer;

        private void StartClipboardSync()
        {
            _clipboardTimer = new Timer(_ =>
            {
                if (!_running || !_server.IsConnected) return;
                try
                {
                    // 在 STA 线程上访问剪贴板
                    System.Threading.Thread staThread = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            if (System.Windows.Forms.Clipboard.ContainsText())
                            {
                                var text = System.Windows.Forms.Clipboard.GetText();
                                if (!string.IsNullOrEmpty(text) && text != _lastServerClipboard)
                                {
                                    _lastServerClipboard = text;
                                    var payload = System.Text.Encoding.UTF8.GetBytes(text);
                                    _server?.Send(new ProtocolFrame(FrameType.ClipboardText, payload));
                                }
                            }
                        }
                        catch { }
                    });
                    staThread.SetApartmentState(System.Threading.ApartmentState.STA);
                    staThread.Start();
                    staThread.Join(1000); // 最多等1秒
                }
                catch { }
            }, null, 1000, 500); // 1秒后开始，每500ms
        }

        private void SetServerClipboard(ProtocolFrame frame)
        {
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(frame.Payload);
                if (!string.IsNullOrEmpty(text) && text != _lastServerClipboard)
                {
                    _lastServerClipboard = text;
                    System.Threading.Thread staThread = new System.Threading.Thread(() =>
                    {
                        try { System.Windows.Forms.Clipboard.SetText(text); }
                        catch { }
                    });
                    staThread.SetApartmentState(System.Threading.ApartmentState.STA);
                    staThread.Start();
                    staThread.Join(1000);
                }
            }
            catch { }
        }

        #endregion

        public void Dispose()
        {
            lock (_disposeLock)
            {
                _running = false;
                var t = _sendTimer;
                _sendTimer = null;
                t?.Dispose();
                _server?.Dispose();
                _capture?.Dispose();

                // 清理可能残留的文件接收流
                _receivingFileStream?.Dispose();
                _receivingFileStream = null;
            }
        }
    }
}
