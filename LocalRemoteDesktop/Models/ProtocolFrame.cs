using System;
using System.IO;

namespace LocalRemoteDesktop.Models
{
    /// <summary>
    /// 二进制协议帧:
    /// [帧类型 (1 byte)] + [数据长度 (4 byte, little-endian)] + [数据体]
    /// </summary>
    public class ProtocolFrame
    {
        /// <summary>最大单帧负载 (10 MB)</summary>
        public const int MaxPayloadSize = 10 * 1024 * 1024;

        /// <summary>
        /// 加密外层的最大负载。它包含业务帧头、序号、IV、PKCS#7 填充和认证标签。
        /// </summary>
        public const int MaxSecurePayloadSize = MaxPayloadSize + 72;

        /// <summary>握手帧仅包含版本、随机数和 HMAC，不应承载任意数据。</summary>
        public const int MaxHandshakePayloadSize = 256;

        public FrameType Type { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public ProtocolFrame() { }

        public ProtocolFrame(FrameType type, byte[] payload)
        {
            Type = type;
            Payload = payload ?? Array.Empty<byte>();
        }

        /// <summary>序列化为字节数组</summary>
        public byte[] Serialize()
        {
            var payload = Payload ?? Array.Empty<byte>();
            ValidatePayloadLength(Type, payload.Length);

            var result = new byte[5 + payload.Length];
            result[0] = (byte)Type;
            WriteInt32LittleEndian(result, 1, payload.Length);
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, result, 5, payload.Length);
            return result;
        }

        /// <summary>从流中反序列化一帧，返回null表示数据不足</summary>
        public static ProtocolFrame Deserialize(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException();
            if (count < 5)
                return null;

            var type = (FrameType)buffer[offset];
            var payloadLen = ReadInt32LittleEndian(buffer, offset + 1);

            if (!IsValidPayloadLength(type, payloadLen))
                return null; // 非法或超大的负载长度

            if (count - 5 < payloadLen)
                return null; // 数据还不完整

            var payload = new byte[payloadLen];
            if (payloadLen > 0)
                Buffer.BlockCopy(buffer, offset + 5, payload, 0, payloadLen);

            return new ProtocolFrame(type, payload);
        }

        /// <summary>
        /// 从流中精确读取一帧：先读取固定 5 字节头，再按声明长度读取负载。
        /// 流提前结束或长度非法时抛出异常，避免依赖固定大小的聚合缓冲区。
        /// </summary>
        public static ProtocolFrame ReadFrom(Stream stream)
        {
            return ReadFrom(stream, MaxSecurePayloadSize);
        }

        internal static ProtocolFrame ReadFrom(Stream stream, int transportPayloadLimit)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (transportPayloadLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(transportPayloadLimit));

            var header = new byte[5];
            ReadExactly(stream, header, 0, header.Length);

            var type = (FrameType)header[0];
            var payloadLength = ReadInt32LittleEndian(header, 1);
            ValidatePayloadLength(type, payloadLength);
            if (payloadLength > transportPayloadLimit)
                throw new InvalidDataException("协议帧超过当前阶段允许的长度。");

            var payload = new byte[payloadLength];
            if (payloadLength > 0)
                ReadExactly(stream, payload, 0, payloadLength);

            return new ProtocolFrame(type, payload);
        }

        /// <summary>安全控制类型不会被作为普通业务帧分发。</summary>
        public static bool IsSecurityFrameType(FrameType type)
        {
            return type == FrameType.ClientHello ||
                   type == FrameType.ServerChallenge ||
                   type == FrameType.ClientProof ||
                   type == FrameType.ServerProof ||
                   type == FrameType.SecureData;
        }

        public static bool IsHandshakeFrameType(FrameType type)
        {
            return type == FrameType.ClientHello ||
                   type == FrameType.ServerChallenge ||
                   type == FrameType.ClientProof ||
                   type == FrameType.ServerProof;
        }

        /// <summary>构造鼠标移动帧（归一化坐标 0.0-1.0，参考 CrossDesk 设计）</summary>
        public static ProtocolFrame CreateMouseMove(float x, float y)
        {
            var data = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, data, 4, 4);
            return new ProtocolFrame(FrameType.MouseMove, data);
        }

        /// <summary>构造鼠标滚轮帧</summary>
        public static ProtocolFrame CreateMouseWheel(int delta)
        {
            return new ProtocolFrame(FrameType.MouseWheel, BitConverter.GetBytes(delta));
        }

        /// <summary>构造键盘按键帧</summary>
        public static ProtocolFrame CreateKeyEvent(FrameType type, int virtualKey)
        {
            return new ProtocolFrame(type, BitConverter.GetBytes(virtualKey));
        }

        /// <summary>构造 Tile 帧 — payload: [2byte X][2byte Y][2byte W][2byte H][JPEG]</summary>
        public static ProtocolFrame CreateTile(int x, int y, int w, int h, byte[] jpeg)
        {
            var data = new byte[8 + jpeg.Length];
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)x), 0, data, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)y), 0, data, 2, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)w), 0, data, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)h), 0, data, 6, 2);
            Buffer.BlockCopy(jpeg, 0, data, 8, jpeg.Length);
            return new ProtocolFrame(FrameType.TileImage, data);
        }

        /// <summary>解析 Tile 帧 payload，返回 (x, y, w, h, jpegData)</summary>
        public static (int x, int y, int w, int h, byte[] jpeg) ParseTile(byte[] payload)
        {
            int x = BitConverter.ToUInt16(payload, 0);
            int y = BitConverter.ToUInt16(payload, 2);
            int w = BitConverter.ToUInt16(payload, 4);
            int h = BitConverter.ToUInt16(payload, 6);
            var jpeg = new byte[payload.Length - 8];
            Buffer.BlockCopy(payload, 8, jpeg, 0, jpeg.Length);
            return (x, y, w, h, jpeg);
        }

        private static bool IsValidPayloadLength(FrameType type, int payloadLength)
        {
            if (payloadLength < 0)
                return false;

            if (type == FrameType.SecureData)
                return payloadLength <= MaxSecurePayloadSize;

            if (IsHandshakeFrameType(type))
                return payloadLength <= MaxHandshakePayloadSize;

            return payloadLength <= MaxPayloadSize;
        }

        private static void ValidatePayloadLength(FrameType type, int payloadLength)
        {
            if (!IsValidPayloadLength(type, payloadLength))
                throw new InvalidDataException("协议帧负载长度非法。");
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    throw new EndOfStreamException("连接在协议帧完整到达前已关闭。");

                offset += read;
                count -= read;
            }
        }

        private static int ReadInt32LittleEndian(byte[] buffer, int offset)
        {
            return buffer[offset] |
                   (buffer[offset + 1] << 8) |
                   (buffer[offset + 2] << 16) |
                   (buffer[offset + 3] << 24);
        }

        private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
    }
}
