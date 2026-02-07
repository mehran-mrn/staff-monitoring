
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace DxgiCapture
{
    // NVENC encoder stub. Replace this with a real NVENC wrapper (P/Invoke to native NVENC or
    // a managed binding) that accepts ARGB/BGRA frames and emits encoded H264/VP8 bitstreams.
    public unsafe class NvencEncoder : IDisposable
    {
        private AVCodec* _codec = null;
        private AVCodecContext* _c = null;
        private AVFrame* _frame = null;
        private AVPacket* _pkt = null;
        private int _width;
        private int _height;
        private int _fps;
        private int _bitrate;
        private bool _initialized = false;
        public byte[]? SPS { get; private set; }
        public byte[]? PPS { get; private set; }

        public NvencEncoder() { }

        public static bool IsNvencAvailable()
        {
            try
            {
                var codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
                return codec != null;
            }
            catch { return false; }
        }

        public Task InitializeAsync(int width, int height, int fps, int bitrate, string codec = "h264")
        {
            _width = width; _height = height; _fps = fps; _bitrate = bitrate;
            Console.WriteLine("[NvencEncoder] Detecting NVIDIA GPU...");
            _codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
            if (_codec == null)
                throw new NotSupportedException("NVENC (h264_nvenc) not available");
            Console.WriteLine("[NvencEncoder] ✓ Found h264_nvenc encoder");

            _c = ffmpeg.avcodec_alloc_context3(_codec);
            _c->width = _width;
            _c->height = _height;
            _c->time_base = new AVRational { num = 1, den = Math.Max(1, _fps) };
            _c->framerate = new AVRational { num = _fps, den = 1 };
            _c->gop_size = Math.Max(1, _fps);
            _c->max_b_frames = 0;
            _c->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
            _c->bit_rate = _bitrate;
            _c->profile = ffmpeg.FF_PROFILE_H264_BASELINE;

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "preset", "p4", 0); // p4 = balanced
            ffmpeg.av_dict_set(&opts, "tune", "ll", 0);   // low latency
            ffmpeg.av_dict_set(&opts, "rc", "cbr", 0);     // constant bitrate
            ffmpeg.av_dict_set(&opts, "gpu", "0", 0);      // use first GPU

            int ret = ffmpeg.avcodec_open2(_c, _codec, &opts);
            if (ret < 0)
                throw new InvalidOperationException($"avcodec_open2 failed: {ret}");

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _frame->width = _width;
            _frame->height = _height;
            ffmpeg.av_frame_get_buffer(_frame, 32);

            _pkt = ffmpeg.av_packet_alloc();

            Console.WriteLine($"[NvencEncoder] GPU: NVIDIA (detected, see nvidia-smi for details)");
            Console.WriteLine($"[NvencEncoder] Preset: p4 (balanced)");
            Console.WriteLine($"[NvencEncoder] Bitrate: {_bitrate / 1000} kbps");
            Console.WriteLine($"[NvencEncoder] Initialized {_width}x{_height}@{_fps}fps");
            _initialized = true;
            return Task.CompletedTask;
        }

        public Task<byte[]?> EncodeNv12FrameAsync(byte[] nv12Buffer, long timestampMs)
        {
            if (!_initialized) throw new InvalidOperationException("NvencEncoder not initialized");
            if (nv12Buffer == null) return Task.FromResult<byte[]?>(null);

            int frameSize = _width * _height;
            int chromaSize = frameSize / 2;

            fixed (byte* pNv = &nv12Buffer[0])
            {
                // NV12: Y plane then interleaved UV
                ffmpeg.av_frame_make_writable(_frame);
                Buffer.MemoryCopy(pNv, (void*)_frame->data[0], frameSize, frameSize);
                Buffer.MemoryCopy(pNv + frameSize, (void*)_frame->data[1], chromaSize, chromaSize);
                _frame->pts = timestampMs;

                int ret = ffmpeg.avcodec_send_frame(_c, _frame);
                if (ret < 0) return Task.FromResult<byte[]?>(null);

                System.Collections.Generic.List<byte> outBytes = new System.Collections.Generic.List<byte>();
                while (ret >= 0)
                {
                    ret = ffmpeg.avcodec_receive_packet(_c, _pkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) break;

                    if (_pkt->size > 0 && _pkt->data != null)
                    {
                        byte[] buff = new byte[_pkt->size];
                        Marshal.Copy((IntPtr)_pkt->data, buff, 0, _pkt->size);
                        outBytes.AddRange(buff);
                        ExtractAndCacheSpsPpsFromAnnexB(buff);
                        Console.WriteLine($"[NvencEncoder] Frame {timestampMs} encoded: {_pkt->size} bytes");
                    }
                    ffmpeg.av_packet_unref(_pkt);
                }

                if (outBytes.Count > 0)
                {
                    return Task.FromResult<byte[]?>(outBytes.ToArray());
                }
                return Task.FromResult<byte[]?>(null);
            }
        }

        private void ExtractAndCacheSpsPpsFromAnnexB(byte[] annexb)
        {
            try
            {
                int offset = 0;
                byte[]? foundSps = null;
                byte[]? foundPps = null;
                while (offset + 4 <= annexb.Length)
                {
                    // Find start code (3 or 4 bytes)
                    int start = -1;
                    int scLen = 0;
                    for (int i = offset; i + 3 < annexb.Length; ++i)
                    {
                        if (annexb[i] == 0 && annexb[i+1] == 0 && annexb[i+2] == 1) { start = i+3; scLen = 3; break; }
                        if (annexb[i] == 0 && annexb[i+1] == 0 && annexb[i+2] == 0 && annexb[i+3] == 1) { start = i+4; scLen = 4; break; }
                    }
                    if (start < 0) break;
                    int next = -1;
                    for (int j = start; j + 3 < annexb.Length; ++j)
                    {
                        if (annexb[j] == 0 && annexb[j+1] == 0 && annexb[j+2] == 1) { next = j; break; }
                        if (annexb[j] == 0 && annexb[j+1] == 0 && annexb[j+2] == 0 && annexb[j+3] == 1) { next = j; break; }
                    }
                    int nalEnd = (next >= 0) ? next : annexb.Length;
                    int nalLen = nalEnd - start;
                    if (nalLen <= 0) break;
                    int nalType = annexb[start] & 0x1F;
                    var raw = new byte[nalLen];
                    Array.Copy(annexb, start, raw, 0, nalLen);
                    if (nalType == 7 && foundSps == null)
                    {
                        foundSps = raw;
                        Console.WriteLine($"[NvencEncoder] SPS: {raw.Length} bytes");
                    }
                    else if (nalType == 8 && foundPps == null)
                    {
                        foundPps = raw;
                        Console.WriteLine($"[NvencEncoder] PPS: {raw.Length} bytes");
                    }
                    if (foundSps != null && foundPps != null) break;
                    if (next >= 0) offset = next; else break;
                }
                if (foundSps != null && foundPps != null)
                {
                    SPS = foundSps;
                    PPS = foundPps;
                    var sprop = GetSpropParameterSets();
                    Console.WriteLine($"[NvencEncoder] sprop-parameter-sets: {sprop}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[NvencEncoder] SPS/PPS extraction error: {ex.Message}"); }
        }

        public string GetSpropParameterSets()
        {
            if (SPS == null || PPS == null) return string.Empty;
            return $"{Convert.ToBase64String(SPS)},{Convert.ToBase64String(PPS)}";
        }

        public void Dispose()
        {
            if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_c != null) { var c = _c; ffmpeg.avcodec_free_context(&c); _c = null; }
        }
    }
}
