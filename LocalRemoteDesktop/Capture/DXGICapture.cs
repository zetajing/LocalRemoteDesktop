using System;
using System.Runtime.InteropServices;
using SharpDX;
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
    /// </summary>
    public class DXGICapture : IDisposable
    {
        private Device _device;
        private DeviceContext _context;
        private OutputDuplication _duplication;
        private Texture2D _stagingTex;
        private int _adapterOutputIndex = -1;
        private bool _frameAcquired;
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool Initialized { get; private set; }
        public int MonitorIndex { get; private set; }

        /// <summary>初始化 DXGI 拷贝</summary>
        public bool Initialize(int monitorIndex = 0)
        {
            if (_disposed)
                return false;

            DisposeCaptureResources();
            MonitorIndex = monitorIndex;

            try
            {
                int outputIndex;
                using (var adapter = GetOutputAdapter(monitorIndex, out outputIndex))
                {
                    if (adapter == null)
                        return false;

                    // 必须在 adapter 仍有效时创建设备；Device 会持有自己的 COM 引用。
                    _device = new Device(adapter, DeviceCreationFlags.BgraSupport);
                    _context = _device.ImmediateContext;
                    _adapterOutputIndex = outputIndex;
                    CreateDuplication();
                }

                Initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DXGICapture] Init: {ex.Message}");
                DisposeCaptureResources();
                return false;
            }
        }

        private void CreateDuplication()
        {
            ReleaseFrameNoThrow();
            _duplication?.Dispose();
            _duplication = null;

            using (var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>())
            using (var adapter = dxgiDevice.GetParent<Adapter>())
            using (var output = adapter.GetOutput(_adapterOutputIndex))
            using (var output1 = output.QueryInterface<Output1>())
            {
                _duplication = output1.DuplicateOutput(_device);
            }
        }

        /// <summary>
        /// 按 Screen.DeviceName 将显示器映射为所属 adapter 和 adapter 内的 output 索引。
        /// 返回的 adapter 由调用方释放；选中它之前不会在本方法中 Dispose。
        /// </summary>
        private Adapter GetOutputAdapter(int monitorIndex, out int adapterOutputIndex)
        {
            adapterOutputIndex = -1;
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
                return null;

            string targetDeviceName = screens[monitorIndex].DeviceName;
            using (var factory = new Factory1())
            {
                for (int ai = 0; ai < factory.GetAdapterCount1(); ai++)
                {
                    Adapter adapter = null;
                    try
                    {
                        adapter = factory.GetAdapter1(ai);
                        for (int oi = 0; oi < adapter.GetOutputCount(); oi++)
                        {
                            using (var output = adapter.GetOutput(oi))
                            {
                                var description = output.Description;
                                if (description.IsAttachedToDesktop &&
                                    string.Equals(description.DeviceName, targetDeviceName,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    adapterOutputIndex = oi;
                                    var selectedAdapter = adapter;
                                    adapter = null;
                                    return selectedAdapter;
                                }
                            }
                        }
                    }
                    finally
                    {
                        adapter?.Dispose();
                    }
                }
            }

            return null;
        }

        /// <summary>捕获下一帧</summary>
        public bool TryAcquireNextFrame(out byte[] pixels, out bool sizeChanged)
        {
            pixels = null;
            sizeChanged = false;

            if (!Initialized || _disposed)
                return false;

            Resource resource = null;
            bool acquiredThisCall = false;

            try
            {
                OutputDuplicateFrameInformation frameInfo;
                Result result = _duplication.TryAcquireNextFrame(100, out frameInfo, out resource);

                if (result.Failure)
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

                acquiredThisCall = true;
                _frameAcquired = true;
                if (resource == null)
                {
                    ReleaseFrameNoThrow();
                    return false;
                }

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
                    DataBox mapSource = default(DataBox);
                    bool mapped = false;
                    try
                    {
                        mapSource = _context.MapSubresource(
                            _stagingTex, 0, MapMode.Read, MapFlags.None);
                        mapped = true;

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
                        if (mapped)
                            _context.UnmapSubresource(_stagingTex, 0);
                    }
                }
            }
            catch (SharpDXException ex)
            {
                // Acquire 成功后，后续 Query/Copy/Map/Marshal 任一步异常都必须配对释放帧。
                bool released = !acquiredThisCall || ReleaseFrameNoThrow();
                if (!released ||
                    ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code)
                {
                    Reinitialize();
                }

                System.Diagnostics.Debug.WriteLine($"[DXGICapture] Acquire: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                if (acquiredThisCall && !ReleaseFrameNoThrow())
                    Reinitialize();

                System.Diagnostics.Debug.WriteLine($"[DXGICapture] Acquire: {ex.Message}");
                return false;
            }
            finally
            {
                resource?.Dispose();
            }
        }

        /// <summary>释放当前帧</summary>
        public void ReleaseFrame()
        {
            if (!ReleaseFrameNoThrow())
                Reinitialize();
        }

        private bool ReleaseFrameNoThrow()
        {
            if (!_frameAcquired)
                return true;

            _frameAcquired = false;
            try
            {
                _duplication?.ReleaseFrame();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DXGICapture] Release: {ex.Message}");
                return false;
            }
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
            if (_disposed)
                return;

            int monitorIndex = MonitorIndex;
            Initialize(monitorIndex);
        }

        private void DisposeCaptureResources()
        {
            Initialized = false;
            ReleaseFrameNoThrow();

            _stagingTex?.Dispose();
            _stagingTex = null;
            _duplication?.Dispose();
            _duplication = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;

            _adapterOutputIndex = -1;
            Width = 0;
            Height = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCaptureResources();
        }
    }
}
