using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace DxgiCapture
{
    // FFmpeg-based H.264 encoder using libx264 via FFmpeg.AutoGen.
    // - Accepts NV12 input buffers
    // - Converts to YUV420P via swscale
    // - Uses libx264 with ultrafast/zerolatency/baseline
    // - Returns Annex-B encoded NALs
    public unsafe class FfmpegEncoder : IDisposable
    {
        private AVCodec* _codec = null;
        private AVCodecContext* _c = null;
        private SwsContext* _sws = null;
        private AVFrame* _dstFrame = null;
        private AVFrame* _srcFrame = null;
        private AVPacket* _pkt = null;
        private int _width;
        private int _height;
        private int _fps;
        private int _bitrate;
        private bool _forceKeyframe = true;  // Force first frame as keyframe

        public byte[]? SPS { get; private set; }
        public byte[]? PPS { get; private set; }

        public void RequestKeyframe()
        {
            _forceKeyframe = true;
            //Console.WriteLine("[FfmpegEncoder] Keyframe requested");
        }

        static FfmpegEncoder()
        {
            try
            {
                // Ensure FFmpeg.AutoGen searches the app base (and any win-x64 subfolder) first
                var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                ffmpeg.RootPath = baseDir;
                // Also allow win-x64 subfolder if present
                var winx64 = System.IO.Path.Combine(baseDir, "win-x64");
                if (System.IO.Directory.Exists(winx64)) ffmpeg.RootPath = winx64;
                // Also try to find a DxgiCapture build output in the repo (user may have placed DLLs there)
                try
                {
                    var repoWin = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "..", "..", "..", "DxgiCapture", "DxgiCapture", "bin", "Debug", "net9.0", "win-x64"));
                    if (System.IO.Directory.Exists(repoWin)) ffmpeg.RootPath = repoWin;
                }
                catch { }
            }
            catch { }
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
        }

        public FfmpegEncoder()
        {
        }

        public Task InitializeAsync(int width, int height, int fps, int bitrate)
        {
            _width = width; _height = height; _fps = fps; _bitrate = bitrate;

            //Console.WriteLine($"[FfmpegEncoder] Initializing libx264 encoder for {_width}x{_height}@{_fps}fps, bitrate={_bitrate}");

            _codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
            if (_codec == null)
            {
                _codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
                if (_codec == null) throw new InvalidOperationException("H264 encoder not found in ffmpeg");
            }

            _c = ffmpeg.avcodec_alloc_context3(_codec);
            _c->width = _width;
            _c->height = _height;
            _c->time_base = new AVRational { num = 1, den = Math.Max(1, _fps) };
            _c->framerate = new AVRational { num = _fps, den = 1 };
            _c->gop_size = Math.Max(1, _fps * 2);
            _c->max_b_frames = 0;
            _c->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _c->bit_rate = _bitrate;

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "preset", "ultrafast", 0);
            ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
            ffmpeg.av_dict_set(&opts, "profile", "baseline", 0);

            int ret = ffmpeg.avcodec_open2(_c, _codec, &opts);
            if (ret < 0)
            {
                throw new InvalidOperationException($"avcodec_open2 failed: {ret}");
            }

            // (SPS/PPS extraction now handled by NALU parser after encoding)

            // Allocate frames and packet
            _srcFrame = ffmpeg.av_frame_alloc();
            _dstFrame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();

            _dstFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            _dstFrame->width = _width;
            _dstFrame->height = _height;
            ffmpeg.av_frame_get_buffer(_dstFrame, 32);

            // sws context: NV12 -> YUV420P
            _sws = ffmpeg.sws_getContext(_width, _height, AVPixelFormat.AV_PIX_FMT_NV12,
                                         _width, _height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                                         ffmpeg.SWS_BILINEAR, null, null, null);

            return Task.CompletedTask;
        }

        public async Task<byte[]?> EncodeNv12FrameAsync(byte[] nv12Buffer, long timestampMs)
        {
            if (_c == null) throw new InvalidOperationException("FfmpegEncoder not initialized");
            if (nv12Buffer == null) return null;

            int frameSize = _width * _height;
            int chromaSize = frameSize / 2;

            fixed (byte* pNv = &nv12Buffer[0])
            {
                // prepare source pointers (NV12: Y plane then interleaved UV)
                byte*[] src = new byte*[2];
                int[] srcStride = new int[2];
                src[0] = pNv;
                src[1] = pNv + frameSize;
                srcStride[0] = _width;
                srcStride[1] = _width;

                // prepare destination pointers from dstFrame (copy to locals first)
                byte* d0 = _dstFrame->data[0];
                byte* d1 = _dstFrame->data[1];
                byte* d2 = _dstFrame->data[2];
                int ls0 = _dstFrame->linesize[0];
                int ls1 = _dstFrame->linesize[1];
                int ls2 = _dstFrame->linesize[2];

                byte*[] dst = new byte*[3];
                int[] dstStride = new int[3];
                dst[0] = d0; dst[1] = d1; dst[2] = d2;
                dstStride[0] = ls0; dstStride[1] = ls1; dstStride[2] = ls2;

                // Convert NV12 -> YUV420P
                int h = ffmpeg.sws_scale(_sws, src, srcStride, 0, _height, dst, dstStride);
                if (h <= 0)
                {
                    return null;
                }

                // set PTS
                _dstFrame->pts = timestampMs;

                // Force keyframe if requested
                if (_forceKeyframe)
                {
                    _dstFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;  // Force IDR/I-frame
                    _forceKeyframe = false;
                    //Console.WriteLine("[FfmpegEncoder] Forcing keyframe (I-frame)");
                }

                int ret = ffmpeg.avcodec_send_frame(_c, _dstFrame);
                if (ret < 0) return null;

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
                    }
                    ffmpeg.av_packet_unref(_pkt);
                }

                if (outBytes.Count > 0)
                {
                    var ba = outBytes.ToArray();
                    try { ExtractAndCacheSpsPpsFromAnnexB(ba); } catch { }
                    return ba;
                }
                return null;
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
                        //Console.WriteLine($"[FfmpegEncoder] SPS detected: {BitConverter.ToString(raw, 0, Math.Min(16, raw.Length)).Replace("-", " ")}...");
                        //Console.WriteLine($"[FfmpegEncoder] SPS Base64: {Convert.ToBase64String(raw)}");
                    }
                    else if (nalType == 8 && foundPps == null)
                    {
                        foundPps = raw;
                        //Console.WriteLine($"[FfmpegEncoder] PPS detected: {BitConverter.ToString(raw, 0, Math.Min(16, raw.Length)).Replace("-", " ")}...");
                        //Console.WriteLine($"[FfmpegEncoder] PPS Base64: {Convert.ToBase64String(raw)}");
                    }
                    if (foundSps != null && foundPps != null) break;
                    if (next >= 0) offset = next; else break;
                }
                if (foundSps != null && foundPps != null)
                {
                    SPS = foundSps;
                    PPS = foundPps;
                    var sprop = GetSpropParameterSets();
                    //Console.WriteLine($"[FfmpegEncoder] sprop-parameter-sets: {sprop}");
                }
            }
            catch (Exception) { /*Console.WriteLine($"[FfmpegEncoder] SPS/PPS extraction error: {ex.Message}");*/ }
        }

        public string GetSpropParameterSets()
        {
            if (SPS == null || PPS == null) return string.Empty;
            return $"{Convert.ToBase64String(SPS)},{Convert.ToBase64String(PPS)}";
        }

        private static byte[]? BuildAnnexBFromAvcC(byte* data, int size)
        {
            // avcC starts with 0x01
            if (size < 7) return null;
            if (data[0] != 0x01) return null;
            int idx = 5;
            int spsCount = data[idx++] & 0x1F;
            using var ms = new System.IO.MemoryStream();
            for (int i=0;i<spsCount;i++)
            {
                int len = (data[idx]<<8) | data[idx+1]; idx+=2;
                ms.Write(new byte[]{0,0,0,1},0,4);
                ms.Write(new ReadOnlySpan<byte>(data+idx, len));
                idx+=len;
            }
            int ppsCount = data[idx++];
            for (int i=0;i<ppsCount;i++)
            {
                int len = (data[idx]<<8) | data[idx+1]; idx+=2;
                ms.Write(new byte[]{0,0,0,1},0,4);
                ms.Write(new ReadOnlySpan<byte>(data+idx, len));
                idx+=len;
            }
            return ms.ToArray();
        }

        public (string? SpsBase64, string? PpsBase64, string? Sprop) GetSdpFmtpParameters()
        {
            if (SPS == null || PPS == null) return (null, null, null);
            string spsB = Convert.ToBase64String(SPS);
            string ppsB = Convert.ToBase64String(PPS);
            string sprop = $"{spsB},{ppsB}";
            return (spsB, ppsB, sprop);
        }

        public void Dispose()
        {
            if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
            if (_dstFrame != null) { var f = _dstFrame; ffmpeg.av_frame_free(&f); _dstFrame = null; }
            if (_srcFrame != null) { var f2 = _srcFrame; ffmpeg.av_frame_free(&f2); _srcFrame = null; }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_c != null) { var c = _c; ffmpeg.avcodec_free_context(&c); _c = null; }
        }
    }
}
