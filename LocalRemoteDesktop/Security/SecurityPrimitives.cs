using System;
using System.Security.Cryptography;
using System.Text;

namespace LocalRemoteDesktop.Security
{
    internal static class SecurityPrimitives
    {
        internal const byte ProtocolVersion = 1;
        internal const int NonceSize = 32;
        internal const int ProofSize = 32;

        private static readonly byte[] PskDomain =
            Encoding.ASCII.GetBytes("LocalRemoteDesktop/PSK/v1");

        private static readonly byte[] ProofDomain =
            Encoding.ASCII.GetBytes("LocalRemoteDesktop/HandshakeProof/v1");

        private static readonly byte[] SessionSaltDomain =
            Encoding.ASCII.GetBytes("LocalRemoteDesktop/SessionSalt/v1");

        private static readonly byte[] SessionKeyDomain =
            Encoding.ASCII.GetBytes("LocalRemoteDesktop/SessionKeys/v1");

        internal static byte[] DerivePreSharedKey(string accessCode)
        {
            if (string.IsNullOrWhiteSpace(accessCode))
                throw new ArgumentException("访问码不能为空。", nameof(accessCode));

            var value = Encoding.UTF8.GetBytes(accessCode);
            try
            {
                return ComputeHmac(PskDomain, value);
            }
            finally
            {
                Clear(value);
            }
        }

        internal static byte[] ComputeHandshakeProof(
            byte[] preSharedKey,
            string proofRole,
            byte[] clientNonce,
            byte[] serverNonce)
        {
            ValidateNonce(clientNonce, nameof(clientNonce));
            ValidateNonce(serverNonce, nameof(serverNonce));

            var role = Encoding.ASCII.GetBytes(proofRole);
            var input = Combine(
                ProofDomain,
                new[] { ProtocolVersion, (byte)role.Length },
                role,
                clientNonce,
                serverNonce);

            try
            {
                return ComputeHmac(preSharedKey, input);
            }
            finally
            {
                Clear(input);
            }
        }

        internal static byte[][] DeriveDirectionalKeys(
            byte[] preSharedKey,
            byte[] clientNonce,
            byte[] serverNonce)
        {
            ValidateNonce(clientNonce, nameof(clientNonce));
            ValidateNonce(serverNonce, nameof(serverNonce));

            byte[] saltInput = null;
            byte[] salt = null;
            byte[] pseudoRandomKey = null;
            byte[] clientInfo = null;
            byte[] serverInfo = null;
            try
            {
                saltInput = Combine(SessionSaltDomain, clientNonce, serverNonce);
                using (var sha = SHA256.Create())
                    salt = sha.ComputeHash(saltInput);

                pseudoRandomKey = ComputeHmac(salt, preSharedKey);
                clientInfo = Combine(
                    SessionKeyDomain,
                    clientNonce,
                    serverNonce,
                    Encoding.ASCII.GetBytes("client-to-server"));
                serverInfo = Combine(
                    SessionKeyDomain,
                    clientNonce,
                    serverNonce,
                    Encoding.ASCII.GetBytes("server-to-client"));

                var clientMaterial = HkdfExpand(pseudoRandomKey, clientInfo, 64);
                var serverMaterial = HkdfExpand(pseudoRandomKey, serverInfo, 64);
                return new[] { clientMaterial, serverMaterial };
            }
            finally
            {
                Clear(saltInput);
                Clear(salt);
                Clear(pseudoRandomKey);
                Clear(clientInfo);
                Clear(serverInfo);
            }
        }

        internal static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return bytes;
        }

        internal static byte[] ComputeHmac(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(data);
        }

        internal static byte[] HkdfExpand(byte[] pseudoRandomKey, byte[] info, int length)
        {
            if (length <= 0 || length > 255 * 32)
                throw new ArgumentOutOfRangeException(nameof(length));

            var output = new byte[length];
            var previous = new byte[0];
            var offset = 0;
            byte counter = 1;

            try
            {
                using (var hmac = new HMACSHA256(pseudoRandomKey))
                {
                    while (offset < output.Length)
                    {
                        var blockInput = Combine(previous, info, new[] { counter });
                        byte[] block;
                        try
                        {
                            block = hmac.ComputeHash(blockInput);
                        }
                        finally
                        {
                            Clear(blockInput);
                        }

                        Clear(previous);
                        previous = block;

                        var toCopy = Math.Min(block.Length, output.Length - offset);
                        Buffer.BlockCopy(block, 0, output, offset, toCopy);
                        offset += toCopy;
                        counter++;
                    }
                }

                return output;
            }
            finally
            {
                Clear(previous);
            }
        }

        internal static bool FixedTimeEquals(
            byte[] left,
            int leftOffset,
            byte[] right,
            int rightOffset,
            int count)
        {
            if (left == null || right == null ||
                leftOffset < 0 || rightOffset < 0 || count < 0 ||
                leftOffset > left.Length - count ||
                rightOffset > right.Length - count)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < count; i++)
                difference |= left[leftOffset + i] ^ right[rightOffset + i];
            return difference == 0;
        }

        internal static ulong ReadUInt64BigEndian(byte[] value, int offset)
        {
            ulong result = 0;
            for (var i = 0; i < 8; i++)
                result = (result << 8) | value[offset + i];
            return result;
        }

        internal static void WriteUInt64BigEndian(byte[] value, int offset, ulong number)
        {
            for (var i = 7; i >= 0; i--)
            {
                value[offset + i] = (byte)number;
                number >>= 8;
            }
        }

        internal static byte[] Combine(params byte[][] values)
        {
            var length = 0;
            for (var i = 0; i < values.Length; i++)
                length = checked(length + values[i].Length);

            var result = new byte[length];
            var offset = 0;
            for (var i = 0; i < values.Length; i++)
            {
                Buffer.BlockCopy(values[i], 0, result, offset, values[i].Length);
                offset += values[i].Length;
            }
            return result;
        }

        internal static void Clear(byte[] value)
        {
            if (value != null)
                Array.Clear(value, 0, value.Length);
        }

        private static void ValidateNonce(byte[] nonce, string parameterName)
        {
            if (nonce == null || nonce.Length != NonceSize)
                throw new ArgumentException("握手随机数长度非法。", parameterName);
        }
    }
}
