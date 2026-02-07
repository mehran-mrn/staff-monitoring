using System;
using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct3D;
using MapFlags = Vortice.Direct3D11.MapFlags;
using Vortice.Mathematics;

namespace DxgiCapture
{
    public class ScreenCapture : IDisposable
    {
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _cachedStagingTex;
        private int _cachedWidth = 0;
        private int _cachedHeight = 0;
        private Format _cachedFormat = default;
        // When Desktop Duplication is unavailable, fall back to GDI capture
        private bool _useGdiFallback = false;
        private int _fallbackWidth = 0;
        private int _fallbackHeight = 0;

        public ScreenCapture()
        {
            Initialize();
        }

        private void Initialize()
        {
            var creationFlags = DeviceCreationFlags.BgraSupport;
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                creationFlags,
                Array.Empty<FeatureLevel>(),
                out _device,
                out _,
                out _context);

            if (_device == null || _context == null)
                throw new Exception("Failed to create D3D11 device/context.");

            // Stronger: iterate all DXGI adapters and outputs and attempt to DuplicateOutput
            // using a D3D11 device created for that adapter. This ensures we pair the
            // device and output correctly and avoid E_INVALIDARG on DuplicateOutput.
            var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            if (dxgiDevice == null) throw new Exception("Failed to get IDXGIDevice from D3D11 device");

            IDXGIFactory1? factory = null;
            try
            {
                factory = dxgiDevice.GetAdapter().GetParent<IDXGIFactory1>();
            }
            catch
            {
                factory = null;
            }

            bool found = false;
            if (factory != null)
            {
                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    var enumAdapterHr = factory.EnumAdapters1(adapterIndex, out var enumAdapter);
                    if (enumAdapterHr != 0 || enumAdapter == null) break;

                    try
                    {
                        for (uint outIdx = 0; ; outIdx++)
                        {
                            var enumOutHr = enumAdapter.EnumOutputs(outIdx, out var enumOutput);
                            if (enumOutHr != 0 || enumOutput == null) break;

                            try
                            {
                                var output1 = enumOutput.QueryInterface<IDXGIOutput1>();
                                if (output1 == null) continue;

                                // Create a D3D11 device bound to this adapter and try duplication
                                ID3D11Device? testDevice = null;
                                ID3D11DeviceContext? testContext = null;
                                try
                                {
                                    D3D11.D3D11CreateDevice(
                                        enumAdapter,
                                        DriverType.Unknown,
                                        DeviceCreationFlags.BgraSupport,
                                        Array.Empty<FeatureLevel>(),
                                        out testDevice,
                                        out _,
                                        out testContext);

                                    if (testDevice == null || testContext == null)
                                    {
                                        continue;
                                    }

                                    try
                                    {
                                        var dup = output1.DuplicateOutput(testDevice);
                                        if (dup != null)
                                        {
                                            // Record descriptive names for logging
                                            string adapterName = string.Empty;
                                            string outputName = string.Empty;
                                            try { adapterName = enumAdapter.Description.Description; } catch { }
                                            try { outputName = enumOutput.Description.DeviceName; } catch { }

                                            // Success: adopt this device/context/duplication
                                            try { _device?.Dispose(); } catch { }
                                            try { _context?.Dispose(); } catch { }
                                            _device = testDevice;
                                            _context = testContext;
                                            _duplication = dup;
                                            found = true;
                                            Console.WriteLine($"[ScreenCapture] Using adapter='{adapterName}', output='{outputName}' for duplication.");
                                            output1.Dispose();
                                            break;
                                        }
                                    }
                                    catch
                                    {
                                        // DuplicateOutput failed for this output; continue searching
                                        try { testContext.Dispose(); } catch { }
                                        try { testDevice.Dispose(); } catch { }
                                        continue;
                                    }
                                }
                                finally
                                {
                                    if (!found)
                                    {
                                        try { testContext?.Dispose(); } catch { }
                                        try { testDevice?.Dispose(); } catch { }
                                    }
                                }
                            }
                            finally
                            {
                                try { enumOutput.Dispose(); } catch { }
                            }
                        }
                    }
                    finally
                    {
                        try { enumAdapter.Dispose(); } catch { }
                    }

                    if (found) break;
                }
            }

            try { factory?.Dispose(); } catch { }
            try { dxgiDevice.Dispose(); } catch { }

            if (!found)
            {
                // Fallback: attempt duplication using the original device's adapter/output as a last resort
                try
                {
                    var fallbackAdapter = _device.QueryInterface<IDXGIDevice>()?.GetAdapter();
                    var fallbackOutput = fallbackAdapter?.EnumOutputs(0, out var tmpOut) == 0 ? tmpOut : null;
                    if (fallbackOutput != null)
                    {
                        var out1 = fallbackOutput.QueryInterface<IDXGIOutput1>();
                        _duplication = out1.DuplicateOutput(_device);
                        out1.Dispose();
                        try { fallbackOutput.Dispose(); } catch { }
                        try { fallbackAdapter.Dispose(); } catch { }
                        found = _duplication != null;
                    }
                }
                catch (Exception ex)
                {
                    // swallow here and throw below with descriptive message
                }
            }

            if (!found || _duplication == null)
            {
                // Desktop Duplication unavailable on this system/configuration (common with mirrored/duplicate outputs).
                // Fall back to a GDI-based screen capture so the app can continue running on single-display or duplicate setups.
                try
                {
                    _useGdiFallback = true;
                    _fallbackWidth = GetSystemMetrics(0); // SM_CXSCREEN
                    _fallbackHeight = GetSystemMetrics(1); // SM_CYSCREEN
                    Console.WriteLine($"[ScreenCapture] Desktop Duplication not available; falling back to GDI capture ({_fallbackWidth}x{_fallbackHeight}).");
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to find a suitable adapter/output for Desktop Duplication (DuplicateOutput failed on all adapters/outputs) and GDI fallback initialization failed: " + ex.Message);
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        /// <summary>
        /// GDI fallback capture using CopyFromScreen -> returns ARGB bytes (Format32bppArgb)
        /// </summary>
        private bool TryCaptureGdi(int timeoutMs, out byte[]? argbCopy, out int stride, out int width, out int height)
        {
            argbCopy = null; stride = 0; width = 0; height = 0;
            try
            {
                width = _fallbackWidth;
                height = _fallbackHeight;
                if (width <= 0 || height <= 0) return false;

                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
                }

                var rect = new System.Drawing.Rectangle(0, 0, width, height);
                var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = Math.Abs(bmpData.Stride) * height;
                    argbCopy = new byte[bytes];
                    System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, argbCopy, 0, bytes);
                    stride = bmpData.Stride;
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Try to acquire the next desktop frame. Returns true and the ARGB bytes if a new frame was captured.
        /// </summary>
        public bool TryCaptureFrame(int timeoutMs, out byte[]? argbCopy, out int stride, out int width, out int height)
        {
            argbCopy = null; stride = 0; width = 0; height = 0;
            if (_duplication == null)
            {
                if (_useGdiFallback)
                {
                    return TryCaptureGdi(timeoutMs, out argbCopy, out stride, out width, out height);
                }
                return false;
            }
            if (_device == null || _context == null) return false;

            IDXGIResource? desktopResource = null;
            bool frameAcquired = false;
            bool released = false;

            try
            {
                var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out desktopResource);
                if (result != 0) return false;
                frameAcquired = true;

                bool hasChanges = frameInfo.AccumulatedFrames > 0 || frameInfo.TotalMetadataBufferSize > 0 || frameInfo.LastPresentTime != 0;
                if (!hasChanges)
                {
                    try { _duplication.ReleaseFrame(); released = true; } catch { }
                    desktopResource?.Dispose();
                    desktopResource = null;
                    return false;
                }

                var desktopTexture = desktopResource?.QueryInterface<ID3D11Texture2D>();
                if (desktopTexture == null)
                {
                    try { _duplication.ReleaseFrame(); } catch { }
                    desktopResource?.Dispose();
                    return false;
                }
                try
                {
                    var desc = desktopTexture.Description;

                    var stagingDesc = new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = desc.Format,
                        SampleDescription = desc.SampleDescription,
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };

                    if (_cachedStagingTex == null || _cachedWidth != (int)desc.Width || _cachedHeight != (int)desc.Height || _cachedFormat != desc.Format)
                    {
                        try { _cachedStagingTex?.Dispose(); } catch { }
                        _cachedStagingTex = _device.CreateTexture2D(stagingDesc);
                        _cachedWidth = (int)desc.Width;
                        _cachedHeight = (int)desc.Height;
                        _cachedFormat = desc.Format;
                    }

                    var stagingTex = _cachedStagingTex!;
                    _context.CopyResource(stagingTex, desktopTexture);
                    var mapped = _context.Map(stagingTex, 0, MapMode.Read, MapFlags.None);

                    try
                    {
                        width = (int)desc.Width;
                        height = (int)desc.Height;
                        int srcRowPitch = (int)mapped.RowPitch;
                        IntPtr srcPtr = mapped.DataPointer;

                        int srcSize = srcRowPitch * height;
                        var srcBytes = ArrayPool<byte>.Shared.Rent(srcSize);
                        try
                        {
                            Marshal.Copy(srcPtr, srcBytes, 0, srcSize);

                            argbCopy = new byte[srcSize];
                            Buffer.BlockCopy(srcBytes, 0, argbCopy, 0, srcSize);
                            stride = srcRowPitch;
                        }
                        finally
                        {
                            try { ArrayPool<byte>.Shared.Return(srcBytes); } catch { }
                        }
                    }
                    finally
                    {
                        _context.Unmap(stagingTex, 0);
                    }
                }
                finally
                {
                    desktopTexture.Dispose();
                }

                try { _duplication.ReleaseFrame(); released = true; } catch { }
                desktopResource?.Dispose();
                desktopResource = null;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (frameAcquired && !released)
                {
                    try { _duplication.ReleaseFrame(); } catch { }
                }
                try { desktopResource?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _duplication?.Dispose(); } catch { }
            try { _context?.ClearState(); _context?.Flush(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            try { _cachedStagingTex?.Dispose(); } catch { }
        }
    }
}
