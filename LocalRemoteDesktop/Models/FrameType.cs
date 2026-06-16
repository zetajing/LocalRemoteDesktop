namespace LocalRemoteDesktop.Models
{
    public enum FrameType : byte
    {
        /// <summary>屏幕图像帧 (Server → Client)</summary>
        ScreenImage = 0x01,

        /// <summary>鼠标移动 (Client → Server)</summary>
        MouseMove = 0x02,

        /// <summary>鼠标左键按下</summary>
        MouseLeftDown = 0x03,

        /// <summary>鼠标左键释放</summary>
        MouseLeftUp = 0x04,

        /// <summary>鼠标右键按下</summary>
        MouseRightDown = 0x05,

        /// <summary>鼠标右键释放</summary>
        MouseRightUp = 0x06,

        /// <summary>鼠标滚轮 (Client → Server)</summary>
        MouseWheel = 0x07,

        /// <summary>键盘按键按下 (Client → Server)</summary>
        KeyDown = 0x08,

        /// <summary>键盘按键释放 (Client → Server)</summary>
        KeyUp = 0x09,

        /// <summary>文件传输请求 (Client → Server) payload: 文件名(UTF8)</summary>
        FileRequest = 0x10,

        /// <summary>文件传输接受 (Server → Client)</summary>
        FileAccept = 0x11,

        /// <summary>文件传输拒绝 (Server → Client)</summary>
        FileReject = 0x12,

        /// <summary>文件数据块 (Client → Server) payload: 4字节序号 + 数据</summary>
        FileData = 0x13,

        /// <summary>文件传输完成 (Client → Server)</summary>
        FileEnd = 0x14,

        /// <summary>剪贴板文本同步 (双向) payload: UTF8文本</summary>
        ClipboardText = 0x15,

        /// <summary>心跳</summary>
        Heartbeat = 0xFF,

        // ---- Tile 流加速 ----

        /// <summary>屏幕信息 (Server → Client) 首次建立画布用</summary>
        ScreenInfo = 0x20,

        /// <summary>方块画面更新 (Server → Client) payload: [2byte X][2byte Y][2byte W][2byte H][JPEG数据]</summary>
        TileImage = 0x21,

        /// <summary>Tile本帧发送完毕 (Server → Client) 通知客户端刷新画面</summary>
        TileEnd = 0x22,

        /// <summary>客户端分辨率通知 (Client → Server) payload: [2byte W][2byte H]</summary>
        ClientResolution = 0x23,
    }
}
