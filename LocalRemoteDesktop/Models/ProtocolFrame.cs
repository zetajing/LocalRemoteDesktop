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
            using (var ms = new MemoryStream(5 + Payload.Length))
            {
                ms.WriteByte((byte)Type);
                var lenBytes = BitConverter.GetBytes(Payload.Length);
                ms.Write(lenBytes, 0, 4);
                ms.Write(Payload, 0, Payload.Length);
                return ms.ToArray();
            }
        }

        /// <summary>从流中反序列化一帧，返回null表示数据不足</summary>
        public static ProtocolFrame Deserialize(byte[] buffer, int offset, int count)
        {
            if (count < 5)
                return null;

            var type = (FrameType)buffer[offset];
            var payloadLen = BitConverter.ToInt32(buffer, offset + 1);

            if (payloadLen < 0 || payloadLen > MaxPayloadSize)
                return null; // 非法或超大的负载长度

            if (count < 5 + payloadLen)
                return null; // 数据还不完整

            var payload = new byte[payloadLen];
            if (payloadLen > 0)
                Buffer.BlockCopy(buffer, offset + 5, payload, 0, payloadLen);

            return new ProtocolFrame(type, payload);
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
    }
}
