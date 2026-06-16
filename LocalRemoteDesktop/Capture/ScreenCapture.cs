using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LocalRemoteDesktop.Capture
{
    /// <summary>
    /// 屏幕捕获和JPEG编码 — Tile 化增量传输。
    /// 将屏幕切为 128x128 方块，只发送变化的部分。
    /// </summary>
    public class ScreenCapture : IDisposable
    {
        public const int TileSize = 128;

        private int _monitorIndex;
        private readonly int _jpegQuality;
        private Bitmap _currentBitmap;
        private Bitmap _previousBitmap;
        private MemoryStream _reusableStream;
        private readonly object _bitmapLock = new object();
        private bool _disposed;
        private bool _firstFrame = true;

        private ImageCodecInfo _jpegEncoder;
        private EncoderParameters _encoderParams;

        public class TileInfo
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public byte[] JpegData { get; set; }
        }

        public ScreenCapture(int jpegQuality = 70, int monitorIndex = 0)
        {
            _monitorIndex = monitorIndex;
            _jpegQuality = Math.Max(10, Math.Min(100, jpegQuality));
            _jpegEncoder = Array.Find(ImageCodecInfo.GetImageEncoders(), e => e.FormatID == ImageFormat.Jpeg.Guid);
            var qualityParam = new EncoderParameter(Encoder.Quality, (long)_jpegQuality);
            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = qualityParam;
        }

        /// <summary>切换到指定显示器（0=主屏, 1=副屏）</summary>
        public void SetMonitor(int index)
        {
            if (index >= 0 && index < System.Windows.Forms.Screen.AllScreens.Length)
            {
                _monitorIndex = index;
                _firstFrame = true; // 切换后全量发送
            }
        }

        /// <summary>获取当前捕获的显示器索引</summary>
        public int GetMonitorIndex() => _monitorIndex;

        /// <summary>
        /// 捕获全屏并检测变化 Tile。首次调用返回全部 Tile。
        /// </summary>
        public List<TileInfo> CaptureChangedTiles()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Math.Max(0, Math.Min(_monitorIndex, screens.Length - 1));
            var bounds = screens[idx].Bounds;
            int scrW = bounds.Width, scrH = bounds.Height;
            int cols = (scrW + TileSize - 1) / TileSize;
            int rows = (scrH + TileSize - 1) / TileSize;

            // 截图
            lock (_bitmapLock)
            {
                if (_disposed) return new List<TileInfo>();

                if (_currentBitmap == null ||
                    _currentBitmap.Width != scrW || _currentBitmap.Height != scrH)
                {
                    _currentBitmap?.Dispose();
                    _currentBitmap = new Bitmap(scrW, scrH, PixelFormat.Format32bppArgb);
                    _previousBitmap?.Dispose();
                    _previousBitmap = new Bitmap(scrW, scrH, PixelFormat.Format32bppArgb);
                }

                using (var g = Graphics.FromImage(_currentBitmap))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }
            }

            var changedTiles = new List<TileInfo>();

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int tileX = col * TileSize;
                    int tileY = row * TileSize;
                    int tileW = Math.Min(TileSize, scrW - tileX);
                    int tileH = Math.Min(TileSize, scrH - tileY);

                    // 首次全部发送，后续只发送变化的
                    if (!_firstFrame && !TileChanged(tileX, tileY, tileW, tileH))
                        continue;

                    var tile = CaptureTile(tileX, tileY, tileW, tileH);
                    if (tile != null)
                        changedTiles.Add(tile);
                }
            }

            _firstFrame = false;

            // 交换 current 和 previous
            var temp = _currentBitmap;
            _currentBitmap = _previousBitmap;
            _previousBitmap = temp;

            return changedTiles;
        }

        /// <summary>判断一个 Tile 是否有变化（像素级别采样对比）</summary>
        private bool TileChanged(int x, int y, int w, int h)
        {
            lock (_bitmapLock)
            {
                if (_disposed || _currentBitmap == null || _previousBitmap == null)
                    return true;

                int step = 4; // 每4像素采样一次
                for (int py = y; py < y + h; py += step)
                {
                    for (int px = x; px < x + w; px += step)
                    {
                        if (_currentBitmap.GetPixel(px, py) != _previousBitmap.GetPixel(px, py))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>截取并编码一个 Tile</summary>
        private TileInfo CaptureTile(int x, int y, int w, int h)
        {
            byte[] jpeg;
            lock (_bitmapLock)
            {
                if (_disposed || _currentBitmap == null) return null;

                using (var tile = _currentBitmap.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb))
                {
                    jpeg = JpegEncode(tile);
                }
            }

            return new TileInfo { X = x, Y = y, Width = w, Height = h, JpegData = jpeg };
        }

        /// <summary>将 Bitmap 编码为 JPEG 字节数组（复用 MemoryStream）</summary>
        private byte[] JpegEncode(Image image)
        {
            if (_reusableStream == null)
                _reusableStream = new MemoryStream();
            else
                _reusableStream.SetLength(0);

            image.Save(_reusableStream, _jpegEncoder, _encoderParams);
            return _reusableStream.ToArray();
        }

        /// <summary>获取当前捕获的显示器尺寸</summary>
        public (int w, int h) GetScreenSize()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Math.Max(0, Math.Min(_monitorIndex, screens.Length - 1));
            var b = screens[idx].Bounds;
            return (b.Width, b.Height);
        }

        /// <summary>获取系统显示器数量</summary>
        public static int GetMonitorCount()
            => System.Windows.Forms.Screen.AllScreens.Length;

        /// <summary>获取各显示器信息（索引、位置、尺寸）</summary>
        public static (int index, int left, int top, int width, int height, bool isPrimary)[] GetMonitorInfo()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var result = new (int, int, int, int, int, bool)[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                result[i] = (i, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height,
                    s.Primary);
            }
            return result;
        }

        public void Dispose()
        {
            lock (_bitmapLock)
            {
                _disposed = true;
                _currentBitmap?.Dispose();
                _currentBitmap = null;
                _previousBitmap?.Dispose();
                _previousBitmap = null;
                _reusableStream?.Dispose();
                _reusableStream = null;
            }
        }
    }
}
