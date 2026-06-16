using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LocalRemoteDesktop.Models;

namespace LocalRemoteDesktop.Network
{
    /// <summary>
    /// 被控端 — 监听连接，发送屏幕帧，接收输入事件
    /// </summary>
    public class RemoteServer : IDisposable
    {
        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _acceptThread;
        private Thread _receiveThread;
        private readonly object _sendLock = new object();
        private volatile bool _running;
        // 需大于最大入站帧：文件传输 256KB 数据块 + 9 字节开销 ≈ 257KB
        private readonly byte[] _recvBuffer = new byte[1024 * 300]; // 300KB
        private int _recvOffset;

        public event Action<ProtocolFrame> FrameReceived;

        public bool IsConnected => _client?.Connected ?? false;

        public void Start(int port)
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "ServerAccept"
            };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    // 同步等待连接（可被 _listener.Stop() 中断抛出异常）
                    var client = _listener.AcceptTcpClient();
                    if (!_running)
                    {
                        client.Close();
                        break;
                    }

                    // 替换旧连接
                    var old = Interlocked.Exchange(ref _client, client);
                    old?.Close();

                    _client.ReceiveTimeout = 10000;
                    _client.SendTimeout = 5000;
                    _client.NoDelay = true; // 禁用 Nagle 算法
                    _stream = _client.GetStream();
                    _recvOffset = 0;

                    // 启动接收线程
                    _receiveThread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "ServerReceive"
                    };
                    _receiveThread.Start();

                    System.Diagnostics.Debug.WriteLine("[RemoteServer] Client connected");
                }
                catch (ObjectDisposedException)
                {
                    // _listener 已释放，正常退出
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RemoteServer] Accept error: {ex.Message}");
                }
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
                        System.Diagnostics.Debug.WriteLine("[RemoteServer] Receive buffer full, disconnecting");
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
                System.Diagnostics.Debug.WriteLine($"[RemoteServer] Receive error: {ex.Message}");
            }
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
                System.Diagnostics.Debug.WriteLine($"[RemoteServer] Send error: {ex.Message}");
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

            _listener?.Stop();
        }
    }
}
