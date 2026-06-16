using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Resource = SharpDX.DXGI.Resource;

namespace LocalRemoteDesktop.Capture
{
    /// <summary>
    /// DXGI Desktop Duplication 屏幕捕获
    /// 延迟 ~8ms（vs GDI BitBlt 的 ~38ms），与 VSync 对齐
    ///
    /// 使用 SharpDX（.NET Framework 4.x 上最成熟的 DXGI 封装）
    /// </summary>
    public class DXGICapture : IDisposable
    {
        private Device _device;
        private DeviceContext _context;
        private OutputDuplication _duplication;
        private Texture2D _stagingTex;
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool Initialized { get; private set; }
        public int MonitorIndex { get; private set; }

        /// <summary>初始化 DXGI 拷贝</summary>
        public bool Initialize(int monitorIndex = 0)
        {
            try
            {
                MonitorIndex = monitorIndex;

                // 1. 创建 D3D11 设备
                var adapter = GetOutputAdapter(monitorIndex);
                if (adapter != null)
                {
                    _device = new Device(adapter, DeviceCreationFlags.BgraSupport);
                }
                else
                {
                    // 回退到主显示器
                    _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                }
                _context = _device.ImmediateContext;

                CreateDuplication();

                Initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DXGICapture] Init: {ex.Message}");
                return false;
            }
        }

        private void CreateDuplication()
        {
            _duplication?.Dispose();

            using (var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>())
            using (var adapter = dxgiDevice.GetParent<Adapter>())
            {
                var output = adapter.GetOutput(MonitorIndex);
                var output1 = output.QueryInterface<Output1>();
                _duplication = output1.DuplicateOutput(_device);
            }
        }

        private Adapter GetOutputAdapter(int monitorIndex)
        {
            try
            {
                using (var factory = new Factory1())
                {
                    for (int ai = 0; ai < factory.GetAdapterCount1(); ai++)
                    {
                        var adapter = factory.GetAdapter1(ai);
                        try
                        {
                            for (int oi = 0; oi < adapter.GetOutputCount(); oi++)
                            {
                                var output = adapter.GetOutput(oi);
                                try
                                {
                                    if (oi == monitorIndex)
                                        return adapter;
                                }
                                finally { output.Dispose(); }
                            }
                        }
                        finally { adapter.Dispose(); }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>捕获下一帧</summary>
        public bool TryAcquireNextFrame(out byte[] pixels, out bool sizeChanged)
        {
            pixels = null;
            sizeChanged = false;

            if (!Initialized || _disposed)
                return false;

            try
            {
                SharpDX.Result result;
                OutputDuplicateFrameInformation frameInfo;
                Resource resource;

                result = _duplication.TryAcquireNextFrame(100, out frameInfo, out resource);

                if (result.Failure || resource == null)
                {
                    if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                        return false; // 无新帧
                    if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code)
                    {
                        Reinitialize();
                        return false;
                    }
                    return false;
                }

                using (resource)
                using (var texture = resource.QueryInterface<Texture2D>())
                {
                    var desc = texture.Description;

                    if (_stagingTex == null ||
                        _stagingTex.Description.Width != desc.Width ||
                        _stagingTex.Description.Height != desc.Height)
                    {
                        _stagingTex?.Dispose();
                        _stagingTex = CreateStagingTexture(desc.Width, desc.Height);
                        Width = desc.Width;
                        Height = desc.Height;
                        sizeChanged = true;
                    }

                    // GPU 端拷贝
                    _context.CopyResource(texture, _stagingTex);

                    // Map 到 CPU
                    var mapSource = _context.MapSubresource(_stagingTex, 0, MapMode.Read, MapFlags.None);
                    try
                    {
                        int rowPitch = mapSource.RowPitch;
                        int bpp = 4;
                        int stride = Width * bpp;
                        int totalSize = stride * Height;

                        pixels = new byte[totalSize];

                        if (rowPitch == stride)
                        {
                            Marshal.Copy(mapSource.DataPointer, pixels, 0, totalSize);
                        }
                        else
                        {
                            for (int y = 0; y < Height; y++)
                            {
                                IntPtr srcRow = IntPtr.Add(mapSource.DataPointer, y * rowPitch);
                                Marshal.Copy(srcRow, pixels, y * stride, stride);
                            }
                        }

                        return true;
                    }
                    finally
                    {
                        _context.UnmapSubresource(_stagingTex, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DXGICapture] Acquire: {ex.Message}");
                return false;
            }
        }

        /// <summary>释放当前帧</summary>
        public void ReleaseFrame()
        {
            try { _duplication?.ReleaseFrame(); } catch { }
        }

        private Texture2D CreateStagingTexture(int width, int height)
        {
            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };
            return new Texture2D(_device, desc);
        }

        private void Reinitialize()
        {
            _duplication?.Dispose();
            _duplication = null;
            _stagingTex?.Dispose();
            _stagingTex = null;

            try { CreateDuplication(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _duplication?.ReleaseFrame();
            _duplication?.Dispose();
            _stagingTex?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
