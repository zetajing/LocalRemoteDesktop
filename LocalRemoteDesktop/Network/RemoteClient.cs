using System;
using System.Net.Sockets;
using System.Threading;
using LocalRemoteDesktop.Models;
using LocalRemoteDesktop.Security;

namespace LocalRemoteDesktop.Network
{
    /// <summary>
    /// 控制端。只有访问码挑战响应完成后才进入已连接状态，所有业务帧均通过安全外层传输。
    /// </summary>
    public class RemoteClient : IDisposable
    {
        private readonly object _stateLock = new object();
        private readonly object _connectLock = new object();
        private readonly object _sendLock = new object();

        private TcpClient _client;
        private NetworkStream _stream;
        private SecureSession _session;
        private Thread _receiveThread;
        private bool _running;
        private bool _disposed;
        private int _disconnectSignaled;

        public event Action<ProtocolFrame> FrameReceived;
        public event Action Disconnected;

        /// <summary>TCP 建立但认证尚未完成时仍为 false。</summary>
        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                    return _running && _session != null;
            }
        }

        public bool Connect(string host, int port, string accessCode)
        {
            lock (_connectLock)
            {
                TcpClient client = null;
                NetworkStream stream = null;
                SecureSession session = null;
                var ownershipTransferred = false;

                try
                {
                    lock (_stateLock)
                    {
                        if (_disposed || _running || _session != null)
                            return false;
                    }

                    if (string.IsNullOrWhiteSpace(host))
                        throw new ArgumentException("远程主机不能为空。", nameof(host));
                    if (port < 1 || port > 65535)
                        throw new ArgumentOutOfRangeException(nameof(port));

                    client = new TcpClient
                    {
                        ReceiveTimeout = 10000,
                        SendTimeout = 5000,
                        NoDelay = true
                    };
                    client.Connect(host, port);
                    stream = client.GetStream();

                    // 握手在当前线程完成；认证成功前不启动业务接收，也不触发 FrameReceived。
                    session = SecureHandshake.AuthenticateClient(stream, accessCode);

                    var receiveThread = new Thread(
                        () => ReceiveLoop(client, stream, session))
                    {
                        IsBackground = true,
                        Name = "ClientSecureReceive"
                    };

                    lock (_stateLock)
                    {
                        if (_disposed)
                            throw new ObjectDisposedException(nameof(RemoteClient));

                        _client = client;
                        _stream = stream;
                        _session = session;
                        _receiveThread = receiveThread;
                        _running = true;
                        Interlocked.Exchange(ref _disconnectSignaled, 0);
                        ownershipTransferred = true;
                    }

                    try
                    {
                        receiveThread.Start();
                    }
                    catch
                    {
                        DisconnectConnection(client, session, false);
                        throw;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RemoteClient] Connect/authentication failed: {ex.Message}");

                    if (!ownershipTransferred)
                    {
                        session?.Dispose();
                        stream?.Close();
                        client?.Close();
                    }
                    return false;
                }
            }
        }

        private void ReceiveLoop(
            TcpClient client,
            NetworkStream stream,
            SecureSession session)
        {
            try
            {
                while (IsCurrentConnection(client, session))
                {
                    var secureFrame = ProtocolFrame.ReadFrom(stream);
                    if (secureFrame.Type != FrameType.SecureData)
                        throw new InvalidOperationException("认证后收到非加密协议帧。");

                    var businessFrame = session.Unprotect(secureFrame);
                    FrameReceived?.Invoke(businessFrame);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RemoteClient] Secure receive failed: {ex.Message}");
            }
            finally
            {
                DisconnectConnection(client, session, true);
            }
        }

        /// <summary>发送一帧数据（线程安全，始终经 SecureData 外层传输）。</summary>
        public void Send(ProtocolFrame frame)
        {
            TcpClient client;
            NetworkStream stream;
            SecureSession session;

            lock (_sendLock)
            {
                lock (_stateLock)
                {
                    if (!_running || _session == null)
                        return;

                    client = _client;
                    stream = _stream;
                    session = _session;
                }

                try
                {
                    var secureFrame = session.Protect(frame);
                    var data = secureFrame.Serialize();
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RemoteClient] Secure send failed: {ex.Message}");
                    DisconnectConnection(client, session, true);
                }
            }
        }

        public void Dispose()
        {
            TcpClient client;
            NetworkStream stream;
            SecureSession session;

            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _running = false;
                client = _client;
                stream = _stream;
                session = _session;
                _client = null;
                _stream = null;
                _session = null;
                _receiveThread = null;
            }

            stream?.Close();
            client?.Close();
            session?.Dispose();
        }

        private bool IsCurrentConnection(
            TcpClient client,
            SecureSession session)
        {
            lock (_stateLock)
            {
                return _running &&
                       ReferenceEquals(_client, client) &&
                       ReferenceEquals(_session, session);
            }
        }

        private void DisconnectConnection(
            TcpClient client,
            SecureSession session,
            bool notify)
        {
            NetworkStream stream = null;
            var wasCurrent = false;

            lock (_stateLock)
            {
                if (ReferenceEquals(_client, client) &&
                    ReferenceEquals(_session, session))
                {
                    wasCurrent = _running;
                    _running = false;
                    stream = _stream;
                    _client = null;
                    _stream = null;
                    _session = null;
                    _receiveThread = null;
                }
            }

            stream?.Close();
            client?.Close();
            session?.Dispose();

            if (notify && wasCurrent &&
                Interlocked.Exchange(ref _disconnectSignaled, 1) == 0)
            {
                Disconnected?.Invoke();
            }
        }
    }
}
