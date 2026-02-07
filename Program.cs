// Program.cs - نسخه نهایی با WebRTCConnectionManager
using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Vortice.DXGI;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;
using System.Threading;
using System.Security.Cryptography;
using System.Buffers;
using System.IO;
using System.Diagnostics;
using Vortice.MediaFoundation;
using System.Linq;
using System.Text.RegularExpressions;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
class Program
{
    // Cached staging texture to avoid repeated allocations
    static ID3D11Texture2D? cachedStagingTex = null;
    static int cachedWidth = 0;
    static int cachedHeight = 0;
    static Format cachedFormat = default;

    // Media Foundation encoder state
    static IMFSinkWriter? sinkWriter = null;
    static int videoStreamIndex = 0;
    static int targetFps = 15;
    static int targetBitrate = 5000000; // 5 Mbps
    static long frameDuration100ns = 10000000 / targetFps; // 100-ns units
    static long nextSampleTime = 0;
    static string? currentOutputPath = null;
    static DateTime lastSinkWriterAttempt = DateTime.MinValue;
    // Set to true to enable writing an MP4 file of captured frames. Leave false to disable MP4 output.
    static bool enableMp4Output = false;

    // ============================================================================
    // WebRTC Connection Manager (جایگزین WebRTC/Signaling مستقیم)
    // ============================================================================
    static DxgiCapture.WebRTCConnectionManager? connectionManager = null;
    // Control channel state
    static int _controlDataChannelId = -1;
    static CancellationTokenSource? _controlSendCts = null;
    static bool _remoteControlEnabled = false;
    // Authorization: HMAC secret (set on host as env var CONTROL_SECRET)
    static string? _controlSecret = Environment.GetEnvironmentVariable("CONTROL_SECRET");
    static bool _remoteControlAuthorized = false;

    // Helper P/Invoke and input simulation for remote control
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint KEYEVENTF_KEYUP = 0x0002;

    static async Task ControlSenderLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_controlDataChannelId >= 0 && connectionManager != null)
                    {
                        if (GetCursorPos(out var pt))
                        {
                            int sw = GetSystemMetrics(0);
                            int sh = GetSystemMetrics(1);
                            // Send normalized percentage coordinates (viewer expects 0..100)
                            double pctX = sw > 0 ? Math.Round((pt.X * 100.0) / sw, 2) : 0.0;
                            double pctY = sh > 0 ? Math.Round((pt.Y * 100.0) / sh, 2) : 0.0;
                            var payload = new { type = "mouseMove", x = pctX, y = pctY, controlEnabled = _remoteControlEnabled, authorized = _remoteControlAuthorized };
                            string msg = System.Text.Json.JsonSerializer.Serialize(payload);
                            connectionManager.SendDataChannelMessage(_controlDataChannelId, msg);
                        }
                    }
                }
                catch { }
                await Task.Delay(80, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    static void HandleIncomingDataChannelMessage(int dcId, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            string type = typeEl.GetString() ?? string.Empty;

            // If we haven't recorded the control datachannel id yet, use the one that sent us messages
            if (_controlDataChannelId < 0) _controlDataChannelId = dcId;

            // Control enable/disable requires auth when a secret is configured
            switch (type)
            {
                case "control":
                    if (root.TryGetProperty("enabled", out var en))
                    {
                        bool want = en.GetBoolean();
                        if (want)
                        {
                            bool authorized = false;
                            if (!string.IsNullOrEmpty(_controlSecret))
                            {
                                if (root.TryGetProperty("auth", out var auth) && auth.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (auth.TryGetProperty("ts", out var tsEl) && auth.TryGetProperty("sig", out var sigEl))
                                    {
                                        string ts = tsEl.GetString() ?? string.Empty;
                                        string sig = sigEl.GetString() ?? string.Empty;
                                        if (long.TryParse(ts, out var tsv))
                                        {
                                            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                            if (Math.Abs(now - tsv) <= 120)
                                            {
                                                // compute HMAC-SHA256 over "{ts}:control"
                                                using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_controlSecret));
                                                var payload = Encoding.UTF8.GetBytes(ts + ":control");
                                                var computed = h.ComputeHash(payload);
                                                var computedHex = BitConverter.ToString(computed).Replace("-", "").ToLowerInvariant();
                                                if (!string.IsNullOrEmpty(sig) && computedHex == sig.ToLowerInvariant()) authorized = true;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // No secret configured on host => allow control by default (useful for dev)
                                authorized = true;
                            }

                            if (authorized)
                            {
                                _remoteControlAuthorized = true;
                                _remoteControlEnabled = true;
                                Console.WriteLine("[Program] Remote control ENABLED (authorized)");
                            }
                            else
                            {
                                Console.WriteLine("[Program] Remote control enable rejected (auth failed)");
                            }
                        }
                        else
                        {
                            _remoteControlAuthorized = false;
                            _remoteControlEnabled = false;
                            Console.WriteLine("[Program] Remote control DISABLED");
                        }
                    }
                    break;

                case "mouseMove":
                    if (!_remoteControlAuthorized) break;
                    if (root.TryGetProperty("x", out var xEl) && root.TryGetProperty("y", out var yEl))
                    {
                        // Viewer sends percentages (0..100) for x/y. Handle both 0..1 (fraction), 0..100 (percent) or pixel values.
                        double relX = xEl.ValueKind == System.Text.Json.JsonValueKind.Number ? xEl.GetDouble() : 0.0;
                        double relY = yEl.ValueKind == System.Text.Json.JsonValueKind.Number ? yEl.GetDouble() : 0.0;
                        int sw = GetSystemMetrics(0);
                        int sh = GetSystemMetrics(1);
                        int xPx;
                        int yPx;
                        if (relX <= 1.0) xPx = (int)Math.Round(relX * sw);
                        else if (relX <= 100.0) xPx = (int)Math.Round(relX * sw / 100.0);
                        else xPx = (int)Math.Round(relX);

                        if (relY <= 1.0) yPx = (int)Math.Round(relY * sh);
                        else if (relY <= 100.0) yPx = (int)Math.Round(relY * sh / 100.0);
                        else yPx = (int)Math.Round(relY);

                        SetCursorPos(xPx, yPx);
                    }
                    break;

                case "mouseClick":
                    if (!_remoteControlAuthorized) break;
                    {
                        double relX = root.TryGetProperty("x", out var xx) && xx.ValueKind == System.Text.Json.JsonValueKind.Number ? xx.GetDouble() : double.NaN;
                        double relY = root.TryGetProperty("y", out var yy) && yy.ValueKind == System.Text.Json.JsonValueKind.Number ? yy.GetDouble() : double.NaN;
                        int sw = GetSystemMetrics(0);
                        int sh = GetSystemMetrics(1);
                        int xPx = -1;
                        int yPx = -1;
                        if (!double.IsNaN(relX))
                        {
                            if (relX <= 1.0) xPx = (int)Math.Round(relX * sw);
                            else if (relX <= 100.0) xPx = (int)Math.Round(relX * sw / 100.0);
                            else xPx = (int)Math.Round(relX);
                        }
                        if (!double.IsNaN(relY))
                        {
                            if (relY <= 1.0) yPx = (int)Math.Round(relY * sh);
                            else if (relY <= 100.0) yPx = (int)Math.Round(relY * sh / 100.0);
                            else yPx = (int)Math.Round(relY);
                        }

                        string button = root.TryGetProperty("button", out var b) ? b.GetString() ?? "left" : "left";
                        string action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "down" : "down";
                        if (xPx >= 0 && yPx >= 0) SetCursorPos(xPx, yPx);
                        if (button == "left")
                        {
                            if (action == "down") mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                            else mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        }
                        else if (button == "right")
                        {
                            if (action == "down") mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                            else mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                        }
                    }
                    break;

                case "key":
                    if (!_remoteControlAuthorized) break;
                    if (root.TryGetProperty("vk", out var vkEl) && root.TryGetProperty("action", out var act))
                    {
                        byte vk = (byte)vkEl.GetInt32();
                        string action = act.GetString() ?? "down";
                        if (action == "down") keybd_event(vk, 0, 0, UIntPtr.Zero);
                        else keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Program] Error handling datachannel message: {ex.Message}");
        }
    }

    // latest ARGB frame buffer published by capture loop (thread-safe via lock)
    static byte[]? latestArgbBuffer = null;
    static int latestStride = 0;
    static int latestWidth = 0;
    static int latestHeight = 0;
    static object latestFrameLock = new object();

    // WebRTC viewer connection state: used to pause/resume capture
    static bool _isViewerConnected = false;

    // Trailing capture: continue processing frames for up to 1 second after last detected change
    static long lastChangedFrameTimeMs = 0;
    const long trailingCaptureWindowMs = 1000;  // 1 second

    static async Task Main(string[] args)
    {
        try
        {
            string outputDir = "screenshots";
            Directory.CreateDirectory(outputDir);

            Console.WriteLine("Starting continuous screen capture (H.264 to capture.mp4). Press Ctrl+C to stop.");

            // Initialize Media Foundation
            try { MediaFactory.MFStartup(true); } catch (Exception ex) { Console.WriteLine($"MFStartup warning: {ex.Message}"); }

            // Initialize capture resources
            InitializeCapture(out ID3D11Device? device, out ID3D11DeviceContext? context, out IDXGIOutputDuplication? duplication);
            if (device == null || context == null || duplication == null)
                throw new Exception("Failed to initialize capture resources");

            using var cts = new CancellationTokenSource();
            
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // ============================================================================
            // WebRTC Integration با Connection Manager
            // ============================================================================
            try
            {
                // Signaling configuration
                string serverBase = Environment.GetEnvironmentVariable("SIGNALING_SERVER_BASE") ?? "https://vigil.aoaci.com";
                string clientKey = Environment.GetEnvironmentVariable("SIGNALING_CLIENT_KEY") ?? "producer-001";

                Console.WriteLine($"[WebRTC] Signaling config: server={serverBase}, client_key={clientKey.Substring(0, Math.Min(8, clientKey.Length))}...");

                // Read optional TURN configuration from environment
                // string? turnUrl = Environment.GetEnvironmentVariable("TURN_URL");
                // string? turnUser = Environment.GetEnvironmentVariable("TURN_USERNAME");
                // string? turnPass = Environment.GetEnvironmentVariable("TURN_PASSWORD");
                string? turnUrl = "turn:38.60.249.249:3478";
                string? turnUser = "mehr3an";
                string? turnPass = "sdfseefdfsd6";

                // ایجاد Connection Manager (pass TURN config)
                connectionManager = new DxgiCapture.WebRTCConnectionManager(
                    serverBase,
                    clientKey,
                    maxReconnectAttempts: 5,
                    reconnectDelayMs: 2000,
                    turnUrl: turnUrl,
                    turnUsername: turnUser,
                    turnPassword: turnPass
                );

                // Subscribe to connection manager events
                connectionManager.OnLog += (msg) => Console.WriteLine(msg);

                connectionManager.OnConnected += () =>
                {
                    Console.WriteLine("[Program] ✅ WebRTC connection established - starting capture");
                    _isViewerConnected = true;

                    // Native sender creates 'control' data channel during initialization; subscribe to events
                    try
                    {
                        connectionManager.OnDataChannelMessage += (dcId, text) =>
                        {
                            try { HandleIncomingDataChannelMessage(dcId, text); } catch { }
                        };

                        connectionManager.OnDataChannelBinaryMessage += (dcId, data) => { };

                        connectionManager.OnDataChannelStateChanged += (dcId, isOpen) =>
                        {
                            Console.WriteLine($"[Program] DataChannel {dcId} state changed: isOpen={isOpen}");
                            if (isOpen)
                            {
                                // Store the control data channel id so we can send updates back to viewer
                                _controlDataChannelId = dcId;
                                if (_controlSendCts == null)
                                {
                                    _controlSendCts = new CancellationTokenSource();
                                    _ = Task.Run(() => ControlSenderLoop(_controlSendCts.Token));
                                }
                            }
                            else
                            {
                                // Only clear if the closed channel is the one we were using
                                if (_controlDataChannelId == dcId) _controlDataChannelId = -1;
                                try { _controlSendCts?.Cancel(); } catch { }
                                _controlSendCts = null;
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Program] Error subscribing to control channel events: {ex.Message}");
                    }
                };

                connectionManager.OnDisconnected += () =>
                {
                    Console.WriteLine("[Program] ❌ WebRTC connection lost - pausing capture");
                    _isViewerConnected = false;
                };

                // Also react to signaling-level viewer presence so we can start capture
                // as soon as a viewer appears (before native WebRTC fully connects).
                connectionManager.OnViewerJoined += () =>
                {
                    Console.WriteLine("[Program] 🟢 Viewer joined (signaling) - starting capture");
                    _isViewerConnected = true;
                };

                connectionManager.OnViewerLeft += () =>
                {
                    Console.WriteLine("[Program] 🔴 Viewer left (signaling) - pausing capture");
                    _isViewerConnected = false;
                };

                connectionManager.OnReconnectAttempt += (attempt) =>
                {
                    Console.WriteLine($"[Program] 🔄 Reconnection attempt {attempt}...");
                };

                connectionManager.OnKeyFrameRequested += () =>
                {
                    try
                    {
                        Console.WriteLine("[Program] 🔑 Remote peer requested keyframe");
                        connectionManager.SoftwareEncoder?.FfmpegFallback?.RequestKeyframe();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Program] Error requesting keyframe: {ex.Message}");
                    }
                };

                // Start WebRTC connection
                bool started = await connectionManager.StartAsync(targetFps, targetBitrate);
                if (!started)
                {
                    Console.WriteLine("[Program] ⚠️ Failed to start WebRTC connection - will retry");
                }
                else
                {
                    Console.WriteLine("[Program] WebRTC connection manager started successfully");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] Initialization error: {ex}");
            }

            

            int frameCount = 0;
            int? maxFrames = null;
            if (args != null && args.Length > 0)
            {
                if (int.TryParse(args[0], out var n) && n > 0) maxFrames = n;
            }
            string mp4Path = Path.Combine(outputDir, "capture.mp4");

            // Session start time for calculating incremental frame timestamps
            long sessionStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // Keyframe forcing: force keyframe every 2 seconds (at 15fps = every 30 frames)
            int keyframeInterval = targetFps * 2;
            long lastKeyframeFrameCount = 0;

            try
            {
                // Use the new ScreenCapture and Mp4Writer classes to encapsulate capture and MP4 writing.
                using var screenCapture = new DxgiCapture.ScreenCapture();
                // MP4 output can be disabled via `enableMp4Output`. When disabled we do not create the Mp4Writer
                DxgiCapture.Mp4Writer? mp4Writer = null;
                if (enableMp4Output) mp4Writer = new DxgiCapture.Mp4Writer();

                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        // Pause capture and encoding when no viewer is connected (save CPU/GPU)
                        if (!_isViewerConnected)
                        {
                            if (frameCount % 50 == 0) // Log هر 50 بار
                            {
                                Console.WriteLine("[Program] Waiting for viewer... (capture paused)");
                            }
                            await Task.Delay(1500, cts.Token);
                            continue;
                        }

                        // Attempt to capture a new frame; if none available but within trailing window, reuse last frame
                        byte[]? frameToProcess = null;
                        int procStride = 0, procW = 0, procH = 0;
                        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        bool isNewFrame = false;
                        int timeoutMs = 1000 / targetFps;
                        
                        if (screenCapture.TryCaptureFrame(timeoutMs, out var argb, out var strideVal, out var w, out var h) && argb != null)
                        {
                            // New frame detected — update last change time and cache latest frame
                            lastChangedFrameTimeMs = nowMs;
                            isNewFrame = true;

                            lock (latestFrameLock)
                            {
                                latestArgbBuffer = argb;
                                latestStride = strideVal;
                                latestWidth = w;
                                latestHeight = h;
                            }

                            frameToProcess = argb;
                            procStride = strideVal;
                            procW = w;
                            procH = h;
                        }
                        else if (lastChangedFrameTimeMs != 0 && (nowMs - lastChangedFrameTimeMs) <= trailingCaptureWindowMs)
                        {
                            // No new frame, but within trailing window — reuse the last captured frame
                            lock (latestFrameLock)
                            {
                                if (latestArgbBuffer != null && latestWidth > 0 && latestHeight > 0)
                                {
                                    try
                                    {
                                        // Make a copy to avoid concurrent modification
                                        int len = latestArgbBuffer.Length;
                                        frameToProcess = new byte[len];
                                        Buffer.BlockCopy(latestArgbBuffer, 0, frameToProcess, 0, len);
                                        procStride = latestStride;
                                        procW = latestWidth;
                                        procH = latestHeight;
                                        //Console.WriteLine($"[Program] [Trailing] Reusing last frame (elapsed {nowMs - lastChangedFrameTimeMs}ms since last change)");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Program] Error copying last frame for trailing: {ex.Message}");
                                        frameToProcess = null;
                                    }
                                }
                            }
                        }

                        if (frameToProcess != null)
                        {
                            // Use the captured frame data for processing
                            argb = frameToProcess;
                            strideVal = procStride;
                            w = procW;
                            h = procH;

                            // Log frame capture details on first frame
                            if (frameCount == 0 && isNewFrame)
                            {
                                Console.WriteLine($"[Program] First frame captured: {w}x{h}, stride={strideVal}, buffer_size={argb.Length}, fps={targetFps}, bitrate={targetBitrate}");
                            }

                            // Encode and send via WebRTC
                            try
                            {
                                var softwareEncoder = connectionManager?.SoftwareEncoder;
                                if (softwareEncoder != null && connectionManager != null && connectionManager.IsConnected)
                                {
                                    // Calculate incremental timestamp based on elapsed time
                                    long currentTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    long elapsedMs = currentTimeMs - sessionStartMs;
                                    var ts = elapsedMs;

                                    // Convert ARGB->NV12
                                    int width = w;
                                    int height = h;
                                    int srcRowPitch = strideVal;
                                    int yPlaneSize = width * height;
                                    int uvPlaneSize = (width * height) / 2;
                                    int totalBytes = yPlaneSize + uvPlaneSize;
                                    var nv12 = ArrayPool<byte>.Shared.Rent(totalBytes);
                                    
                                    try
                                    {
                                        // BGRA -> NV12 conversion
                                        for (int yy = 0; yy < height; yy++)
                                        {
                                            int srcRow = yy * srcRowPitch;
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

                                        int uvIndex = yPlaneSize;
                                        for (int y2 = 0; y2 < height; y2 += 2)
                                        {
                                            for (int x2 = 0; x2 < width; x2 += 2)
                                            {
                                                int sumU = 0, sumV = 0;
                                                int count = 0;
                                                for (int yy2 = 0; yy2 < 2; yy2++)
                                                {
                                                    int py = y2 + yy2;
                                                    if (py >= height) continue;
                                                    int srcRow = py * srcRowPitch;
                                                    for (int xx2 = 0; xx2 < 2; xx2++)
                                                    {
                                                        int px = x2 + xx2;
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

                                        var encoded = await softwareEncoder.EncodeNv12FrameAsync(nv12, ts);
                                        if (encoded != null)
                                        {
                                            uint ts90 = (uint)(ts * 90);
                                            bool isKey = false;
                                            int nalHeaderIndex = 0;
                                            if (encoded.Length >= 4 && encoded[0] == 0 && encoded[1] == 0 && encoded[2] == 0 && encoded[3] == 1) 
                                                nalHeaderIndex = 4;
                                            else if (encoded.Length >= 3 && encoded[0] == 0 && encoded[1] == 0 && encoded[2] == 1) 
                                                nalHeaderIndex = 3;
                                            
                                            if (nalHeaderIndex < encoded.Length)
                                            {
                                                var nal = encoded[nalHeaderIndex];
                                                int nalType = (nal & 0x1F);
                                                // Only IDR frames (type 5) are true keyframes
                                                isKey = (nalType == 5);  // IDR only, NOT SPS(7) or PPS(8)
                                            }
                                            
                                            // Check if we need to force a keyframe
                                            long framesUntilNextKeyframe = frameCount - lastKeyframeFrameCount;
                                            if (framesUntilNextKeyframe >= keyframeInterval)
                                            {
                                                // Request keyframe for next frame
                                                softwareEncoder.FfmpegFallback?.RequestKeyframe();
                                                if  (frameCount % 10 == 0){ // Log every 10 frames
                                                // Console.WriteLine($"[Program] **KEYFRAME REQUEST**: Frame #{frameCount} (elapsed {framesUntilNextKeyframe} frames)");
                                                }
                                            }
                                            
                                            if (isKey)
                                            {
                                                if  (frameCount % 10 == 0){ // Log every 10 frames
                                                // Console.WriteLine($"[Program] **KEYFRAME DETECTED**: Frame #{frameCount}, NAL type={(nalHeaderIndex > 0 ? (encoded[nalHeaderIndex] & 0x1F) : 0)}");
                                                }
                                                lastKeyframeFrameCount = frameCount;
                                            }
                                            
                                            // Send frame via Connection Manager
                                            await connectionManager.SendFrameAsync(encoded, ts90, isKey);
                                        }
                                    }
                                    finally 
                                    { 
                                        try { ArrayPool<byte>.Shared.Return(nv12); } catch { } 
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Encode/send error: {ex}");
                            }

                            // Ensure MP4 writer and write sample
                            try
                            {
                                if (enableMp4Output && mp4Writer != null)
                                {
                                    mp4Writer.EnsureWriter(mp4Path, w, h, targetFps, targetBitrate);
                                    mp4Writer.WriteArgbFrame(argb!, strideVal, w, h);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"MP4 write error: {ex.Message}");
                            }

                            frameCount++;
                            if (maxFrames.HasValue && frameCount >= maxFrames.Value)
                            {
                                Console.WriteLine($"Reached max frames {maxFrames.Value}; stopping loop.");
                                cts.Cancel();
                            }
                            
                            if (frameCount % 100 == 0)
                            {
                                Console.WriteLine($"Captured frame #{frameCount} at {DateTime.Now:HH:mm:ss.fff}");
                            }
                        }

                        if (cts.Token.WaitHandle.WaitOne(2)) break;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error during capture loop: {ex}");
                    }
                }

                // Dispose mp4 writer if we created one
                try { mp4Writer?.Dispose(); } catch { }
            }
            finally
            {
                // Cleanup WebRTC Connection Manager
                if (connectionManager != null)
                {
                    try
                    {
                        Console.WriteLine("[Program] Stopping WebRTC connection manager...");
                        await connectionManager.StopAsync();
                        connectionManager.Dispose();
                        Console.WriteLine("[Program] WebRTC connection manager stopped");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Program] Error stopping connection manager: {ex.Message}");
                    }
                }

                // cleanup D3D (ScreenCapture.Dispose will do it via using)
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex}");
        }
    }

    // native memcpy
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    static extern void CopyMemory(IntPtr Destination, IntPtr Source, int Length);

    static void InitializeCapture(out ID3D11Device? device, out ID3D11DeviceContext? context, out IDXGIOutputDuplication? duplication)
    {
        var creationFlags = DeviceCreationFlags.BgraSupport;
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            creationFlags,
            Array.Empty<FeatureLevel>(),
            out device,
            out _,
            out context);

        if (device == null || context == null)
            throw new Exception("Failed to create D3D11 device/context.");

        var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var factory = dxgiDevice.GetAdapter().GetParent<IDXGIFactory>();

        uint adapterIndex = 0;
        IDXGIAdapter? adapter = null;
        IDXGIOutput? output = null;

        while (factory.EnumAdapters(adapterIndex, out var tempAdapter) == 0)
        {
            try
            {
                if (tempAdapter.EnumOutputs(0, out output) == 0)
                {
                    adapter = tempAdapter;
                    break;
                }
                tempAdapter.Dispose();
            }
            catch
            {
                tempAdapter.Dispose();
            }
            adapterIndex++;
        }

        if (adapter == null || output == null)
            throw new Exception("No adapter with valid output found");

        var output1 = output.QueryInterface<IDXGIOutput1>();
        duplication = output1.DuplicateOutput(device);

        output1.Dispose();
        output.Dispose();
        adapter.Dispose();
        dxgiDevice.Dispose();
        factory.Dispose();
    }
}