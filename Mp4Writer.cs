using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace DxgiCapture
{
    public class Mp4Writer : IDisposable
    {
        private IMFSinkWriter? _writer;
        private int _streamIndex = 0;
        private long _nextSampleTime = 0;
        private long _frameDuration100ns = 0;
        private string? _currentOutputPath;

        public Mp4Writer()
        {
        }

        public void EnsureWriter(string outputPath, int width, int height, int fps, int bitrate)
        {
            if (_writer != null) return;
            _frameDuration100ns = 10_000_000L / Math.Max(1, fps);

            var writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, null);

            var outMediaType = MediaFactory.MFCreateMediaType();
            outMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outMediaType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            MediaFactory.MFSetAttributeSize(outMediaType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(outMediaType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
            outMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);

            int streamIndex = writer.AddStream(outMediaType);

            var inMediaType = MediaFactory.MFCreateMediaType();
            inMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeSize(inMediaType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(inMediaType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1u);
            inMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
            int sampleSize = width * height + (width * height) / 2;
            inMediaType.Set(MediaTypeAttributeKeys.SampleSize, (uint)sampleSize);
            inMediaType.Set(MediaTypeAttributeKeys.DefaultStride, width);

            writer.SetInputMediaType(streamIndex, inMediaType, null);
            writer.BeginWriting();

            _writer = writer;
            _streamIndex = streamIndex;
            _nextSampleTime = 0;
            _currentOutputPath = outputPath;

            Console.WriteLine($"Started sink writer -> {outputPath} ({width}x{height}@{fps}fps, {bitrate}bps)");
        }

        public void WriteArgbFrame(byte[] argb, int stride, int width, int height)
        {
            if (_writer == null) return;

            int yPlaneSize = width * height;
            int uvPlaneSize = (width * height) / 2;
            int totalBytes = yPlaneSize + uvPlaneSize;

            var nv12 = ArrayPool<byte>.Shared.Rent(totalBytes);
            try
            {
                // Fill Y plane
                for (int yy = 0; yy < height; yy++)
                {
                    int srcRow = yy * stride;
                    int dstRow = yy * width;
                    for (int xx = 0; xx < width; xx++)
                    {
                        int sIdx = srcRow + xx * 4; // BGRA
                        int B = argb[sIdx + 0];
                        int G = argb[sIdx + 1];
                        int R = argb[sIdx + 2];
                        int Y = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
                        if (Y < 0) Y = 0; else if (Y > 255) Y = 255;
                        nv12[dstRow + xx] = (byte)Y;
                    }
                }

                // Fill interleaved UV plane (subsample 2x2)
                int uvIndex = yPlaneSize;
                for (int y = 0; y < height; y += 2)
                {
                    for (int x = 0; x < width; x += 2)
                    {
                        int sumU = 0, sumV = 0;
                        int count = 0;
                        for (int yy2 = 0; yy2 < 2; yy2++)
                        {
                            int py = y + yy2;
                            if (py >= height) continue;
                            int srcRow = py * stride;
                            for (int xx2 = 0; xx2 < 2; xx2++)
                            {
                                int px = x + xx2;
                                if (px >= width) continue;
                                int sIdx = srcRow + px * 4;
                                int B = argb[sIdx + 0];
                                int G = argb[sIdx + 1];
                                int R = argb[sIdx + 2];
                                int U = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
                                int V = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;
                                sumU += U; sumV += V;
                                count++;
                            }
                        }
                        if (count == 0) count = 1;
                        nv12[uvIndex++] = (byte)(sumU / count);
                        nv12[uvIndex++] = (byte)(sumV / count);
                    }
                }

                var buffer = MediaFactory.MFCreateMemoryBuffer(totalBytes);
                try
                {
                    buffer.Lock(out IntPtr destPtr, out int maxLen, out int curLen);
                    try
                    {
                        Marshal.Copy(nv12, 0, destPtr, totalBytes);
                        buffer.CurrentLength = totalBytes;
                    }
                    finally { buffer.Unlock(); }

                    var sample = MediaFactory.MFCreateSample();
                    try
                    {
                        sample.AddBuffer(buffer);
                        sample.SampleTime = _nextSampleTime;
                        sample.SampleDuration = _frameDuration100ns;

                        _writer.WriteSample(_streamIndex, sample);
                        _nextSampleTime += _frameDuration100ns;
                    }
                    finally { sample.Dispose(); }
                }
                finally { buffer.Dispose(); }
            }
            finally
            {
                try { ArrayPool<byte>.Shared.Return(nv12); } catch { }
            }
        }

        public void Dispose()
        {
            try { _writer?.Finalize(); } catch { }
            try { _writer?.Dispose(); _writer = null; } catch { }
        }
    }
}
