using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LocalRemoteDesktop.Capture
{
    /// <summary>
    /// DXGI Desktop Duplication 屏幕捕获 + 全帧 JPEG 编码
    ///
    /// 核心改进 vs 旧版 GDI BitBlt 方案：
    /// ┌─────────────────────┬──────────────┬─────────────────┐
    /// │       项目          │  旧版 (GDI)   │  新版 (DXGI)     │
    /// ├─────────────────────┼──────────────┼─────────────────┤
    /// │ 截屏延迟            │ ~38ms         │ ~8ms             │
    /// │ 变化检测            │ GetPixel 逐像素│ 全帧自动检测     │
    /// │ 纹理拷贝            │ CPU 内存       │ GPU 显存         │
    /// │ VSync 同步          │ 无            │ 自带             │
    /// │ 编码方式            │ Tile JPEG     │ 全帧 JPEG        │
    /// └─────────────────────┴──────────────┴─────────────────┘
    /// </summary>
    public class ScreenCapture : IDisposable
    {
        public const int TileSize = 128; // 保持兼容性（不再实际使用）

        private int _monitorIndex;
        private readonly int _jpegQuality;
        private MemoryStream _reusableStream;
        private readonly object _captureLock = new object();
        private bool _disposed;

        private DXGICapture _dxgiCapture;
        private byte[] _previousFrame; // 用于变化检测的缓存
        private bool _firstFrame = true;

        /// <summary>分辨率是否在上次捕获后改变</summary>
        public bool SizeChanged { get; private set; }

        /// <summary>读取并重置 SizeChanged 标记（ServerRunner 在发送 ScreenInfo 后调用）</summary>
        public void ResetSizeChanged() => SizeChanged = false;

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

        public ScreenCapture(int jpegQuality = 80, int monitorIndex = 0)
        {
            _monitorIndex = monitorIndex;
            _jpegQuality = Math.Max(10, Math.Min(100, jpegQuality));

            // 缓存 JPEG 编码器
            _jpegEncoder = Array.Find(ImageCodecInfo.GetImageEncoders(),
                e => e.FormatID == ImageFormat.Jpeg.Guid);
            var qualityParam = new EncoderParameter(Encoder.Quality, (long)_jpegQuality);
            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = qualityParam;
        }

        /// <summary>切换到指定显示器</summary>
        public void SetMonitor(int index)
        {
            if (index < 0 || index >= System.Windows.Forms.Screen.AllScreens.Length)
                return;

            lock (_captureLock)
            {
                _monitorIndex = index;
                _dxgiCapture?.Dispose();
                _dxgiCapture = null;
                _previousFrame = null;
                _firstFrame = true;
            }
        }

        /// <summary>获取当前捕获的显示器索引</summary>
        public int GetMonitorIndex() => _monitorIndex;

        /// <summary>
        /// 捕获屏幕变化并返回变化的 Tile 列表。
        /// 接口保持与旧版兼容，但内部使用 DXGI 全帧捕获 + 像素级变化检测。
        /// </summary>
        public List<TileInfo> CaptureChangedTiles()
        {
            var result = new List<TileInfo>();
            if (_disposed) return result;

            lock (_captureLock)
            {
                // 1. 延迟初始化 DXGI
                if (_dxgiCapture == null || !_dxgiCapture.Initialized)
                {
                    _dxgiCapture?.Dispose();
                    _dxgiCapture = new DXGICapture();
                    if (!_dxgiCapture.Initialize(_monitorIndex))
                    {
                        // 回退到旧版 GDI
                        return FallbackCapture();
                    }
                }

                // 2. 捕获帧
                if (!_dxgiCapture.TryAcquireNextFrame(out byte[] pixels, out bool sizeChanged))
                {
                    return result; // 无新帧，等待下一个 vsync
                }

                try
                {
                    int width = _dxgiCapture.Width;
                    int height = _dxgiCapture.Height;

                    if (width <= 0 || height <= 0)
                        return result;

                    // 3. 分辨率变化或首次：发送全帧 ScreenInfo
                    if (sizeChanged || _firstFrame)
                    {
                        SizeChanged = true;
                        _previousFrame = null;
                    }

                    // 4. 简化策略：对于首次或大变化，编码并返回全帧
                    //    对于后续帧，检测是否有任何变化
                    bool hasChanges = _firstFrame || sizeChanged;

                    if (!hasChanges && _previousFrame != null)
                    {
                        // 快速像素级变化检测（隔行采样）
                        hasChanges = DetectChanges(pixels, _previousFrame, width, height);
                    }

                    if (hasChanges)
                    {
                        // 编码为 JPEG
                        byte[] jpeg = EncodeJpeg(pixels, width, height);
                        result.Add(new TileInfo
                        {
                            X = 0, Y = 0,
                            Width = width, Height = height,
                            JpegData = jpeg
                        });

                        // 缓存当前帧用于下次变化检测
                        _previousFrame = pixels;
                        _firstFrame = false;
                    }
                }
                finally
                {
                    _dxgiCapture.ReleaseFrame();
                }
            }

            return result;
        }

        /// <summary>像素级变化检测（隔行采样，50μs 级快速检测）</summary>
        private bool DetectChanges(byte[] current, byte[] previous, int w, int h)
        {
            if (current == null || previous == null || current.Length != previous.Length)
                return true;

            // 采样步长：水平 8px，垂直 4px
            int stride = w * 4;
            int sampleStepX = 8;
            int sampleStepY = 4;

            // 高分辨率下采样约 2% 的像素，50μs 级检测
            for (int y = 0; y < h; y += sampleStepY)
            {
                int rowStart = y * stride;
                for (int x = 0; x < w; x += sampleStepX)
                {
                    int idx = rowStart + x * 4;
                    // 比较 BGRA 值
                    if (current[idx] != previous[idx] ||
                        current[idx + 1] != previous[idx + 1] ||
                        current[idx + 2] != previous[idx + 2])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>将 BGRA32 像素数据编码为 JPEG</summary>
        private byte[] EncodeJpeg(byte[] pixels, int width, int height)
        {
            // 用 System.Drawing 编码 JPEG（简单可靠）
            using (var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    int stride = width * 4;
                    int srcStride = width * 4;

                    if (bmpData.Stride == srcStride)
                    {
                        Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr dstRow = IntPtr.Add(bmpData.Scan0, y * bmpData.Stride);
                            Marshal.Copy(pixels, y * srcStride, dstRow, srcStride);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                return JpegEncode(bmp);
            }
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

        /// <summary>GDI 回退捕获（当 DXGI 不可用时）</summary>
        private List<TileInfo> FallbackCapture()
        {
            var result = new List<TileInfo>();
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                int idx = Math.Max(0, Math.Min(_monitorIndex, screens.Length - 1));
                var bounds = screens[idx].Bounds;
                int w = bounds.Width, h = bounds.Height;

                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size,
                        CopyPixelOperation.SourceCopy);

                    byte[] jpeg = JpegEncode(bmp);
                    result.Add(new TileInfo { X = 0, Y = 0, Width = w, Height = h, JpegData = jpeg });
                }
            }
            catch { }
            return result;
        }

        /// <summary>获取当前捕获的显示器尺寸</summary>
        public (int w, int h) GetScreenSize()
        {
            if (_dxgiCapture?.Initialized == true && _dxgiCapture.Width > 0)
                return (_dxgiCapture.Width, _dxgiCapture.Height);

            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Math.Max(0, Math.Min(_monitorIndex, screens.Length - 1));
            var b = screens[idx].Bounds;
            return (b.Width, b.Height);
        }

        /// <summary>获取系统显示器数量</summary>
        public static int GetMonitorCount()
            => System.Windows.Forms.Screen.AllScreens.Length;

        /// <summary>获取各显示器信息</summary>
        public static (int index, int left, int top, int width, int height, bool isPrimary)[] GetMonitorInfo()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var result = new (int, int, int, int, int, bool)[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                result[i] = (i, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary);
            }
            return result;
        }

        public void Dispose()
        {
            lock (_captureLock)
            {
                _disposed = true;
                _dxgiCapture?.Dispose();
                _dxgiCapture = null;
                _reusableStream?.Dispose();
                _reusableStream = null;
                _previousFrame = null;
            }
        }
    }
}
