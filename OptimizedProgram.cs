using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DxgiCapture.Optimized
{
    /// <summary>
    /// برنامه اصلی بهینه‌شده برای ریموت دسکتاپ
    /// - کپچر با حداقل CPU
    /// - ارسال فریم‌ها
    /// - دریافت و اجرای دستورات موس/کیبورد
    /// </summary>
    class OptimizedProgram
    {
        // Windows API برای کنترل ورودی
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
        
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        static async Task Main(string[] args)
        {
            string serverUrl = args.Length > 0 ? args[0] : "http://localhost:5000";
            int targetFps = args.Length > 1 ? int.Parse(args[1]) : 30;
            int bitrate = args.Length > 2 ? int.Parse(args[2]) : 2_000_000;
            
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine("  Optimized Remote Desktop Client");
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine($"Server: {serverUrl}");
            Console.WriteLine($"Target: {targetFps} FPS @ {bitrate/1000} kbps");
            Console.WriteLine();

            using var capture = new OptimizedScreenCapture(targetFps, bitrate);
            
            // انتظار برای استخراج SPS/PPS
            Console.Write("Extracting codec parameters... ");
            int attempts = 0;
            while (capture.SPS == null && attempts < 10)
            {
                await capture.CaptureAndEncodeFrameAsync(0);
                attempts++;
                await Task.Delay(100);
            }
            
            if (capture.SPS == null)
            {
                Console.WriteLine("✗ Failed");
                return;
            }
            Console.WriteLine("✓");
            
            var sprop = capture.GetSpropParameterSets();
            Console.WriteLine($"sprop: {sprop.Substring(0, Math.Min(50, sprop.Length))}...");
            Console.WriteLine();
            
            // اتصال به سرور
            using var sender = new NativeWebRtcSender(); // یا SIPSorcerySDPBuilder
            using var client = new MinimalWebRtcClient(serverUrl, sender);
            
            // ثبت event handlers برای کنترل
            client.OnMouseMove += HandleMouseMove;
            client.OnKeyPress += HandleKeyPress;
            
            Console.Write("Connecting to server... ");
            if (!await client.ConnectAsync(sprop))
            {
                Console.WriteLine("✗ Failed");
                return;
            }
            Console.WriteLine("✓");
            Console.WriteLine();
            
            // حلقه اصلی capture و ارسال
            Console.WriteLine("Streaming started. Press Ctrl+C to stop.");
            Console.WriteLine("─────────────────────────────────────");
            
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            
            await StreamingLoopAsync(capture, client, targetFps, cts.Token);
            
            Console.WriteLine();
            Console.WriteLine("Streaming stopped.");
        }

        /// <summary>
        /// حلقه اصلی با rate limiting دقیق
        /// </summary>
        static async Task StreamingLoopAsync(
            OptimizedScreenCapture capture,
            MinimalWebRtcClient client,
            int targetFps,
            CancellationToken ct)
        {
            long frameCount = 0;
            var startTime = DateTime.UtcNow;
            var nextFrameTime = DateTime.UtcNow;
            var frameDuration = TimeSpan.FromMilliseconds(1000.0 / targetFps);
            
            // Statistics
            long totalBytes = 0;
            var lastStatsTime = DateTime.UtcNow;
            
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                
                // Rate limiting
                if (now < nextFrameTime)
                {
                    var delay = (int)(nextFrameTime - now).TotalMilliseconds;
                    if (delay > 0)
                        await Task.Delay(delay, ct);
                    
                    now = DateTime.UtcNow;
                }
                
                nextFrameTime = now + frameDuration;
                
                try
                {
                    // Capture و encode
                    var timestampMs = (long)(now - startTime).TotalMilliseconds;
                    var encodedFrame = await capture.CaptureAndEncodeFrameAsync(timestampMs);
                    
                    if (encodedFrame != null && encodedFrame.Length > 0)
                    {
                        // تبدیل timestamp به 90kHz
                        uint timestamp90kHz = (uint)(timestampMs * 90);
                        
                        // تشخیص keyframe (شروع با 00 00 00 01 65 یا 67)
                        bool isKeyFrame = IsKeyFrame(encodedFrame);
                        
                        // ارسال به سرور
                        await client.SendFrameAsync(encodedFrame, timestamp90kHz, isKeyFrame);
                        
                        totalBytes += encodedFrame.Length;
                        frameCount++;
                        
                        // نمایش آمار هر 5 ثانیه
                        if ((now - lastStatsTime).TotalSeconds >= 5)
                        {
                            var elapsed = (now - startTime).TotalSeconds;
                            var fps = frameCount / elapsed;
                            var kbps = (totalBytes * 8 / 1024) / elapsed;
                            
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] " +
                                            $"Frames: {frameCount} | " +
                                            $"FPS: {fps:F1} | " +
                                            $"Bitrate: {kbps:F0} kbps");
                            
                            lastStatsTime = now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] {ex.Message}");
                }
            }
        }

        /// <summary>
        /// تشخیص keyframe از Annex-B NAL
        /// </summary>
        static bool IsKeyFrame(byte[] data)
        {
            if (data.Length < 5) return false;
            
            // 00 00 00 01 67 = SPS (keyframe marker)
            // 00 00 00 01 65 = IDR slice
            if (data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
            {
                int nalType = data[4] & 0x1F;
                return nalType == 7 || nalType == 5; // SPS or IDR
            }
            
            // 00 00 01 67 or 65
            if (data[0] == 0 && data[1] == 0 && data[2] == 1)
            {
                int nalType = data[3] & 0x1F;
                return nalType == 7 || nalType == 5;
            }
            
            return false;
        }

        /// <summary>
        /// پردازش دستور حرکت موس
        /// </summary>
        static void HandleMouseMove(int x, int y, int buttons)
        {
            try
            {
                SetCursorPos(x, y);
                
                // بررسی کلیک‌ها
                if ((buttons & 1) != 0) // Left button
                {
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                }
                else
                {
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }
                
                if ((buttons & 2) != 0) // Right button
                {
                    mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                }
                else
                {
                    mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                }
            }
            catch { }
        }

        /// <summary>
        /// پردازش دستور کیبورد
        /// </summary>
        static void HandleKeyPress(int keyCode, bool isDown)
        {
            try
            {
                byte vkCode = (byte)keyCode;
                uint flags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                keybd_event(vkCode, 0, flags, UIntPtr.Zero);
            }
            catch { }
        }
    }
}
