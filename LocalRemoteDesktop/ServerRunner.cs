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
    ///
    /// v2 改进：
    /// - DXGI 截屏（延迟 ~8ms vs 旧版 GDI ~38ms）
    /// - 全帧 JPEG 传输（去掉瓦片系统）
    /// - 自适应帧率（DXGI 自带 vsync 同步）
    /// </summary>
    public class ServerRunner : IDisposable
    {
        private RemoteServer _server;
        private ScreenCapture _capture;
        private Timer _sendTimer;
        private int _clipboardPollInProgress;
        private volatile bool _running;
        private volatile bool _isSending;
        private bool _screenInfoSent; // 是否已发送分辨率信息，分辨率变化后重置
        private readonly object _disposeLock = new object();
        private bool _disposed;

        public void Start(int port, string accessCode, int jpegQuality = 80, int monitorIndex = 0)
        {
            Start(port, accessCode, true, jpegQuality, monitorIndex);
        }

        public void Start(
            int port,
            string accessCode,
            bool accessCodeEnabled,
            int jpegQuality = 80,
            int monitorIndex = 0)
        {
            _server = new RemoteServer();
            _capture = new ScreenCapture(jpegQuality, monitorIndex);
            _running = true;
            _screenInfoSent = false;

            _server.FrameReceived += OnFrameReceived;
            _server.Start(port, accessCode, accessCodeEnabled);

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
                    AbortActiveFileTransfer(false, null);
                    _screenInfoSent = false;
                    ScheduleNextFrame();
                    return;
                }

                if (_isSending) return;
                _isSending = true;

                try
                {
                    var tiles = _capture.CaptureChangedTiles();

                    if (tiles.Count == 0)
                    {
                        // 无变化，继续下一帧（DXGI 已同步 vsync）
                        return;
                    }

                    // 分辨率是否变了？发送 ScreenInfo
                    if (_capture.SizeChanged || !_screenInfoSent)
                    {
                        var (w, h) = _capture.GetScreenSize();
                        var info = new byte[4];
                        Buffer.BlockCopy(BitConverter.GetBytes(w), 0, info, 0, 2);
                        Buffer.BlockCopy(BitConverter.GetBytes(h), 0, info, 2, 2);
                        _screenInfoSent = true;
                        _capture.ResetSizeChanged();
                        _server.Send(new ProtocolFrame(FrameType.ScreenInfo, info));
                    }

                    // 发送全帧 JPEG
                    foreach (var tile in tiles)
                    {
                        _server.Send(new ProtocolFrame(FrameType.TileImage, tile.JpegData));
                    }

                    // 帧结束标记
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
                // 16ms ≈ 60fps；DXGI 实际帧率由 vsync 决定
                _sendTimer?.Change(16, Timeout.Infinite);
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

        private readonly object _fileReceiveLock = new object();
        private string _receivingRelativePath;
        private string _receivingDestinationPath;
        private string _receivingTempPath;
        private FileStream _receivingFileStream;
        private long _receivingFileExpectedSize;
        private long _receivingFileBytesWritten;
        private int _receivingNextSequence;
        private bool _fileTerminalResultSent;

        private void HandleFileRequest(ProtocolFrame frame)
        {
            lock (_fileReceiveLock)
            {
                if (_receivingFileStream != null)
                {
                    RejectFileRequest("服务端正在接收另一个文件");
                    return;
                }

                _fileTerminalResultSent = false;
                if (frame.Payload.Length < 10)
                {
                    RejectFileRequest("文件请求格式无效");
                    return;
                }

                var expectedSize = BitConverter.ToInt64(frame.Payload, 0);
                if (expectedSize < 0 || frame.Payload[8] > 1)
                {
                    RejectFileRequest("文件大小或类型无效");
                    return;
                }

                string rawPath;
                try
                {
                    rawPath = new System.Text.UTF8Encoding(false, true).GetString(
                        frame.Payload, 9, frame.Payload.Length - 9);
                }
                catch (System.Text.DecoderFallbackException)
                {
                    RejectFileRequest("文件路径编码无效");
                    return;
                }

                string relativePath;
                string destinationPath;
                string error;
                if (!TryResolveDesktopPath(rawPath, out relativePath, out destinationPath, out error))
                {
                    RejectFileRequest(error);
                    return;
                }

                try
                {
                    var destinationDirectory = Path.GetDirectoryName(destinationPath);
                    Directory.CreateDirectory(destinationDirectory);

                    var tempPath = Path.Combine(destinationDirectory,
                        ".lrd-" + Guid.NewGuid().ToString("N") + ".tmp");
                    var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 256 * 1024, FileOptions.SequentialScan);

                    _receivingRelativePath = relativePath;
                    _receivingDestinationPath = destinationPath;
                    _receivingTempPath = tempPath;
                    _receivingFileStream = stream;
                    _receivingFileExpectedSize = expectedSize;
                    _receivingFileBytesWritten = 0;
                    _receivingNextSequence = 0;

                    _server?.Send(new ProtocolFrame(FrameType.FileAccept, Array.Empty<byte>()));
                    System.Diagnostics.Debug.WriteLine(
                        $"[Server] Receiving file: {_receivingRelativePath}");
                }
                catch (Exception ex)
                {
                    CleanupReceivingFileUnsafe();
                    System.Diagnostics.Debug.WriteLine($"[Server] File reject: {ex.Message}");
                    RejectFileRequest("无法创建接收文件");
                }
            }
        }

        private void HandleFileData(ProtocolFrame frame)
        {
            lock (_fileReceiveLock)
            {
                if (_receivingFileStream == null)
                    return;
                if (frame.Payload.Length < 4)
                {
                    FailActiveFileTransfer("文件数据块格式无效");
                    return;
                }

                var sequence = BitConverter.ToInt32(frame.Payload, 0);
                if (sequence != _receivingNextSequence)
                {
                    FailActiveFileTransfer(
                        $"文件数据块序号错误，应为 {_receivingNextSequence}，实际为 {sequence}");
                    return;
                }

                var dataLength = frame.Payload.Length - 4;
                if (_receivingFileBytesWritten > _receivingFileExpectedSize - dataLength)
                {
                    FailActiveFileTransfer("接收数据超过声明的文件大小");
                    return;
                }

                try
                {
                    _receivingFileStream.Write(frame.Payload, 4, dataLength);
                    _receivingFileBytesWritten += dataLength;
                    _receivingNextSequence++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Server] File write error: {ex.Message}");
                    FailActiveFileTransfer("写入接收文件失败");
                }
            }
        }

        private void HandleFileEnd()
        {
            lock (_fileReceiveLock)
            {
                if (_receivingFileStream == null)
                {
                    if (!_fileTerminalResultSent)
                    {
                        _fileTerminalResultSent = true;
                        SendTransferResult(false, "没有正在接收的文件");
                    }
                    return;
                }

                if (_receivingFileBytesWritten != _receivingFileExpectedSize)
                {
                    var message =
                        $"文件大小不一致，应为 {_receivingFileExpectedSize} 字节，实际为 {_receivingFileBytesWritten} 字节";
                    System.Diagnostics.Debug.WriteLine($"[Server] {message}");
                    FailActiveFileTransfer(message);
                    return;
                }

                try
                {
                    _receivingFileStream.Flush(true);
                    _receivingFileStream.Dispose();
                    _receivingFileStream = null;

                    var finalPath = MoveTempFileAtomically(
                        _receivingTempPath, _receivingDestinationPath);
                    var savedName = Path.GetFileName(finalPath);
                    var savedBytes = _receivingFileBytesWritten;

                    ClearReceivingStateUnsafe();
                    _fileTerminalResultSent = true;
                    SendTransferResult(true, $"发送完成，已保存到桌面: {savedName}");
                    System.Diagnostics.Debug.WriteLine(
                        $"[Server] File saved: {savedName} ({savedBytes} bytes)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Server] File save error: {ex.Message}");
                    FailActiveFileTransfer("保存接收文件失败");
                }
            }
        }

        private static bool TryResolveDesktopPath(string rawPath, out string relativePath,
            out string destinationPath, out string error)
        {
            relativePath = null;
            destinationPath = null;
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(rawPath) || Path.IsPathRooted(rawPath))
                {
                    error = "文件路径必须是安全的相对路径";
                    return false;
                }

                var parts = rawPath.Replace('/', '\\').Split(
                    new[] { '\\' }, StringSplitOptions.None);
                if (parts.Length == 0)
                {
                    error = "文件路径为空";
                    return false;
                }

                var invalidChars = Path.GetInvalidFileNameChars();
                foreach (var part in parts)
                {
                    if (string.IsNullOrWhiteSpace(part) || part == "." || part == ".." ||
                        part.IndexOfAny(invalidChars) >= 0 ||
                        part.EndsWith(" ", StringComparison.Ordinal) ||
                        part.EndsWith(".", StringComparison.Ordinal) ||
                        IsReservedWindowsName(part))
                    {
                        error = "文件路径包含不安全的目录或文件名";
                        return false;
                    }
                }

                var desktop = Path.GetFullPath(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                var desktopPrefix = desktop.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), parts);
                destinationPath = Path.GetFullPath(Path.Combine(desktop, relativePath));

                if (!destinationPath.StartsWith(desktopPrefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = null;
                    destinationPath = null;
                    error = "文件路径超出桌面目录";
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                                       ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                relativePath = null;
                destinationPath = null;
                error = "文件路径无效或过长";
                return false;
            }
        }

        private static bool IsReservedWindowsName(string pathPart)
        {
            var name = Path.GetFileNameWithoutExtension(pathPart);
            if (string.IsNullOrEmpty(name))
                return false;

            switch (name.ToUpperInvariant())
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "COM1":
                case "COM2":
                case "COM3":
                case "COM4":
                case "COM5":
                case "COM6":
                case "COM7":
                case "COM8":
                case "COM9":
                case "LPT1":
                case "LPT2":
                case "LPT3":
                case "LPT4":
                case "LPT5":
                case "LPT6":
                case "LPT7":
                case "LPT8":
                case "LPT9":
                    return true;
                default:
                    return false;
            }
        }

        private static string MoveTempFileAtomically(string tempPath, string desiredPath)
        {
            var directory = Path.GetDirectoryName(desiredPath);
            var name = Path.GetFileNameWithoutExtension(desiredPath);
            var extension = Path.GetExtension(desiredPath);

            for (var suffix = 0; suffix < 10000; suffix++)
            {
                var candidate = suffix == 0
                    ? desiredPath
                    : Path.Combine(directory, $"{name} ({suffix}){extension}");
                try
                {
                    File.Move(tempPath, candidate);
                    return candidate;
                }
                catch (IOException)
                {
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                        continue;
                    throw;
                }
            }

            throw new IOException("无法为接收文件分配不重复的名称");
        }

        private void RejectFileRequest(string message)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(message ?? "文件传输被拒绝");
            _server?.Send(new ProtocolFrame(FrameType.FileReject, payload));
        }

        private void FailActiveFileTransfer(string message)
        {
            CleanupReceivingFileUnsafe();
            _fileTerminalResultSent = true;
            SendTransferResult(false, message);
        }

        private void AbortActiveFileTransfer(bool sendResult, string message)
        {
            lock (_fileReceiveLock)
            {
                if (_receivingFileStream == null && string.IsNullOrEmpty(_receivingTempPath))
                    return;

                CleanupReceivingFileUnsafe();
                if (sendResult)
                {
                    _fileTerminalResultSent = true;
                    SendTransferResult(false, message ?? "文件传输已中止");
                }
            }
        }

        private void CleanupReceivingFileUnsafe()
        {
            try
            {
                _receivingFileStream?.Dispose();
            }
            catch
            {
            }
            _receivingFileStream = null;

            if (!string.IsNullOrEmpty(_receivingTempPath))
            {
                try
                {
                    if (File.Exists(_receivingTempPath))
                        File.Delete(_receivingTempPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Server] Temporary file cleanup failed: {ex.Message}");
                }
            }

            ClearReceivingStateUnsafe();
        }

        private void ClearReceivingStateUnsafe()
        {
            _receivingRelativePath = null;
            _receivingDestinationPath = null;
            _receivingTempPath = null;
            _receivingFileStream = null;
            _receivingFileExpectedSize = 0;
            _receivingFileBytesWritten = 0;
            _receivingNextSequence = 0;
        }

        private void SendTransferResult(bool succeeded, string message)
        {
            var messageBytes = System.Text.Encoding.UTF8.GetBytes(message ?? string.Empty);
            var payload = new byte[1 + messageBytes.Length];
            payload[0] = succeeded ? (byte)1 : (byte)0;
            Buffer.BlockCopy(messageBytes, 0, payload, 1, messageBytes.Length);
            _server?.Send(new ProtocolFrame(FrameType.FileTransferResult, payload));
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
                if (Interlocked.Exchange(ref _clipboardPollInProgress, 1) != 0)
                    return;

                try
                {
                    // 在 STA 线程上访问剪贴板
                    System.Threading.Thread staThread = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            string text;
                            if (ClipboardHelper.TryGetText(out text))
                            {
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
                finally
                {
                    Interlocked.Exchange(ref _clipboardPollInProgress, 0);
                }
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
                        try { ClipboardHelper.TrySetText(text); }
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
                if (_disposed)
                    return;
                _disposed = true;
                _running = false;

                var t = _sendTimer;
                _sendTimer = null;
                t?.Dispose();

                var clipboardTimer = _clipboardTimer;
                _clipboardTimer = null;
                clipboardTimer?.Dispose();

                AbortActiveFileTransfer(false, null);
                if (_server != null)
                    _server.FrameReceived -= OnFrameReceived;
                _server?.Dispose();
                _capture?.Dispose();
            }
        }
    }
}
