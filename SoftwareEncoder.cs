using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Linq;
using Vortice.MediaFoundation;

namespace DxgiCapture
{
    // Software H264 encoder using Media Foundation MFTs (scaffold).
    // TODO: implement full IMFTransform enumeration and ProcessInput/ProcessOutput flow.
    // The current scaffold provides the public API and logs, and will be extended to
    // perform actual encoding to Annex-B H.264 NALs.
    public class SoftwareEncoder : IDisposable
    {
        private int _width;
        private int _height;
        private int _fps;
        private int _bitrate;
        private bool _initialized = false;
        private IMFActivate? _chosenActivate;
        private IMFTransform? _transform;
        private NvencEncoder? _nvencEncoder;
        public NvencEncoder? NvencEncoder => _nvencEncoder;
        private FfmpegEncoder? _ffmpegFallback;
        public FfmpegEncoder? FfmpegFallback => _ffmpegFallback;
        private byte[]? _cachedSpsPpsAnnexB;

        public SoftwareEncoder()
        {
        }

        // Public getter that returns Base64-encoded avcC (AVCC) codec private data constructed
        // from cached SPS/PPS Annex-B NALs, or null if not available.
        public string? CachedAvcCBase64
        {
            get
            {
                try
                {
                    var avcc = BuildAvcCFromCachedSpsPps();
                    if (avcc == null) return null;
                    return Convert.ToBase64String(avcc);
                }
                catch { return null; }
            }
        }

        // Base64 of raw SPS NAL (without start code)
        public string? CachedSpsBase64
        {
            get
            {
                var t = GetCachedSpsPpsRaw();
                if (t.sps == null) return null;
                return Convert.ToBase64String(t.sps);
            }
        }

        // Base64 of raw PPS NAL (without start code)
        public string? CachedPpsBase64
        {
            get
            {
                var t = GetCachedSpsPpsRaw();
                if (t.pps == null) return null;
                return Convert.ToBase64String(t.pps);
            }
        }

        private (byte[]? sps, byte[]? pps) GetCachedSpsPpsRaw()
        {
            try
            {
                if (_cachedSpsPpsAnnexB == null) return (null, null);
                int offset = 0;
                byte[]? sps = null;
                byte[]? pps = null;
                while (offset + 4 <= _cachedSpsPpsAnnexB.Length)
                {
                    int start = -1;
                    for (int i = offset; i + 3 < _cachedSpsPpsAnnexB.Length; ++i)
                    {
                        if (_cachedSpsPpsAnnexB[i] == 0 && _cachedSpsPpsAnnexB[i + 1] == 0 && _cachedSpsPpsAnnexB[i + 2] == 0 && _cachedSpsPpsAnnexB[i + 3] == 1)
                        {
                            start = i + 4;
                            break;
                        }
                    }
                    if (start < 0) break;
                    int next = -1;
                    for (int j = start; j + 3 < _cachedSpsPpsAnnexB.Length; ++j)
                    {
                        if (_cachedSpsPpsAnnexB[j] == 0 && _cachedSpsPpsAnnexB[j + 1] == 0 && _cachedSpsPpsAnnexB[j + 2] == 0 && _cachedSpsPpsAnnexB[j + 3] == 1)
                        {
                            next = j;
                            break;
                        }
                    }
                    int nalEnd = (next >= 0) ? next : _cachedSpsPpsAnnexB.Length;
                    int nalLen = nalEnd - start;
                    if (nalLen <= 0) break;
                    int nalType = _cachedSpsPpsAnnexB[start] & 0x1F;
                    var raw = new byte[nalLen];
                    Array.Copy(_cachedSpsPpsAnnexB, start, raw, 0, nalLen);
                    if (nalType == 7 && sps == null) sps = raw;
                    else if (nalType == 8 && pps == null) pps = raw;
                    if (next >= 0) offset = next; else break;
                }
                return (sps, pps);
            }
            catch { return (null, null); }
        }

        // Initialize the software encoder MFT with NV12 input and H264 output.
        public Task InitializeAsync(int width, int height, int fps, int bitrate)
        {
            _width = width; _height = height; _fps = fps; _bitrate = bitrate;

            // Try NVENC first (if on Windows)
            try
            {
                _nvencEncoder = new NvencEncoder();
                _nvencEncoder.InitializeAsync(_width, _height, _fps, _bitrate).GetAwaiter().GetResult();
                //Console.WriteLine("[SoftwareEncoder] NVENC encoder initialized and will be used for H.264 encoding.");
                _initialized = true;
                return Task.CompletedTask;
            }
            catch (Exception)
            {
                //Console.WriteLine($"[SoftwareEncoder] NVENC encoder unavailable or failed to initialize: {ex.Message}");
                _nvencEncoder = null;
            }

            Console.WriteLine($"[SoftwareEncoder] Initializing MFT enumeration for {_width}x{_height}@{_fps}fps, target bitrate={_bitrate}");
            try
            {
                // Prefer hardware MFTs first, then fall back to software.
                var mftCategoryVideoEncoder = new Guid("F79EAC7D-E545-4387-BDEE-D647D7BDE42A"); // MFT_CATEGORY_VIDEO_ENCODER

                // --- Deep Diagnostics: Enumerate all H.264 encoder MFTs (hardware and software) ---
                void PrintMftList(string label, IMFActivate?[]? activates)
                {
                    if (activates == null || activates.Length == 0)
                    {
                        //Console.WriteLine($"  [Diagnostics] No {label} MFTs found.");
                        return;
                    }
                    //Console.WriteLine($"  [Diagnostics] {label} MFTs found: {activates.Length}");
                    for (int i = 0; i < activates.Length; ++i)
                    {
                        var a = activates[i];
                        if (a == null) { /*Console.WriteLine($"    {i}: <null>");*/ continue; }
                        string? name = null;
                        string? clsid = null;
                        try
                        {
                            dynamic da = a;
                            // Try to get friendly name (MF_TRANSFORM_FRIENDLY_NAME_Attribute)
                            var attrType = a.GetType();
                            var guidName = new Guid("52656E49-6F6E-4091-8F3B-48E7B4C1CFFC");
                            // Try GetString(Guid)
                            try { name = da.GetString(guidName); }
                            catch
                            {
                                // Try GetString(object)
                                try { name = da.GetString((object)guidName); } catch { }
                            }
                            // Try to get CLSID (MF_TRANSFORM_CLSID_Attribute)
                            var guidClsid = new Guid("6821C42B-65A4-4E82-99BC-9A88205ECD0C");
                            // Try GetGUID(Guid)
                            try { clsid = da.GetGUID(guidClsid).ToString(); }
                            catch
                            {
                                // Try GetGUID(object)
                                try { clsid = da.GetGUID((object)guidClsid).ToString(); } catch { }
                            }
                        }
                        catch { }
                        //Console.WriteLine($"    {i}: Name = '{name ?? "<unknown>"}', CLSID = {clsid ?? "<unknown>"}");
                    }
                }

                // Try hardware encoders
                //Console.WriteLine("Enumerating hardware H.264 encoders via MFTEnumEx...");
                IMFActivate?[]? hwActivates = null;
                try
                {
                    // MFTEnumExFlags.Hardware == 0x00000001
                    nint pActivates = nint.Zero;
                    uint activateCount = 0;
                    MediaFactory.MFTEnumEx(mftCategoryVideoEncoder, 0x00000001u, null, null, out pActivates, out activateCount);
                    if (activateCount > 0 && pActivates != nint.Zero)
                    {
                        hwActivates = new IMFActivate?[activateCount];
                        for (uint i = 0; i < activateCount; ++i)
                        {
                            var ptr = Marshal.ReadIntPtr((IntPtr)pActivates, (int)(i * (uint)IntPtr.Size));
                            if (ptr != IntPtr.Zero)
                            {
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                {
                                    hwActivates[i] = (IMFActivate?)Marshal.GetObjectForIUnknown(ptr);
                                }
                                else
                                {
                                    hwActivates[i] = null;
                                }
                            }
                        }
                        Marshal.FreeCoTaskMem((IntPtr)pActivates);
                    }
                }
                catch (Exception)
                {
                    hwActivates = null;
                }


                IMFActivate? chosen = null;
                PrintMftList("Hardware H.264 Encoder", hwActivates);
                if (hwActivates != null && hwActivates.Length > 0)
                {
                    foreach (var a in hwActivates)
                    {
                        if (a != null) { chosen = a; break; }
                    }
                    //Console.WriteLine($"Selected hardware MFT (count={hwActivates.Length})");
                }

                IMFActivate?[]? swActivates = null;
                if (chosen == null)
                {
                    //Console.WriteLine("No hardware MFT found; enumerating software H.264 encoders...");
                    try
                    {
                        nint pActivates = nint.Zero;
                        uint activateCount = 0;
                        MediaFactory.MFTEnumEx(mftCategoryVideoEncoder, 0u, null, null, out pActivates, out activateCount);
                        if (activateCount > 0 && pActivates != nint.Zero)
                        {
                            swActivates = new IMFActivate?[activateCount];
                            for (uint i = 0; i < activateCount; ++i)
                            {
                                var ptr = Marshal.ReadIntPtr((IntPtr)pActivates, (int)(i * (uint)IntPtr.Size));
                                if (ptr != IntPtr.Zero)
                                {
                                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    {
                                        swActivates[i] = (IMFActivate?)Marshal.GetObjectForIUnknown(ptr);
                                    }
                                    else
                                    {
                                        swActivates[i] = null;
                                    }
                                }
                            }
                            Marshal.FreeCoTaskMem((IntPtr)pActivates);
                        }
                    }
                    catch (Exception)
                    {
                        swActivates = null;
                    }

                    PrintMftList("Software H.264 Encoder", swActivates);

                    if (swActivates != null && swActivates.Length > 0)
                    {
                        // Prefer Microsoft software encoder if present
                        foreach (var a in swActivates)
                        {
                            try
                            {
                                IntPtr pTransform = IntPtr.Zero;
                                //Console.WriteLine($"Found software MFT activate object: {a}");
                                if (a != null && chosen == null) chosen = a;
                            }
                            catch { }
                        }

                        if (chosen == null)
                        {
                            foreach (var a in swActivates)
                            {
                                if (a != null) { chosen = a; break; }
                            }
                        }
                    }
                }

                if (chosen != null)
                {
                    try
                    {
                        //Console.WriteLine($"Chosen MFT activate object: {chosen}");
                        _chosenActivate = chosen;

                        // Try to set low-latency attribute on the IMFActivate if possible (best-effort).
                        try
                        {
                            // SinkWriterAttributeKeys.LowLatency exists in Vortice and maps to MF_LOW_LATENCY
                            var lowKey = SinkWriterAttributeKeys.LowLatency;
                            try
                            {
                                // Try dynamic Set
                                dynamic da = _chosenActivate;
                                try { da.Set(lowKey, (uint)1); }
                                catch { }
                            }
                            catch { }
                            try
                            {
                                // Try casting to IMFAttributes
                                if (_chosenActivate is IMFAttributes attrs)
                                {
                                    attrs.Set(lowKey, (uint)1);
                                    //Console.WriteLine("Set LowLatency attribute on IMFActivate (IMFAttributes.Set)");
                                }
                            }
                            catch { }
                        }
                        catch (Exception)
                        {
                            //Console.WriteLine($"Warning: could not set LowLatency on IMFActivate: {ex.Message}");
                        }

                        // Attempt to activate an IMFTransform from the IMFActivate using the standard Vortice signature
                        try
                        {
                            IntPtr pTransform = IntPtr.Zero;
                            try
                            {
                                // Try several activation patterns (use dynamic to avoid compile-time signature assumptions)
                                dynamic dd = chosen;
                                try
                                {
                                    // Try common signature ActivateObject(Guid, out IntPtr)
                                    dd.ActivateObject(typeof(IMFTransform).GUID, out pTransform);
                                }
                                catch { }

                                try
                                {
                                    // Try parameterless ActivateObject() returning an object
                                    var created = dd.ActivateObject();
                                    if (created is IMFTransform tf) _transform = tf;
                                }
                                catch { }

                                try
                                {
                                    // Try ActivateObject(Guid) returning an object
                                    var created2 = dd.ActivateObject(typeof(IMFTransform).GUID);
                                    if (created2 is IMFTransform tf2) _transform = tf2;
                                }
                                catch { }

                                if (pTransform != IntPtr.Zero && _transform == null)
                                {
                                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    {
                                        _transform = (IMFTransform)Marshal.GetObjectForIUnknown(pTransform);
                                    }
                                    Marshal.Release(pTransform);
                                }
                            }
                            catch { }

                            // If we have a transform, configure input/output media types
                            if (_transform != null)
                            {
                                // Create output media type (H264)
                                var outMediaType = MediaFactory.MFCreateMediaType();
                                outMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                                outMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                                outMediaType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrate);
                                // Prefer baseline profile (eAVEncH264VProfile_Base == 66)
                                try
                                {
                                    outMediaType.Set(TransformAttributeKeys.MftPreferredEncoderProfile, (uint)66);
                                }
                                catch { }
                                MediaFactory.MFSetAttributeSize(outMediaType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
                                MediaFactory.MFSetAttributeRatio(outMediaType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);

                                // Create input media type (NV12)
                                var inMediaType = MediaFactory.MFCreateMediaType();
                                inMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                                inMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                                MediaFactory.MFSetAttributeSize(inMediaType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
                                MediaFactory.MFSetAttributeRatio(inMediaType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
                                inMediaType.Set(MediaTypeAttributeKeys.SampleSize, (uint)(_width * _height + (_width * _height) / 2));
                                inMediaType.Set(MediaTypeAttributeKeys.DefaultStride, _width);

                                try
                                {
                                    _transform.SetOutputType(0, outMediaType, 0);
                                }
                                catch (Exception)
                                {
                                    //Console.WriteLine($"Warning: SetOutputType failed: {ex.Message}");
                                }
                                try
                                {
                                    _transform.SetInputType(0, inMediaType, 0);
                                }
                                catch (Exception)
                                {
                                    //Console.WriteLine($"Warning: SetInputType failed: {ex.Message}");
                                }

                                // Best-effort: Query ICodecAPI on the activated IMFTransform and set
                                // CODECAPI_AVLowLatencyMode = TRUE if supported.
                                try
                                {
                                    TrySetCodecApiLowLatency(_transform);
                                }
                                catch (Exception)
                                {
                                    //Console.WriteLine($"Warning: TrySetCodecApiLowLatency failed: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception)
                        {
                            //Console.WriteLine($"Warning: failed to ActivateObject to IMFTransform: {ex.Message}");
                        }
                    }
                    catch { /*Console.WriteLine("Chosen MFT selected (unable to read friendly name)");*/ }
                }
                else
                {
                    //Console.WriteLine("No suitable H.264 encoder MFT found on system.");
                    try
                    {
                        _ffmpegFallback = new FfmpegEncoder();
                        _ffmpegFallback.InitializeAsync(_width, _height, _fps, _bitrate).GetAwaiter().GetResult();
                        //Console.WriteLine("FFmpeg (libx264) fallback initialized (requires FFmpeg native DLLs at runtime).");
                    }
                    catch (Exception ex)
                    {
                        //Console.WriteLine($"[SoftwareEncoder] FFmpeg fallback init failed: {ex}");
                        _ffmpegFallback = null;
                    }
                }

                // NOTE: actual IMFTransform instantiation and media-type configuration
                // will be implemented in the next step (ProcessInput/ProcessOutput flow).
                _initialized = true;
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SoftwareEncoder.InitializeAsync failed during MFT enumeration: {ex}");
                throw;
            }
        }

        // Accept an NV12 frame buffer and return encoded H.264 Annex-B NALs (byte[]), or null if none.
        public Task<byte[]?> EncodeNv12FrameAsync(byte[] nv12Buffer, long timestampMs)
        {
            //Console.WriteLine($"[SoftwareEncoder] EncodeNv12FrameAsync called: buffer_len={nv12Buffer.Length}, ts={timestampMs}, initialized={_initialized}");
            
            if (!_initialized) throw new InvalidOperationException("SoftwareEncoder not initialized");

            // Prefer NVENC if available
            if (_nvencEncoder != null)
            {
                try
                {
                    //Console.WriteLine("[SoftwareEncoder] Attempting NVENC encode...");
                    var encoded = _nvencEncoder.EncodeNv12FrameAsync(nv12Buffer, timestampMs).GetAwaiter().GetResult();
                    //Console.WriteLine($"[SoftwareEncoder] NVENC encode succeeded: {encoded?.Length ?? 0} bytes");
                    return Task.FromResult<byte[]?>(encoded);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SoftwareEncoder] NVENC encode failed: {ex.Message}");
                    // If NVENC fails, fall through to other encoders
                }
            }

            if (_transform == null)
            {
                // If we don't have an IMFTransform, try x264 fallback if available.
                if (_ffmpegFallback != null)
                {
                    try
                    {
                        //Console.WriteLine("[SoftwareEncoder] Attempting FFmpeg fallback encode...");
                        var encoded = _ffmpegFallback.EncodeNv12FrameAsync(nv12Buffer, timestampMs).GetAwaiter().GetResult();
                        //Console.WriteLine($"[SoftwareEncoder] FFmpeg encode succeeded: {encoded?.Length ?? 0} bytes");
                        return Task.FromResult<byte[]?>(encoded);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SoftwareEncoder] FFmpeg fallback encode failed: {ex.Message}");
                        return Task.FromResult<byte[]?>(null);
                    }
                }

                //Console.WriteLine("[SoftwareEncoder] No IMFTransform is activated; cannot encode");
                return Task.FromResult<byte[]?>(null);
            }

            try
            {
                // Create MF memory buffer and sample (use the same APIs as Mp4Writer)
                int totalBytes = _width * _height + (_width * _height) / 2;
                var buffer = MediaFactory.MFCreateMemoryBuffer(totalBytes);
                try
                {
                    buffer.Lock(out IntPtr destPtr, out int maxLen, out int curLen);
                    try
                    {
                        Marshal.Copy(nv12Buffer, 0, destPtr, Math.Min(nv12Buffer.Length, totalBytes));
                        buffer.CurrentLength = Math.Min(nv12Buffer.Length, totalBytes);
                    }
                    finally { buffer.Unlock(); }

                    var sample = MediaFactory.MFCreateSample();
                    try
                    {
                        sample.AddBuffer(buffer);
                        sample.SampleTime = timestampMs * 10000;
                        sample.SampleDuration = 10000000 / Math.Max(1, _fps);

                        // Feed to transform
                        try
                        {
                            _transform.ProcessInput(0, sample, 0);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SoftwareEncoder] ProcessInput failed: {ex.Message}");
                        }

                        // Prepare an output sample to receive encoded data
                        var outSample = MediaFactory.MFCreateSample();
                        var outBuf = MediaFactory.MFCreateMemoryBuffer(1 << 16);
                        outSample.AddBuffer(outBuf);

                        // Build MFTOutputDataBuffer dynamically (reflection) to call ProcessOutput
                        var transformType = _transform.GetType();
                        var asm = typeof(IMFTransform).Assembly;
                        var outputStructType = asm.GetType("Vortice.MediaFoundation.MFTOutputDataBuffer");
                        object? encodedBytes = null;

                        if (outputStructType != null)
                        {
                            var outStruct = Activator.CreateInstance(outputStructType);
                            // set fields: dwStreamID, pSample, dwStatus, pEvents
                            var f_dwStreamID = outputStructType.GetField("dwStreamID");
                            var f_pSample = outputStructType.GetField("pSample");
                            var f_dwStatus = outputStructType.GetField("dwStatus");
                            if (f_dwStreamID != null) f_dwStreamID.SetValue(outStruct, 0);
                            if (f_pSample != null) f_pSample.SetValue(outStruct, outSample);
                            if (f_dwStatus != null) f_dwStatus.SetValue(outStruct, 0u);

                            var arr = Array.CreateInstance(outputStructType, 1);
                            arr.SetValue(outStruct, 0);

                            // Find ProcessOutput method
                            var mi = transformType.GetMethod("ProcessOutput");
                            if (mi != null)
                            {
                                object?[] args = new object?[] { 0, 1, arr, null };
                                var result = mi.Invoke(_transform, args);
                                int hr = -1;
                                try { hr = (int)result!; } catch { }

                                // Retrieve sample from struct
                                var returnedStruct = arr.GetValue(0);
                                IMFSample? outSampleReturned = null;
                                if (returnedStruct != null && f_pSample != null)
                                {
                                    outSampleReturned = f_pSample.GetValue(returnedStruct) as IMFSample;
                                }

                                if (outSampleReturned != null)
                                {
                                    // Convert to contiguous buffer and read bytes
                                    IMFSample s = outSampleReturned;
                                    try
                                    {
                                        // Use reflection to support different Vortice signatures for ConvertToContiguousBuffer
                                        var sType = s.GetType();
                                        var miConvert = sType.GetMethod("ConvertToContiguousBuffer");
                                        IMFMediaBuffer? outMb = null;
                                        if (miConvert != null)
                                        {
                                            var rv = miConvert.Invoke(s, null);
                                            if (rv is IMFMediaBuffer mb)
                                            {
                                                outMb = mb;
                                            }
                                        }
                                        else
                                        {
                                            // Try GetBufferByIndex(0, out IMFMediaBuffer)
                                            var miGet = sType.GetMethod("GetBufferByIndex");
                                            if (miGet != null)
                                            {
                                                object?[] args2 = new object?[] { 0, null };
                                                miGet.Invoke(s, args2);
                                                if (args2[1] is IMFMediaBuffer mb2) outMb = mb2;
                                            }
                                        }

                                        if (outMb != null)
                                        {
                                            outMb.Lock(out IntPtr oPtr, out int oMax, out int oCur);
                                            try
                                            {
                                                byte[] encoded = new byte[oCur];
                                                Marshal.Copy(oPtr, encoded, 0, oCur);
                                                encodedBytes = ConvertAvccToAnnexB(encoded);
                                            }
                                            finally { outMb.Unlock(); outMb.Dispose(); }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[SoftwareEncoder] Failed to read output sample: {ex.Message}");
                                    }
                                }
                            }
                        }

                        if (encodedBytes is byte[] res)
                        {
                            // Try extracting SPS/PPS from Annex-B encoded data and cache them for signaling
                            try
                            {
                                ExtractAndCacheSpsPpsFromAnnexB(res);
                            }
                            catch { }
                            return Task.FromResult<byte[]?>(res);
                        }
                        return Task.FromResult<byte[]?>(null);
                    }
                    finally { /* sample.Dispose? leave for GC */ }
                }
                finally { /* buffer.Dispose? left to GC */ }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SoftwareEncoder] Encode error: {ex}");
                return Task.FromResult<byte[]?>(null);
            }
        }

        private static byte[]? ConvertAvccToAnnexB(byte[] avcc)
        {
            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    int offset = 0;
                    while (offset + 4 <= avcc.Length)
                    {
                        // first 4 bytes: length (big-endian)
                        int nalLen = (avcc[offset] << 24) | (avcc[offset + 1] << 16) | (avcc[offset + 2] << 8) | avcc[offset + 3];
                        offset += 4;
                        if (nalLen <= 0 || offset + nalLen > avcc.Length) break;
                        // write start code
                        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 }, 0, 4);
                        ms.Write(avcc, offset, nalLen);
                        offset += nalLen;
                    }
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        private void ExtractAndCacheSpsPpsFromAnnexB(byte[] annexb)
        {
            try
            {
                int offset = 0;
                System.Collections.Generic.List<byte[]> sps = new System.Collections.Generic.List<byte[]>();
                System.Collections.Generic.List<byte[]> pps = new System.Collections.Generic.List<byte[]>();
                while (offset + 4 <= annexb.Length)
                {
                    // find next start code
                    int start = -1;
                    for (int i = offset; i + 3 < annexb.Length; ++i)
                    {
                        if (annexb[i] == 0x00 && annexb[i + 1] == 0x00 && annexb[i + 2] == 0x00 && annexb[i + 3] == 0x01)
                        {
                            start = i + 4;
                            offset = start;
                            break;
                        }
                    }
                    if (start < 0) break;

                    // find next start code
                    int next = -1;
                    for (int j = start; j + 3 < annexb.Length; ++j)
                    {
                        if (annexb[j] == 0x00 && annexb[j + 1] == 0x00 && annexb[j + 2] == 0x00 && annexb[j + 3] == 0x01)
                        {
                            next = j;
                            break;
                        }
                    }
                    int nalEnd = (next >= 0) ? next : annexb.Length;
                    int nalLen = nalEnd - start;
                    if (nalLen <= 0) break;
                    byte nalHeader = annexb[start];
                    int nalType = nalHeader & 0x1F;
                    var nalData = new byte[nalLen + 4];
                    // include start code
                    nalData[0] = 0; nalData[1] = 0; nalData[2] = 0; nalData[3] = 1;
                    Array.Copy(annexb, start, nalData, 4, nalLen);

                    if (nalType == 7) sps.Add(nalData);
                    else if (nalType == 8) pps.Add(nalData);

                    if (next >= 0) offset = next;
                    else break;
                }

                if (sps.Count > 0 || pps.Count > 0)
                {
                    using (var ms = new System.IO.MemoryStream())
                    {
                        foreach (var b in sps) ms.Write(b, 0, b.Length);
                        foreach (var b in pps) ms.Write(b, 0, b.Length);
                        _cachedSpsPpsAnnexB = ms.ToArray();
                    }
                    //Console.WriteLine($"[SoftwareEncoder] Cached SPS/PPS Annex-B (SPS={sps.Count}, PPS={pps.Count})");
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"[SoftwareEncoder] Failed to extract SPS/PPS: {ex.Message}");
            }
        }

        private byte[]? BuildAvcCFromCachedSpsPps()
        {
            try
            {
                if (_cachedSpsPpsAnnexB == null || _cachedSpsPpsAnnexB.Length == 0) return null;
                // Parse annex-b concatenation to extract first SPS and PPS raw (without start codes)
                int offset = 0;
                byte[]? sps = null;
                byte[]? pps = null;
                while (offset + 4 <= _cachedSpsPpsAnnexB.Length)
                {
                    // find start code
                    int start = -1;
                    for (int i = offset; i + 3 < _cachedSpsPpsAnnexB.Length; ++i)
                    {
                        if (_cachedSpsPpsAnnexB[i] == 0 && _cachedSpsPpsAnnexB[i + 1] == 0 && _cachedSpsPpsAnnexB[i + 2] == 0 && _cachedSpsPpsAnnexB[i + 3] == 1)
                        {
                            start = i + 4;
                            break;
                        }
                    }
                    if (start < 0) break;
                    int next = -1;
                    for (int j = start; j + 3 < _cachedSpsPpsAnnexB.Length; ++j)
                    {
                        if (_cachedSpsPpsAnnexB[j] == 0 && _cachedSpsPpsAnnexB[j + 1] == 0 && _cachedSpsPpsAnnexB[j + 2] == 0 && _cachedSpsPpsAnnexB[j + 3] == 1)
                        {
                            next = j;
                            break;
                        }
                    }
                    int nalEnd = (next >= 0) ? next : _cachedSpsPpsAnnexB.Length;
                    int nalLen = nalEnd - start;
                    if (nalLen <= 0) break;
                    int nalType = _cachedSpsPpsAnnexB[start] & 0x1F;
                    var raw = new byte[nalLen];
                    Array.Copy(_cachedSpsPpsAnnexB, start, raw, 0, nalLen);
                    if (nalType == 7 && sps == null) sps = raw;
                    else if (nalType == 8 && pps == null) pps = raw;
                    if (next >= 0) offset = next; else break;
                }

                if (sps == null || pps == null) return null;

                // Parse profile/compat/level from SPS (raw includes nal header at [0])
                // SPS raw: [nal_header, profile_idc, constraint_set_flags, level_idc, ...]
                byte profile = 0;
                byte compat = 0;
                byte level = 0;
                if (sps.Length >= 4)
                {
                    profile = sps[1];
                    compat = sps[2];
                    level = sps[3];
                }

                using (var ms = new System.IO.MemoryStream())
                {
                    // configurationVersion
                    ms.WriteByte(0x01);
                    ms.WriteByte(profile);
                    ms.WriteByte(compat);
                    ms.WriteByte(level);
                    // lengthSizeMinusOne: 6 bits set + (3) to indicate 4-byte lengths
                    ms.WriteByte(0xFF);
                    // numOfSequenceParameterSets (0xE1 | count)
                    ms.WriteByte(0xE0 | (byte)1);
                    // SPS length (2 bytes)
                    ms.WriteByte((byte)((sps.Length >> 8) & 0xFF));
                    ms.WriteByte((byte)(sps.Length & 0xFF));
                    ms.Write(sps, 0, sps.Length);
                    // PPS count
                    ms.WriteByte(1);
                    ms.WriteByte((byte)((pps.Length >> 8) & 0xFF));
                    ms.WriteByte((byte)(pps.Length & 0xFF));
                    ms.Write(pps, 0, pps.Length);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            // Cleanup FFmpeg fallback encoder
            try
            {
                _ffmpegFallback?.Dispose();
                _ffmpegFallback = null;
            }
            catch { }
            
            // Cleanup NVENC encoder
            try
            {
                _nvencEncoder?.Dispose();
                _nvencEncoder = null;
            }
            catch { }
            
            // TODO: release MFT resources
            _initialized = false;
            //Console.WriteLine("[SoftwareEncoder] Disposed");
        }

        // Small DTO for SDP fmtp parameters (sprop-parameter-sets)
        public record SdpFmtpParameters(string? SpsBase64, string? PpsBase64, string? SpropParameterSet);

        // Returns SPS/PPS Base64 and a ready-to-use sprop-parameter-sets value, or nulls if not available.
        public SdpFmtpParameters GetSdpFmtpParameters()
        {
            var sps = CachedSpsBase64;
            var pps = CachedPpsBase64;
            string? sprop = null;
            if (!string.IsNullOrEmpty(sps) && !string.IsNullOrEmpty(pps)) sprop = $"{sps},{pps}";
            return new SdpFmtpParameters(sps, pps, sprop);
        }

        // Try to obtain ICodecAPI from the active IMFTransform and set CODECAPI_AVLowLatencyMode to TRUE (best-effort).
        private void TrySetCodecApiLowLatency(IMFTransform transform)
        {
            if (transform == null) return;
            try
            {
                var asm = typeof(IMFTransform).Assembly;

                // Attempt to find a Guid constant in the Vortice assembly named with 'LowLatency' (best-effort).
                Guid avLowGuid = Guid.Empty;
                foreach (var t in asm.GetTypes())
                {
                    var fields = t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    foreach (var f in fields)
                    {
                        if (f.FieldType == typeof(Guid) && f.Name.IndexOf("LowLatency", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var val = f.GetValue(null);
                            if (val is Guid g && g != Guid.Empty) { avLowGuid = g; break; }
                        }
                    }
                    if (avLowGuid != Guid.Empty) break;
                }

                // If we couldn't find a GUID, leave (best-effort only).
                if (avLowGuid == Guid.Empty)
                {
                    Console.WriteLine("[SoftwareEncoder] Could not discover CODECAPI AVLowLatency GUID in Vortice assembly (continuing without ICodecAPI)");
                }

                // Try to find an ICodecAPI interface type in the Vortice assembly.
                var codecApiType = asm.GetTypes().FirstOrDefault(t => string.Equals(t.Name, "ICodecAPI", StringComparison.OrdinalIgnoreCase));
                IntPtr pUnk = IntPtr.Zero;
                IntPtr pCodec = IntPtr.Zero;
                try
                {
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        Console.WriteLine("[SoftwareEncoder] ICodecAPI configuration only supported on Windows (skipping)");
                        return;
                    }

                    pUnk = Marshal.GetIUnknownForObject(transform);

                    if (codecApiType != null)
                    {
                        // If the managed type has a GuidAttribute, use it for QueryInterface
                        var ga = (GuidAttribute?)codecApiType.GetCustomAttributes(typeof(GuidAttribute), false).FirstOrDefault();
                        if (ga != null)
                        {
                            var iid = new Guid(ga.Value);
                            int hr = Marshal.QueryInterface(pUnk, in iid, out pCodec);
                            if (hr == 0 && pCodec != IntPtr.Zero)
                            {
                                var codecObj = Marshal.GetObjectForIUnknown(pCodec);
                                bool ok = false;
                                if (avLowGuid != Guid.Empty)
                                {
                                    // Try several invocation patterns for SetValue
                                    try
                                    {
                                        dynamic d = codecObj;
                                        try { d.SetValue(avLowGuid, (uint)1); ok = true; }
                                        catch { }
                                        if (!ok)
                                        {
                                            try { d.SetValue(avLowGuid, true); ok = true; }
                                            catch { }
                                        }
                                        if (!ok)
                                        {
                                            try { d.SetValue(avLowGuid, (short)1); ok = true; }
                                            catch { }
                                        }
                                    }
                                    catch { }
                                }

                                if (ok) Console.WriteLine("[SoftwareEncoder] Set CODECAPI_AVLowLatencyMode via ICodecAPI.SetValue (best-effort)");
                                else Console.WriteLine("[SoftwareEncoder] ICodecAPI present but SetValue for AVLowLatency did not succeed (best-effort)");
                            }
                            else
                            {
                                Console.WriteLine("[SoftwareEncoder] ICodecAPI QueryInterface returned no interface (not supported by this MFT)");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("[SoftwareEncoder] ICodecAPI managed type not found in Vortice assembly");
                    }
                }
                finally
                {
                    if (pCodec != IntPtr.Zero) Marshal.Release(pCodec);
                    if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SoftwareEncoder] TrySetCodecApiLowLatency encountered an error: {ex.Message}");
            }
        }
    }
}
