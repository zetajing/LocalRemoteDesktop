using System;
using System.Net.Sockets;
using System.Threading;
using LocalRemoteDesktop.Models;

namespace LocalRemoteDesktop.Network
{
    /// <summary>
    /// 控制端 — 连接被控端，接收屏幕帧，发送输入事件
    /// </summary>
    public class RemoteClient : IDisposable
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _receiveThread;
        private volatile bool _running;
        private readonly byte[] _recvBuffer = new byte[1024 * 512]; // 512KB 接收缓冲区
        private int _recvOffset;
        private readonly object _sendLock = new object();

        public event Action<ProtocolFrame> FrameReceived;
        public event Action Disconnected;

        public bool IsConnected => _client?.Connected ?? false;

        public bool Connect(string ip, int port)
        {
            try
            {
                _client = new TcpClient();
                _client.ReceiveTimeout = 10000;
                _client.SendTimeout = 5000;
                _client.Connect(ip, port);
                _client.NoDelay = true; // 禁用 Nagle 算法，减少小包延迟
                _stream = _client.GetStream();
                _running = true;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "ClientReceive"
                };
                _receiveThread.Start();

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteClient] Connect failed: {ex.Message}");
                return false;
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (_running && _client?.Connected == true)
                {
                    var available = _recvBuffer.Length - _recvOffset;
                    if (available <= 0)
                    {
                        // 缓冲区满了且无法解析出完整帧 — 可能协议出错，断开连接
                        System.Diagnostics.Debug.WriteLine("[RemoteClient] Buffer full, disconnecting");
                        break;
                    }

                    var read = _stream.Read(_recvBuffer, _recvOffset, available);
                    if (read <= 0) break;

                    _recvOffset += read;

                    int consumed = 0;
                    while (true)
                    {
                        var frame = ProtocolFrame.Deserialize(_recvBuffer, consumed, _recvOffset - consumed);
                        if (frame == null) break;

                        consumed += 5 + frame.Payload.Length;
                        FrameReceived?.Invoke(frame);
                    }

                    if (consumed > 0)
                    {
                        var remaining = _recvOffset - consumed;
                        if (remaining > 0)
                            Buffer.BlockCopy(_recvBuffer, consumed, _recvBuffer, 0, remaining);
                        _recvOffset = remaining;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteClient] Receive error: {ex.Message}");
            }

            _running = false;
            Disconnected?.Invoke();
        }

        /// <summary>发送一帧数据（线程安全）</summary>
        public void Send(ProtocolFrame frame)
        {
            if (!IsConnected) return;

            try
            {
                var data = frame.Serialize();
                lock (_sendLock)
                {
                    _stream?.Write(data, 0, data.Length);
                    _stream?.Flush();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoteClient] Send error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _running = false;
            var s = _stream;
            _stream = null;
            s?.Close();

            var c = _client;
            _client = null;
            c?.Close();
        }
    }
}
