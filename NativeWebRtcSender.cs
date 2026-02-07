using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;
using System.Diagnostics;
namespace DxgiCapture { 
    // ================================================================================= 
    // 1. C# DELEGATES FOR NATIVE CALLBACKS 
    // =================================================================================
    // Callback when the Native WebRTC stack generates an SDP offer.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SdpOfferReadyCallback(IntPtr peerConnectionHandle, [MarshalAs(UnmanagedType.LPStr)] string sdp);
    // Callback when the Native WebRTC stack generates a new ICE candidate.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void IceCandidateReadyCallback(IntPtr peerConnectionHandle, [MarshalAs(UnmanagedType.LPStr)] string candidate, [MarshalAs(UnmanagedType.LPStr)] string sdpMid, int sdpMLineIndex);
    // Callback for connection status changes (e.g., Connected, Disconnected).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ConnectionStateChangedCallback(IntPtr peerConnectionHandle, int state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DataChannelMessageCallback(IntPtr pcHandle, int dcId, IntPtr data, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DataChannelStateCallback(IntPtr pcHandle, int dcId, int state);

    // =================================================================================
    // 2. P/INVOKE DECLARATIONS (Communication with webrtc_native.dll)
    // =================================================================================
    public static class NativeWebRtc
    {
        private const string DllName = "webrtc_native.dll";
        // Initialization and Creation
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CreatePeerConnection(
        SdpOfferReadyCallback sdpOfferCallback,
        IceCandidateReadyCallback iceCandidateCallback,
        ConnectionStateChangedCallback stateChangeCallback,
        [MarshalAs(UnmanagedType.LPStr)] string? turnUrl,
        [MarshalAs(UnmanagedType.LPStr)] string? turnUsername,
        [MarshalAs(UnmanagedType.LPStr)] string? turnPassword);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void DestroyPeerConnection(IntPtr pcHandle);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetRemoteSdpAnswer(IntPtr pcHandle, [MarshalAs(UnmanagedType.LPStr)] string sdpAnswer);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AddRemoteIceCandidate(IntPtr pcHandle, [MarshalAs(UnmanagedType.LPStr)] string candidate, [MarshalAs(UnmanagedType.LPStr)] string sdpMid, int sdpMLineIndex);
    // Media Stream Configuration
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void AddVideoTrack(
        IntPtr pcHandle,
        [MarshalAs(UnmanagedType.LPStr)] string codecName,
        [MarshalAs(UnmanagedType.LPStr)] string spropParameterSets,
        int bitrateKbps /*Initial target bitrate in Kbps*/); 

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetTargetBitrate(IntPtr pcHandle, int bitrateKbps);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetTargetBitrate(IntPtr pcHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ShouldGenerateKeyframe(IntPtr pcHandle);

    // H.264 Frame Injection
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void InjectEncodedFrame(
        IntPtr pcHandle,
        [In] byte[] data,
        int dataLength,
        long timestampUs, // Timestamp in microseconds
        bool isKeyFrame);

        // ✅ DataChannel Functions
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetDataChannelCallbacks(IntPtr pcHandle,
        DataChannelMessageCallback messageCallback,
        DataChannelStateCallback stateCallback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CreateDataChannel(IntPtr pcHandle,
        [MarshalAs(UnmanagedType.LPStr)] string label,
        bool ordered,
        bool reliable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool SendDataChannelMessage(IntPtr pcHandle, 
        int dcId, 
        [MarshalAs(UnmanagedType.LPStr)] string message);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool SendDataChannelBinary(IntPtr pcHandle, 
        int dcId, 
        byte[] data, 
        int length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TriggerOfferGeneration(IntPtr pcHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseDataChannel(IntPtr pcHandle, int dcId);

    }
    // =================================================================================
    // 3. C# WRAPPER CLASS (IWebRtcSender Implementation)
    // =================================================================================
    public class NativeWebRtcSender : IWebRtcSender
    {
        // Events to hand off local signaling elements to an external signaling client
        public event Func<string, Task>? OnLocalSdpOffer; // sdp
        public event Action<string, string, int>? OnLocalIceCandidate; // candidate, sdpMid, sdpMLineIndex
        public event Action<int>? OnLocalConnectionStateChanged; // state code
        public event Action<int, string>? OnDataChannelMessage;
        public event Action<int, byte[]>? OnDataChannelBinaryMessage;
        public event Action<int, bool>? OnDataChannelStateChanged;
        private IntPtr _nativePeerConnectionHandle;
        private object _handleLock = new object();  // Protect handle access during concurrent operations
        /// <summary>
        /// Public property to expose the native peer connection handle for cleanup/destruction
        /// </summary>
        public IntPtr NativePeerConnectionHandle
        {
            get { lock (_handleLock) { return _nativePeerConnectionHandle; } }
        }

        private TaskCompletionSource<string> _sdpOfferCompletionSource;
        private string _spropParameterSets;
        // Optional signaling server configuration
        private string _serverBase = string.Empty;
        private string _sessionId = string.Empty;
        private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();
        // TaskCompletionSource used to wait for connection establishment (signaled by native callback)
        private TaskCompletionSource<bool> _connectedTcs;
        // Monitor for RTCP feedback (PLI/FIR) requests
        private System.Threading.CancellationTokenSource? _rtcpMonitorCts;
        private Task? _rtcpMonitorTask;

        // Event to notify when native requests a keyframe
        public event Action? OnKeyFrameRequested;
        // Keep delegates alive to prevent Garbage Collector from cleaning them up
        private SdpOfferReadyCallback _sdpOfferCallback;
        private IceCandidateReadyCallback _iceCandidateCallback;
        private ConnectionStateChangedCallback _stateChangeCallback;
        private DataChannelMessageCallback _dcMessageCallback;
        private DataChannelStateCallback _dcStateCallback;
        // Track created data channels (id -> label)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _dataChannels = new();
        public NativeWebRtcSender()
        {
            _sdpOfferCompletionSource = new TaskCompletionSource<string>();
            _spropParameterSets = string.Empty;
            _sdpOfferCallback = null!;
            _iceCandidateCallback = null!;
            _stateChangeCallback = null!;
            // provide no-op initial data channel callbacks to satisfy non-nullable delegates
            _dcMessageCallback = (h, id, data, len) => { };
            _dcStateCallback = (h, id, st) => { };
            _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Native -> C# bridge for incoming datachannel messages
        private void OnDataChannelMessageNative(IntPtr pcHandle, int dcId, IntPtr data, int length)
        {
            try
            {
                string label = "unknown";
                try { _dataChannels.TryGetValue(dcId, out label); } catch { }
                Console.WriteLine($"[C# NativeWebRtcSender] OnDataChannelMessageNative: dcId={dcId}, label={label}, len={length}");

                if (length <= 0 || data == IntPtr.Zero)
                {
                    OnDataChannelMessage?.Invoke(dcId, string.Empty);
                    return;
                }

                var buffer = new byte[length];
                try { System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, length); } catch { }

                // Try to decode as UTF8 text
                string? text = null;
                try
                {
                    text = System.Text.Encoding.UTF8.GetString(buffer);
                }
                catch { text = null; }

                if (!string.IsNullOrEmpty(text) && IsMostlyPrintable(text))
                {
                    Console.WriteLine($"[C# NativeWebRtcSender] 📩 Text message from dcId={dcId}: {text.Substring(0, Math.Min(200, text.Length))}{(text.Length>200?"...":"")}");
                    OnDataChannelMessage?.Invoke(dcId, text);
                }
                else
                {
                    Console.WriteLine($"[C# NativeWebRtcSender] 📩 Binary message from dcId={dcId}: {buffer.Length} bytes");
                    OnDataChannelBinaryMessage?.Invoke(dcId, buffer);
                }
            }
            catch { }
        }

        private bool IsMostlyPrintable(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int printable = 0;
            foreach (var ch in s)
            {
                if (ch >= 32 && ch <= 126) printable++; else if (char.IsWhiteSpace(ch)) printable++;
            }
            return ((double)printable / s.Length) > 0.6;
        }

        private void OnDataChannelStateNative(IntPtr pcHandle, int dcId, int state)
        {
            try
            {
                bool isOpen = state != 0;
                string label = "unknown";
                try { _dataChannels.TryGetValue(dcId, out label); } catch { }
                Console.WriteLine($"[C# NativeWebRtcSender] DataChannel state change: dcId={dcId}, label={label}, rawState={state}, isOpen={isOpen}");
                OnDataChannelStateChanged?.Invoke(dcId, isOpen);
            }
            catch { }
        }

        // Create a data channel on the native PeerConnection. Returns data channel id (>=0) or -1
        public int CreateDataChannel(string label, bool ordered = true, bool reliable = true)
        {
            try
            {
                if (_nativePeerConnectionHandle == IntPtr.Zero) return -1;
                int id = NativeWebRtc.CreateDataChannel(_nativePeerConnectionHandle, label ?? string.Empty, ordered, reliable);
                if (id >= 0) _dataChannels.TryAdd(id, label ?? string.Empty);
                return id;
            }
            catch { return -1; }
        }

        public bool SendDataChannelMessage(int dcId, string message)
        {
            try
            {
                if (_nativePeerConnectionHandle == IntPtr.Zero) return false;
                return NativeWebRtc.SendDataChannelMessage(_nativePeerConnectionHandle, dcId, message ?? string.Empty);
            }
            catch { return false; }
        }

        public bool SendDataChannelBinary(int dcId, byte[] data)
        {
            try
            {
                if (_nativePeerConnectionHandle == IntPtr.Zero) return false;
                return NativeWebRtc.SendDataChannelBinary(_nativePeerConnectionHandle, dcId, data ?? new byte[0], data?.Length ?? 0);
            }
            catch { return false; }
        }

        public void CloseDataChannel(int dcId)
        {
            try
            {
                if (_nativePeerConnectionHandle == IntPtr.Zero) return;
                NativeWebRtc.CloseDataChannel(_nativePeerConnectionHandle, dcId);
                _dataChannels.TryRemove(dcId, out _);
            }
            catch { }
        }
        public void Initialize(string spropParameterSets, string? turnUrl = null, string? turnUsername = null, string? turnPassword = null, string? serverBase = null, string? sessionId = null, int initialBitrate = 1500)
        {
            _spropParameterSets = spropParameterSets;
            // serverBase/sessionId are no longer required; external PollingSignalingClient will handle POSTing
            if (!string.IsNullOrWhiteSpace(serverBase)) _serverBase = serverBase!;
            if (!string.IsNullOrWhiteSpace(sessionId)) _sessionId = sessionId!;
            // Initialize Callbacks (must be stored as fields to prevent GC)
            _sdpOfferCallback = OnSdpOfferReady;
            _iceCandidateCallback = OnIceCandidateReady;
            _stateChangeCallback = OnConnectionStateChanged;
            //Console.WriteLine("[NativeWebRTC] Callbacks initialized and assigned to delegate fields");
            // 1. Create Native Peer Connection (pass TURN parameters which may be null)
            //Console.WriteLine("[NativeWebRTC] Calling CreatePeerConnection...");
            _nativePeerConnectionHandle = NativeWebRtc.CreatePeerConnection(
                _sdpOfferCallback,
                _iceCandidateCallback,
                _stateChangeCallback,
                turnUrl ?? string.Empty,
                turnUsername ?? string.Empty,
                turnPassword ?? string.Empty
            );
            if (_nativePeerConnectionHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create Native Peer Connection.");
            }
            // 2. Add H.264 Track FIRST (before datachannel) to ensure it's included in SDP offer
            //Console.WriteLine("[NativeWebRTC] Calling AddVideoTrack with sprop=" + (_spropParameterSets ?? "EMPTY"));
            NativeWebRtc.AddVideoTrack(
                _nativePeerConnectionHandle, 
                "H264", 
                _spropParameterSets,
                initialBitrate
            );

            //Console.WriteLine($"[NativeWebRTC] Native peer connection handle: {_nativePeerConnectionHandle}");
            // ✅ 2. Register DataChannel callbacks FIRST (before AddVideoTrack)
            try
            {
                _dcMessageCallback = OnDataChannelMessageNative;
                _dcStateCallback = OnDataChannelStateNative;
                NativeWebRtc.SetDataChannelCallbacks(_nativePeerConnectionHandle, _dcMessageCallback, _dcStateCallback);
                Console.WriteLine("[C# NativeWebRtcSender] DataChannel callbacks registered EARLY");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[C# NativeWebRtcSender] Failed to register datachannel callbacks: {ex.Message}");
            }

            // ✅ 3. Create control datachannel BEFORE AddVideoTrack
            try
            {
                Console.WriteLine("[C# NativeWebRtcSender] Creating control datachannel BEFORE AddVideoTrack...");
                int cid = NativeWebRtc.CreateDataChannel(_nativePeerConnectionHandle, "control", true, true);
                
                if (cid >= 0)
                {
                    _dataChannels.TryAdd(cid, "control");
                    Console.WriteLine($"[C# NativeWebRtcSender] ✅ Created control datachannel id={cid} BEFORE video track");
                }
                else
                {
                    Console.WriteLine("[C# NativeWebRtcSender] ❌ CreateDataChannel returned -1");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[C# NativeWebRtcSender] ❌ Exception creating datachannel: {ex.Message}");
            }


            //Console.WriteLine("[NativeWebRTC] Peer Connection and H.264 Track initialized.");

            // Register DataChannel callbacks so native can forward messages/state
            try
            {
                _dcMessageCallback = OnDataChannelMessageNative;
                _dcStateCallback = OnDataChannelStateNative;
                NativeWebRtc.SetDataChannelCallbacks(_nativePeerConnectionHandle, _dcMessageCallback, _dcStateCallback);

                Console.WriteLine("[C# NativeWebRtcSender] DataChannel callbacks registered with native DLL");

                // Create a default control datachannel AFTER video track
                try
                {
                    int cid = NativeWebRtc.CreateDataChannel(_nativePeerConnectionHandle, "control", true, true);
                    if (cid >= 0)
                    {
                        _dataChannels.TryAdd(cid, "control");
                        Console.WriteLine($"[C# NativeWebRtcSender] Created control datachannel id={cid}");
                    }
                    else
                    {
                        Console.WriteLine("[C# NativeWebRtcSender] CreateDataChannel returned -1 (datachannel not supported by native build or failed to create)");
                    }
                }
                catch { }
            }
            catch (Exception)
            {
                // ignore - not all native builds may expose datachannel yet
            }

            // Start RTCP feedback monitor to detect PLI/FIR requests from native
            try
            {
                _rtcpMonitorCts = new System.Threading.CancellationTokenSource();
                _rtcpMonitorTask = Task.Run(() => RtcpMonitorLoop(_rtcpMonitorCts.Token));
                //Console.WriteLine("[NativeWebRTC] RTCP feedback monitor started.");
            }
            catch (Exception)
            {
                //Console.WriteLine($"[NativeWebRTC] Failed to start RTCP monitor: {ex.Message}");
            }
        }

        private async Task RtcpMonitorLoop(System.Threading.CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_nativePeerConnectionHandle != IntPtr.Zero)
                        {
                            bool should = NativeWebRtc.ShouldGenerateKeyframe(_nativePeerConnectionHandle);
                            if (should)
                            {
                                //Console.WriteLine("[NativeWebRTC] Native indicated keyframe requested (ShouldGenerateKeyframe==true)");
                                try { OnKeyFrameRequested?.Invoke(); } catch { }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        //Console.WriteLine($"[NativeWebRTC] RTCP monitor error: {ex.Message}");
                    }
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /*Console.WriteLine($"[NativeWebRTC] RTCP monitor fatal error: {ex.Message}");*/ }
        }
        public async Task<string> GetLocalSdpOfferAsync()
        {
            lock (_handleLock)
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    Console.WriteLine("[NativeWebRTC] 🎬 Explicitly triggering SDP offer generation...");
                    try
                    {
                        NativeWebRtc.TriggerOfferGeneration(_nativePeerConnectionHandle);
                        Console.WriteLine("[NativeWebRTC] ✅ Offer generation triggered successfully");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NativeWebRTC] ❌ TriggerOfferGeneration failed: {ex.Message}");
                        // Continue anyway - maybe old DLL doesn't have this function
                    }
                }
            }
            // Wait for the native stack to generate the offer (with 10 second timeout)
            try
            {
                var offerTask = _sdpOfferCompletionSource.Task;
                var delayTask = Task.Delay(10000); // 10 second timeout
                var completedTask = await Task.WhenAny(offerTask, delayTask).ConfigureAwait(false);
                
                if (completedTask == offerTask)
                {
                    var offer = await offerTask.ConfigureAwait(false);
                    //Console.WriteLine($"[NativeWebRTC] GetLocalSdpOfferAsync: offer received (len={offer?.Length ?? 0})");
                    return offer ?? string.Empty;
                }
                else
                {
                    // Timeout occurred - reset completion source for next attempt
                    Console.WriteLine("[NativeWebRTC] GetLocalSdpOfferAsync: timeout waiting for native offer (10s)");
                    _sdpOfferCompletionSource = new TaskCompletionSource<string>();
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeWebRTC] GetLocalSdpOfferAsync error: {ex.Message}");
                return string.Empty;
            }
        }
        // Start streaming frames to the remote peer. Implementation depends on app-level frame pumping.
        public Task StartStreamingAsync()
        {
            //Console.WriteLine("[NativeWebRTC] StartStreamingAsync called");
            // No-op here; actual frame injection is performed by SendEncodedFrameAsync when frames are available.
            return Task.CompletedTask;
        }
        // Stop streaming frames to the remote peer.
        public Task StopStreamingAsync()
        {
            //Console.WriteLine("[NativeWebRTC] StopStreamingAsync called");
            // No-op placeholder; caller should stop calling SendEncodedFrameAsync.
            return Task.CompletedTask;
        }
        public Task SetRemoteSdpAnswerAsync(string sdpAnswer)
        {
            //Console.WriteLine("[NativeWebRTC] Setting Remote SDP Answer...");
            // Safety check: validate handle and SDP before calling native
            lock (_handleLock)
            {
                if (_nativePeerConnectionHandle == IntPtr.Zero)
                {
                    //Console.WriteLine("[NativeWebRTC] ERROR: Peer connection handle is zero; cannot set remote SDP answer. Peer connection may have been destroyed.");
                    return Task.CompletedTask;
                }
                if (string.IsNullOrWhiteSpace(sdpAnswer))
                {
                    //Console.WriteLine("[NativeWebRTC] ERROR: SDP answer is null or empty; skipping SetRemoteSdpAnswer.");
                    return Task.CompletedTask;
                }
                try
                {
                    //Console.WriteLine($"[NativeWebRTC] Calling native SetRemoteSdpAnswer with handle={_nativePeerConnectionHandle}, sdp_len={sdpAnswer.Length}");
                    NativeWebRtc.SetRemoteSdpAnswer(_nativePeerConnectionHandle, sdpAnswer);
                    //Console.WriteLine("[NativeWebRTC] Remote SDP answer set successfully.");
                }
                catch (Exception)
                {
                    //Console.WriteLine($"[NativeWebRTC] EXCEPTION in SetRemoteSdpAnswer: {ex}");
                }
            }
            return Task.CompletedTask;
        }

        // Bitrate control helpers
        public void SetTargetBitrate(int bitrateKbps)
        {
            try
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    NativeWebRtc.SetTargetBitrate(_nativePeerConnectionHandle, bitrateKbps);
                }
            }
            catch (Exception)
            {
                //Console.WriteLine($"[NativeWebRTC] SetTargetBitrate error: {ex.Message}");
            }
        }

        public int GetTargetBitrate()
        {
            try
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    return NativeWebRtc.GetTargetBitrate(_nativePeerConnectionHandle);
                }
            }
            catch (Exception)
            {
                //Console.WriteLine($"[NativeWebRTC] GetTargetBitrate error: {ex.Message}");
            }
            return 0;
        }

        public bool ShouldGenerateKeyframe()
        {
            try
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    return NativeWebRtc.ShouldGenerateKeyframe(_nativePeerConnectionHandle);
                }
            }
            catch (Exception)
            {
                //Console.WriteLine($"[NativeWebRTC] ShouldGenerateKeyframe error: {ex.Message}");
            }
            return false;
        }
        public Task SendEncodedFrameAsync(byte[] nalUnit, uint timestamp, bool isKeyFrame)
        {
            try
            {
                //Console.WriteLine($"[NativeWebRTC C#] SendEncodedFrameAsync called - len={nalUnit?.Length ?? 0}, ts90={timestamp}, isKey={isKeyFrame}");
                lock (_handleLock)
                {
                    if (_nativePeerConnectionHandle == IntPtr.Zero)
                    {
                        //Console.WriteLine("[NativeWebRTC C#] Warning: native peer connection handle is zero; not injecting frame.");
                        return Task.CompletedTask;
                    }

                    if (nalUnit == null)
                    {
                        //Console.WriteLine("[NativeWebRTC C#] Warning: nalUnit is null; skipping injection.");
                        return Task.CompletedTask;
                    }

                    // 3. Frame Injection (Conversion from 90kHz ticks to microseconds)
                    long timestampUs = (long)timestamp * 1000L / 90L;
                    // Console.WriteLine($"[NativeWebRTC C#] Injecting frame to native DLL (len={(nalUnit?.Length ?? 0)}, tsUs={timestampUs}, handle={_nativePeerConnectionHandle})");
                    var payload = nalUnit!;
                    NativeWebRtc.InjectEncodedFrame(
                        _nativePeerConnectionHandle,
                        payload,
                        payload.Length,
                        timestampUs,
                        isKeyFrame
                    );
                    // Console.WriteLine($"[NativeWebRTC C#] InjectEncodedFrame call completed (len={payload.Length})");
                }
            }
            catch (Exception)
            {
                //Console.WriteLine($"[NativeWebRTC C#] SendEncodedFrameAsync error: {ex}");
            }
            return Task.CompletedTask;
        }
        // 4. SIGNALING BRIDGE IMPLEMENTATION (C# Callbacks)
        private void OnSdpOfferReady(IntPtr pcHandle, string sdp)
        {
            Debug.Assert(pcHandle == _nativePeerConnectionHandle, "Handle mismatch in SDP callback.");
            //Console.WriteLine($"[NativeWebRTC] *** OnSdpOfferReady CALLBACK INVOKED *** (len={sdp?.Length ?? 0})");
            if (string.IsNullOrWhiteSpace(sdp))
            {
                //Console.WriteLine("[NativeWebRTC] WARNING: SDP offer is null or empty!");
                return;
            }
            //Console.WriteLine($"[NativeWebRTC] SDP Offer received (len={sdp?.Length ?? 0})");
            // Surface the local SDP offer to any registered handler (e.g., PollingSignalingClient)
            // Only set result if not already completed to prevent ObjectDisposedException
            if (!_sdpOfferCompletionSource.Task.IsCompleted)
            {
                try
                {
                    _sdpOfferCompletionSource.SetResult(sdp ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NativeWebRTC] Warning: Could not set SDP result: {ex.Message}");
                }
            }
            
            try
            {
                var handler = OnLocalSdpOffer;
                if (handler != null)
                {
                    // Run the handler asynchronously so we don't block the native callback thread
                    _ = Task.Run(async () =>
                    {
                        try { await handler.Invoke(sdp ?? string.Empty); }
                        catch (Exception) { /*Console.WriteLine($"[NativeWebRTC] OnLocalSdpOffer handler error: {ex.Message}");*/ }
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeWebRTC] Offer handler invocation error: {ex.Message}");
            }
        }
        private void OnIceCandidateReady(IntPtr pcHandle, string candidate, string sdpMid, int sdpMLineIndex)
        {
            Debug.Assert(pcHandle == _nativePeerConnectionHandle, "Handle mismatch in ICE callback.");
            
            // Remove 'a=' prefix if present (RFC 8839 requires candidate string without SDP line prefix)
            string cleanCandidate = candidate ?? string.Empty;
            if (cleanCandidate.StartsWith("a=candidate:", StringComparison.Ordinal))
            {
                cleanCandidate = cleanCandidate.Substring(2);  // Remove "a="
                //Console.WriteLine($"[NativeWebRTC] Removed 'a=' prefix from candidate");
            }
            
            //Console.WriteLine($"[NativeWebRTC] ICE Candidate generated: {cleanCandidate.Substring(0, Math.Min(80, cleanCandidate.Length))} (Mid: {sdpMid}, Index: {sdpMLineIndex})");
            
            // Notify any registered handler with cleaned candidate
            try
            {
                OnLocalIceCandidate?.Invoke(cleanCandidate, sdpMid ?? "0", sdpMLineIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeWebRTC] LocalIceCandidate handler error: {ex.Message}");
            }
        }

        public Task AddRemoteIceCandidateAsync(string candidate, string sdpMid, int sdpMLineIndex)
        {
            try
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    NativeWebRtc.AddRemoteIceCandidate(_nativePeerConnectionHandle, candidate ?? string.Empty, sdpMid ?? string.Empty, sdpMLineIndex);
                }
            }
            catch (Exception) { /*Console.WriteLine($"[NativeWebRTC] AddRemoteIceCandidateAsync error: {ex}");*/ }
            return Task.CompletedTask;
        }
        /// <summary>
        /// Invalidate the peer connection handle after native destruction.
        /// Call this after DestroyPeerConnection to prevent further operations on stale handle.
        /// </summary>
        public void InvalidateHandle()
        {
            lock (_handleLock)
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    //Console.WriteLine($"[NativeWebRTC] Invalidating peer connection handle: {_nativePeerConnectionHandle}");
                    _nativePeerConnectionHandle = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// Reinitialize the peer connection after it has been destroyed.
        /// Call this after cleanup to create a fresh peer connection for new sessions.
        /// </summary>
        public void ReinitializePeerConnection(string spropParameterSets)
        {
            // Reset completion sources for new peer connection lifecycle
            _sdpOfferCompletionSource = new TaskCompletionSource<string>();
            _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _spropParameterSets = spropParameterSets;
            
            // Reinitialize Callbacks (must be stored as fields to prevent GC)
            _sdpOfferCallback = OnSdpOfferReady;
            _iceCandidateCallback = OnIceCandidateReady;
            _stateChangeCallback = OnConnectionStateChanged;
            
            // Stop any existing RTCP monitor before recreating peer connection
            try
            {
                if (_rtcpMonitorCts != null)
                {
                    _rtcpMonitorCts.Cancel();
                    _rtcpMonitorCts.Dispose();
                    _rtcpMonitorCts = null;
                }
            }
            catch { }
            
            // Create new Native Peer Connection
            lock (_handleLock)
            {
                if (_nativePeerConnectionHandle != IntPtr.Zero)
                {
                    // If still has old handle, destroy it first
                    try { NativeWebRtc.DestroyPeerConnection(_nativePeerConnectionHandle); } catch { }
                }
                
                _nativePeerConnectionHandle = NativeWebRtc.CreatePeerConnection(
                    _sdpOfferCallback,
                    _iceCandidateCallback,
                    _stateChangeCallback,
                    string.Empty,
                    string.Empty,
                    string.Empty
                );
            }
            
            if (_nativePeerConnectionHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create new Native Peer Connection during reinitialization.");
            }
            
            NativeWebRtc.AddVideoTrack(
                _nativePeerConnectionHandle,
                "H264",
                _spropParameterSets,
                1500  // Default bitrate
            );

    // ✅ Register data channel callbacks and create control channel AFTER video
            try
            {
                _dcMessageCallback = OnDataChannelMessageNative;
                _dcStateCallback = OnDataChannelStateNative;
                NativeWebRtc.SetDataChannelCallbacks(_nativePeerConnectionHandle, _dcMessageCallback, _dcStateCallback);
                    try
                    {
                        Console.WriteLine("[C# NativeWebRtcSender] Creating control datachannel BEFORE AddVideoTrack...");
                        int cid = NativeWebRtc.CreateDataChannel(_nativePeerConnectionHandle, "control", true, true);
                        if (cid >= 0)
                        {
                            _dataChannels.TryAdd(cid, "control");
                            Console.WriteLine($"[C# NativeWebRtcSender] ✅ Created control datachannel id={cid} BEFORE video track");
                        }
                        else
                        {
                            Console.WriteLine("[C# NativeWebRtcSender] ❌ CreateDataChannel returned -1");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[C# NativeWebRtcSender] ❌ Exception creating datachannel: {ex.Message}");
                    }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[C# NativeWebRtcSender] Failed to register datachannel callbacks: {ex.Message}");
            }


            // Restart RTCP feedback monitor for the new peer connection
            try
            {
                _rtcpMonitorCts = new System.Threading.CancellationTokenSource();
                _rtcpMonitorTask = Task.Run(() => RtcpMonitorLoop(_rtcpMonitorCts.Token));
                Console.WriteLine("[NativeWebRTC] RTCP feedback monitor restarted after peer connection reinitialization.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NativeWebRTC] Failed to restart RTCP monitor: {ex.Message}");
            }
        }

        // Cleanup and destroy the native peer connection asynchronously.
        public Task CleanupPeerConnectionAsync()
        {
            try
            {
                Dispose();
                return Task.CompletedTask;
            }
            catch (Exception) { /*Console.WriteLine($"[NativeWebRTC] CleanupPeerConnectionAsync error: {ex}");*/ return Task.CompletedTask; }
        }
        private void OnConnectionStateChanged(IntPtr pcHandle, int state)
        {
            Debug.Assert(pcHandle == _nativePeerConnectionHandle, "Handle mismatch in State callback.");
            // Log connection state with readable name
            string name = state switch
            {
                0 => "New",
                1 => "Connecting",
                2 => "Connected",
                3 => "Disconnected",
                4 => "Failed",
                5 => "Closed",
                _ => "Unknown"
            };
            Console.WriteLine($"[C# NativeWebRtcSender] Connection State Changed: code={state} name={name}");
            // Some native stacks use 2 for 'connected' state. Signal the waiting task when connected.
            if (state == 2)
            {
                try { _connectedTcs.TrySetResult(true); } catch { }
            }
            try
            {
                OnLocalConnectionStateChanged?.Invoke(state);
            }
            catch { }
        }

        /// <summary>
        /// Wait asynchronously for the native WebRTC connection to reach the connected state.
        /// Throws TimeoutException if the connection is not established within <paramref name="timeoutMs"/>.
        /// </summary>
        public async Task WaitForConnectionAsync(int timeoutMs = 90000)
        {
            var delay = Task.Delay(timeoutMs);
            var winner = await Task.WhenAny(_connectedTcs.Task, delay).ConfigureAwait(false);
            if (winner != _connectedTcs.Task)
            {
                throw new TimeoutException("WebRTC connection not established within timeout.");
            }
        }
        public void Dispose()
        {
            if (_nativePeerConnectionHandle != IntPtr.Zero)
            {
                NativeWebRtc.DestroyPeerConnection(_nativePeerConnectionHandle);
                _nativePeerConnectionHandle = IntPtr.Zero;
                //Console.WriteLine("[NativeWebRTC] Peer Connection destroyed.");
            }
        }
    }
}