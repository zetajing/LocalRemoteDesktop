using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LocalRemoteDesktop.Models;

namespace LocalRemoteDesktop.Security
{
    /// <summary>
    /// 认证后的双向安全会话。每个方向使用独立的 AES/HMAC 密钥，并要求序号严格递增。
    /// 公开工厂和 Protect/Unprotect 仅用于协议集成及安全测试，不会暴露派生密钥。
    /// </summary>
    public sealed class SecureSession : IDisposable
    {
        private const int SequenceSize = 8;
        private const int IvSize = 16;
        private const int AuthenticationTagSize = 32;
        private const byte ClientToServerDirection = 1;
        private const byte ServerToClientDirection = 2;

        private static readonly byte[] AuthenticationDomain =
            Encoding.ASCII.GetBytes("LocalRemoteDesktop/SecureData/v1");

        private readonly byte[] _sendEncryptionKey;
        private readonly byte[] _sendAuthenticationKey;
        private readonly byte[] _receiveEncryptionKey;
        private readonly byte[] _receiveAuthenticationKey;
        private readonly byte _sendDirection;
        private readonly byte _receiveDirection;
        private readonly object _sendLock = new object();
        private readonly object _receiveLock = new object();

        private ulong _sendSequence;
        private ulong _receiveSequence;
        private bool _disposed;

        private SecureSession(
            byte[] sendMaterial,
            byte[] receiveMaterial,
            byte sendDirection,
            byte receiveDirection)
        {
            _sendEncryptionKey = CopyRange(sendMaterial, 0, 32);
            _sendAuthenticationKey = CopyRange(sendMaterial, 32, 32);
            _receiveEncryptionKey = CopyRange(receiveMaterial, 0, 32);
            _receiveAuthenticationKey = CopyRange(receiveMaterial, 32, 32);
            _sendDirection = sendDirection;
            _receiveDirection = receiveDirection;
        }

        public static SecureSession CreateForClient(
            string accessCode,
            byte[] clientNonce,
            byte[] serverNonce)
        {
            var preSharedKey = SecurityPrimitives.DerivePreSharedKey(accessCode);
            try
            {
                return CreateForClient(preSharedKey, clientNonce, serverNonce);
            }
            finally
            {
                SecurityPrimitives.Clear(preSharedKey);
            }
        }

        public static SecureSession CreateForServer(
            string accessCode,
            byte[] clientNonce,
            byte[] serverNonce)
        {
            var preSharedKey = SecurityPrimitives.DerivePreSharedKey(accessCode);
            try
            {
                return CreateForServer(preSharedKey, clientNonce, serverNonce);
            }
            finally
            {
                SecurityPrimitives.Clear(preSharedKey);
            }
        }

        internal static SecureSession CreateForClient(
            byte[] preSharedKey,
            byte[] clientNonce,
            byte[] serverNonce)
        {
            return Create(
                preSharedKey,
                clientNonce,
                serverNonce,
                true);
        }

        internal static SecureSession CreateForServer(
            byte[] preSharedKey,
            byte[] clientNonce,
            byte[] serverNonce)
        {
            return Create(
                preSharedKey,
                clientNonce,
                serverNonce,
                false);
        }

        /// <summary>将一个普通业务帧封装为 SecureData。</summary>
        public ProtocolFrame Protect(ProtocolFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (ProtocolFrame.IsSecurityFrameType(frame.Type))
                throw new InvalidDataException("安全控制帧不能嵌套为业务数据。");

            lock (_sendLock)
            {
                ThrowIfDisposed();
                if (_sendSequence == ulong.MaxValue)
                    throw new CryptographicException("安全会话发送序号已耗尽。");

                var plaintext = frame.Serialize();
                byte[] cipherText = null;
                byte[] iv = null;
                try
                {
                    using (var aes = CreateAes(_sendEncryptionKey))
                    {
                        aes.GenerateIV();
                        iv = (byte[])aes.IV.Clone();
                        using (var encryptor = aes.CreateEncryptor())
                        {
                            cipherText = encryptor.TransformFinalBlock(
                                plaintext,
                                0,
                                plaintext.Length);
                        }
                    }

                    var authenticatedLength = SequenceSize + IvSize + cipherText.Length;
                    var payload = new byte[authenticatedLength + AuthenticationTagSize];
                    SecurityPrimitives.WriteUInt64BigEndian(payload, 0, _sendSequence);
                    Buffer.BlockCopy(iv, 0, payload, SequenceSize, IvSize);
                    Buffer.BlockCopy(
                        cipherText,
                        0,
                        payload,
                        SequenceSize + IvSize,
                        cipherText.Length);

                    var tag = ComputeFrameAuthenticationTag(
                        _sendAuthenticationKey,
                        _sendDirection,
                        payload,
                        authenticatedLength);
                    try
                    {
                        Buffer.BlockCopy(
                            tag,
                            0,
                            payload,
                            authenticatedLength,
                            AuthenticationTagSize);
                    }
                    finally
                    {
                        SecurityPrimitives.Clear(tag);
                    }

                    _sendSequence++;
                    return new ProtocolFrame(FrameType.SecureData, payload);
                }
                finally
                {
                    SecurityPrimitives.Clear(plaintext);
                    SecurityPrimitives.Clear(cipherText);
                    SecurityPrimitives.Clear(iv);
                }
            }
        }

        /// <summary>认证并解密一个 SecureData 外层；重放、乱序或篡改均会失败。</summary>
        public ProtocolFrame Unprotect(ProtocolFrame secureFrame)
        {
            if (secureFrame == null)
                throw new ArgumentNullException(nameof(secureFrame));
            if (secureFrame.Type != FrameType.SecureData)
                throw new InvalidDataException("认证后只接受 SecureData 外层。");

            lock (_receiveLock)
            {
                ThrowIfDisposed();
                if (_receiveSequence == ulong.MaxValue)
                    throw new CryptographicException("安全会话接收序号已耗尽。");

                var payload = secureFrame.Payload ?? new byte[0];
                var cipherLength =
                    payload.Length - SequenceSize - IvSize - AuthenticationTagSize;
                if (cipherLength <= 0 || cipherLength % 16 != 0 ||
                    payload.Length > ProtocolFrame.MaxSecurePayloadSize)
                {
                    throw AuthenticationFailure();
                }

                var authenticatedLength = payload.Length - AuthenticationTagSize;
                var expectedTag = ComputeFrameAuthenticationTag(
                    _receiveAuthenticationKey,
                    _receiveDirection,
                    payload,
                    authenticatedLength);
                try
                {
                    if (!SecurityPrimitives.FixedTimeEquals(
                        expectedTag,
                        0,
                        payload,
                        authenticatedLength,
                        AuthenticationTagSize))
                    {
                        throw AuthenticationFailure();
                    }
                }
                finally
                {
                    SecurityPrimitives.Clear(expectedTag);
                }

                var receivedSequence = SecurityPrimitives.ReadUInt64BigEndian(payload, 0);
                if (receivedSequence != _receiveSequence)
                    throw AuthenticationFailure();

                var iv = new byte[IvSize];
                var cipherText = new byte[cipherLength];
                Buffer.BlockCopy(payload, SequenceSize, iv, 0, IvSize);
                Buffer.BlockCopy(
                    payload,
                    SequenceSize + IvSize,
                    cipherText,
                    0,
                    cipherLength);

                byte[] plaintext = null;
                try
                {
                    try
                    {
                        using (var aes = CreateAes(_receiveEncryptionKey))
                        {
                            aes.IV = iv;
                            using (var decryptor = aes.CreateDecryptor())
                            {
                                plaintext = decryptor.TransformFinalBlock(
                                    cipherText,
                                    0,
                                    cipherText.Length);
                            }
                        }
                    }
                    catch (CryptographicException)
                    {
                        throw AuthenticationFailure();
                    }

                    var frame = ProtocolFrame.Deserialize(plaintext, 0, plaintext.Length);
                    if (frame == null ||
                        5 + frame.Payload.Length != plaintext.Length ||
                        ProtocolFrame.IsSecurityFrameType(frame.Type))
                    {
                        throw AuthenticationFailure();
                    }

                    _receiveSequence++;
                    return frame;
                }
                finally
                {
                    SecurityPrimitives.Clear(iv);
                    SecurityPrimitives.Clear(cipherText);
                    SecurityPrimitives.Clear(plaintext);
                }
            }
        }

        public void Dispose()
        {
            lock (_sendLock)
            {
                lock (_receiveLock)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    SecurityPrimitives.Clear(_sendEncryptionKey);
                    SecurityPrimitives.Clear(_sendAuthenticationKey);
                    SecurityPrimitives.Clear(_receiveEncryptionKey);
                    SecurityPrimitives.Clear(_receiveAuthenticationKey);
                }
            }
        }

        private static SecureSession Create(
            byte[] preSharedKey,
            byte[] clientNonce,
            byte[] serverNonce,
            bool clientRole)
        {
            var directionalKeys = SecurityPrimitives.DeriveDirectionalKeys(
                preSharedKey,
                clientNonce,
                serverNonce);
            try
            {
                if (clientRole)
                {
                    return new SecureSession(
                        directionalKeys[0],
                        directionalKeys[1],
                        ClientToServerDirection,
                        ServerToClientDirection);
                }

                return new SecureSession(
                    directionalKeys[1],
                    directionalKeys[0],
                    ServerToClientDirection,
                    ClientToServerDirection);
            }
            finally
            {
                SecurityPrimitives.Clear(directionalKeys[0]);
                SecurityPrimitives.Clear(directionalKeys[1]);
            }
        }

        private static AesCryptoServiceProvider CreateAes(byte[] key)
        {
            return new AesCryptoServiceProvider
            {
                KeySize = 256,
                BlockSize = 128,
                Mode = CipherMode.CBC,
                Padding = PaddingMode.PKCS7,
                Key = key
            };
        }

        private static byte[] ComputeFrameAuthenticationTag(
            byte[] authenticationKey,
            byte direction,
            byte[] payload,
            int authenticatedLength)
        {
            var domain = new byte[AuthenticationDomain.Length + 3];
            Buffer.BlockCopy(
                AuthenticationDomain,
                0,
                domain,
                0,
                AuthenticationDomain.Length);
            domain[AuthenticationDomain.Length] = SecurityPrimitives.ProtocolVersion;
            domain[AuthenticationDomain.Length + 1] = direction;
            domain[AuthenticationDomain.Length + 2] = (byte)FrameType.SecureData;

            try
            {
                using (var hmac = new HMACSHA256(authenticationKey))
                {
                    hmac.TransformBlock(domain, 0, domain.Length, domain, 0);
                    hmac.TransformFinalBlock(payload, 0, authenticatedLength);
                    return (byte[])hmac.Hash.Clone();
                }
            }
            finally
            {
                SecurityPrimitives.Clear(domain);
            }
        }

        private static byte[] CopyRange(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static CryptographicException AuthenticationFailure()
        {
            return new CryptographicException("安全帧认证失败。");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SecureSession));
        }
    }
}
