using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LocalRemoteDesktop.Models;
using LocalRemoteDesktop.Security;

namespace LocalRemoteDesktop.Network
{
    /// <summary>
    /// 被控端。未通过访问码挑战响应的 TCP 连接不会成为当前连接，也不会分发业务帧。
    /// </summary>
    public class RemoteServer : IDisposable
    {
        private sealed class AuthenticatedConnection : IDisposable
        {
            internal readonly TcpClient Client;
            internal readonly NetworkStream Stream;
            internal readonly SecureSession Session;

            internal AuthenticatedConnection(
                TcpClient client,
                NetworkStream stream,
                SecureSession session)
            {
                Client = client;
                Stream = stream;
                Session = session;
            }

            public void Dispose()
            {
                Stream.Close();
                Client.Close();
                Session.Dispose();
            }
        }

        private readonly object _stateLock = new object();
        private readonly object _sendLock = new object();

        private TcpListener _listener;
        private TcpClient _pendingClient;
        private AuthenticatedConnection _connection;
        private Thread _acceptThread;
        private byte[] _preSharedKey;
        private bool _running;
        private bool _disposed;

        public event Action<ProtocolFrame> FrameReceived;

        /// <summary>仅当挑战响应认证成功并建立安全会话后才为 true。</summary>
        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                    return _running && _connection != null;
            }
        }

        public void Start(int port, string accessCode)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            var preSharedKey = SecurityPrimitives.DerivePreSharedKey(accessCode);
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();

                lock (_stateLock)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(RemoteServer));
                    if (_running)
                        throw new InvalidOperationException("服务端已经启动。");

                    _preSharedKey = preSharedKey;
                    _listener = listener;
                    _running = true;
                }

                // 闭包必须捕获稳定的局部副本；下面会将 listener 置空以转移所有权。
                var listenerForThread = listener;
                var acceptThread = new Thread(() => AcceptLoop(listenerForThread))
                {
                    IsBackground = true,
                    Name = "ServerSecureAccept"
                };
                lock (_stateLock)
                    _acceptThread = acceptThread;

                acceptThread.Start();
                preSharedKey = null; // 所有权已转交给实例
                listener = null;
            }
            finally
            {
                listener?.Stop();
                SecurityPrimitives.Clear(preSharedKey);
            }
        }

        private void AcceptLoop(TcpListener listener)
        {
            while (IsRunning())
            {
                TcpClient pendingClient = null;
                NetworkStream pendingStream = null;
                SecureSession session = null;
                byte[] handshakeKey = null;
                var promoted = false;

                try
                {
                    pendingClient = listener.AcceptTcpClient();
                    if (!RegisterPendingClient(pendingClient, out handshakeKey))
                        break;

                    pendingClient.ReceiveTimeout = 10000;
                    pendingClient.SendTimeout = 5000;
                    pendingClient.NoDelay = true;
                    pendingStream = pendingClient.GetStream();

                    session = SecureHandshake.AuthenticateServer(
                        pendingStream,
                        handshakeKey);

                    var connection = new AuthenticatedConnection(
                        pendingClient,
                        pendingStream,
                        session);
                    AuthenticatedConnection previous;
                    if (!PromotePendingClient(
                        pendingClient,
                        connection,
                        out previous))
                    {
                        connection.Dispose();
                        session = null;
                        pendingStream = null;
                        pendingClient = null;
                        break;
                    }

                    promoted = true;
                    session = null;
                    pendingStream = null;
                    pendingClient = null;
                    previous?.Dispose();

                    var receiveThread = new Thread(() => ReceiveLoop(connection))
                    {
                        IsBackground = true,
                        Name = "ServerSecureReceive"
                    };
                    try
                    {
                        receiveThread.Start();
                    }
                    catch
                    {
                        RemoveConnection(connection);
                        throw;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        "[RemoteServer] Authenticated client connected");
                }
                catch (ObjectDisposedException)
                {
                    if (!IsRunning())
                        break;
                }
                catch (Exception ex)
                {
                    if (IsRunning())
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[RemoteServer] Accept/authentication failed: {ex.Message}");
                    }
                }
                finally
                {
                    SecurityPrimitives.Clear(handshakeKey);
                    if (!promoted)
                    {
                        ClearPendingClient(pendingClient);
                        session?.Dispose();
                        pendingStream?.Close();
                        pendingClient?.Close();
                    }
                }
            }
        }

        private void ReceiveLoop(AuthenticatedConnection connection)
        {
            try
            {
                while (IsCurrentConnection(connection))
                {
                    var secureFrame = ProtocolFrame.ReadFrom(connection.Stream);
                    if (secureFrame.Type != FrameType.SecureData)
                        throw new InvalidOperationException("认证后收到非加密协议帧。");

                    var businessFrame = connection.Session.Unprotect(secureFrame);
                    FrameReceived?.Invoke(businessFrame);
                }
            }
            catch (Exception ex)
            {
                if (IsRunning())
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RemoteServer] Secure receive failed: {ex.Message}");
                }
            }
            finally
            {
                RemoveConnection(connection);
            }
        }

        /// <summary>发送一帧数据（线程安全，始终经 SecureData 外层传输）。</summary>
        public void Send(ProtocolFrame frame)
        {
            lock (_sendLock)
            {
                AuthenticatedConnection connection;
                lock (_stateLock)
                {
                    if (!_running || _connection == null)
                        return;
                    connection = _connection;
                }

                try
                {
                    var secureFrame = connection.Session.Protect(frame);
                    var data = secureFrame.Serialize();
                    connection.Stream.Write(data, 0, data.Length);
                    connection.Stream.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RemoteServer] Secure send failed: {ex.Message}");
                    RemoveConnection(connection);
                }
            }
        }

        public void Dispose()
        {
            TcpListener listener;
            TcpClient pendingClient;
            AuthenticatedConnection connection;
            byte[] preSharedKey;

            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _running = false;
                listener = _listener;
                pendingClient = _pendingClient;
                connection = _connection;
                preSharedKey = _preSharedKey;
                _listener = null;
                _pendingClient = null;
                _connection = null;
                _acceptThread = null;
                _preSharedKey = null;
            }

            listener?.Stop();
            pendingClient?.Close();
            connection?.Dispose();
            SecurityPrimitives.Clear(preSharedKey);
        }

        private bool IsRunning()
        {
            lock (_stateLock)
                return _running;
        }

        private bool RegisterPendingClient(
            TcpClient client,
            out byte[] handshakeKey)
        {
            lock (_stateLock)
            {
                if (!_running || _preSharedKey == null)
                {
                    handshakeKey = null;
                    client.Close();
                    return false;
                }

                _pendingClient = client;
                handshakeKey = (byte[])_preSharedKey.Clone();
                return true;
            }
        }

        private bool PromotePendingClient(
            TcpClient pendingClient,
            AuthenticatedConnection connection,
            out AuthenticatedConnection previous)
        {
            lock (_stateLock)
            {
                if (!_running || !ReferenceEquals(_pendingClient, pendingClient))
                {
                    previous = null;
                    return false;
                }

                _pendingClient = null;
                previous = _connection;
                _connection = connection;
                return true;
            }
        }

        private void ClearPendingClient(TcpClient pendingClient)
        {
            if (pendingClient == null)
                return;

            lock (_stateLock)
            {
                if (ReferenceEquals(_pendingClient, pendingClient))
                    _pendingClient = null;
            }
        }

        private bool IsCurrentConnection(AuthenticatedConnection connection)
        {
            lock (_stateLock)
            {
                return _running && ReferenceEquals(_connection, connection);
            }
        }

        private void RemoveConnection(AuthenticatedConnection connection)
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_connection, connection))
                    _connection = null;
            }

            connection.Dispose();
        }
    }
}
