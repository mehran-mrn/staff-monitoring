using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DxgiCapture
{
    /// <summary>
    /// Manages WebRTC connection lifecycle with automatic reconnection.
    /// Handles cleanup, state transitions, and reconnection logic.
    /// </summary>
    public class WebRTCConnectionManager : IDisposable
    {
        private enum ConnectionState
        {
            Idle,           // هیچ connection فعال نیست
            Connecting,     // در حال برقراری ارتباط
            Connected,      // ارتباط برقرار است
            Disconnecting,  // در حال قطع ارتباط
            Failed,         // خطا در ارتباط
            Reconnecting    // در حال تلاش برای reconnect
        }

        // Components
        private IWebRtcSender? _webRtcSender;
        private SignalingClient? _signalingClient;
        private SoftwareEncoder? _softwareEncoder;

        // State tracking
        private ConnectionState _state = ConnectionState.Idle;
        private readonly object _stateLock = new object();
        private CancellationTokenSource? _reconnectCts;
        private Task? _reconnectTask;
        
        // Remote description tracking
        private bool _remoteDescriptionSet = false;
        private readonly List<(string candidate, string mid, int index)> _pendingCandidates = new();

        // LOCAL ICE candidate buffering
        private bool _offerSent = false;
        private readonly List<(string candidate, string mid, int index)> _localCandidateBuffer = new();
        private readonly object _localCandidateLock = new object();

        // Configuration
        private readonly string _serverBase;
        private readonly string _clientKey;
        // Optional TURN config
        private readonly string? _turnUrl;
        private readonly string? _turnUsername;
        private readonly string? _turnPassword;
        private readonly int _maxReconnectAttempts;
        private readonly int _reconnectDelayMs;
        private int _reconnectAttempts = 0;

        // Events
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<int>? OnReconnectAttempt; // attempt number
        public event Action<string>? OnLog;
        public event Action? OnKeyFrameRequested;
        // Signaling-level viewer presence events
        public event Action? OnViewerJoined;
        public event Action? OnViewerLeft;
        // DataChannel passthrough events
        public event Action<int, string>? OnDataChannelMessage;
        public event Action<int, byte[]>? OnDataChannelBinaryMessage;
        public event Action<int, bool>? OnDataChannelStateChanged;

        // Properties
        public bool IsConnected => _state == ConnectionState.Connected;
        public IWebRtcSender? WebRtcSender => _webRtcSender;
        public SignalingClient? SignalingClient => _signalingClient;
        public SoftwareEncoder? SoftwareEncoder => _softwareEncoder;

        public WebRTCConnectionManager(
            string serverBase,
            string clientKey,
            int maxReconnectAttempts = 5,
            int reconnectDelayMs = 2000,
            string? turnUrl = null,
            string? turnUsername = null,
            string? turnPassword = null)
        {
            _serverBase = serverBase ?? throw new ArgumentNullException(nameof(serverBase));
            _clientKey = clientKey ?? throw new ArgumentNullException(nameof(clientKey));
            _turnUrl = string.IsNullOrWhiteSpace(turnUrl) ? null : turnUrl;
            _turnUsername = string.IsNullOrWhiteSpace(turnUsername) ? null : turnUsername;
            _turnPassword = string.IsNullOrWhiteSpace(turnPassword) ? null : turnPassword;
            _maxReconnectAttempts = maxReconnectAttempts;
            _reconnectDelayMs = reconnectDelayMs;
        }

        /// <summary>
        /// Initialize and start the WebRTC connection.
        /// </summary>
        public async Task<bool> StartAsync(int targetFps = 15, int targetBitrate = 5000000)
        {
            lock (_stateLock)
            {
                if (_state != ConnectionState.Idle && _state != ConnectionState.Failed)
                {
                    Log($"Cannot start: current state is {_state}");
                    return false;
                }
                _state = ConnectionState.Connecting;
            }

            try
            {
                Log("Starting WebRTC connection...");

                // Reset flags
                _offerSent = false;
                lock (_localCandidateLock)
                {
                    _localCandidateBuffer.Clear();
                }

                // 1. Initialize Software Encoder
                _softwareEncoder = new SoftwareEncoder();
                await _softwareEncoder.InitializeAsync(1920, 1080, targetFps, targetBitrate);
                Log("Software encoder initialized");

                // 2. Get sprop-parameter-sets
                string? sprop = _softwareEncoder.FfmpegFallback?.GetSpropParameterSets();
                if (string.IsNullOrWhiteSpace(sprop))
                {
                    sprop = _softwareEncoder.GetSdpFmtpParameters().SpropParameterSet;
                }

                if (string.IsNullOrWhiteSpace(sprop))
                {
                    Log("Warning: sprop-parameter-sets is empty");
                    sprop = "";
                }
                else
                {
                    Log($"Got sprop-parameter-sets: {sprop.Substring(0, Math.Min(50, sprop.Length))}...");
                }

                // 3. Create Native WebRTC Sender
                _webRtcSender = new NativeWebRtcSender();
                // Pass TURN parameters (if configured) into the native sender initialize path
                try
                {
                    if (!string.IsNullOrWhiteSpace(_turnUrl))
                    {
                        var maskedUser = string.IsNullOrWhiteSpace(_turnUsername) ? "(none)" : _turnUsername.Substring(0, Math.Min(4, _turnUsername.Length)) + "***";
                        Log($"Initializing native sender with TURN server={_turnUrl}, user={maskedUser}");
                    }
                    _webRtcSender.Initialize(sprop, _turnUrl, _turnUsername, _turnPassword);
                }
                catch (Exception ex)
                {
                    Log($"Warning: failed to initialize native sender with TURN config: {ex.Message}");
                    // fallback: initialize without explicit TURN
                    _webRtcSender.Initialize(sprop);
                }
                Log("Native WebRTC sender initialized");

                // 4. Subscribe to native connection state changes
                var nativeSender = _webRtcSender as NativeWebRtcSender;
                if (nativeSender != null)
                {
                    nativeSender.OnLocalConnectionStateChanged += HandleNativeStateChange;
                    nativeSender.OnKeyFrameRequested += () => 
                    {
                        try { OnKeyFrameRequested?.Invoke(); } catch { }
                    };

                    // Set initial bitrate
                    try
                    {
                        int kbps = Math.Max(150, targetBitrate / 1000);
                        nativeSender.SetTargetBitrate(kbps);
                        Log($"Set target bitrate: {kbps} kbps");
                    }
                    catch (Exception ex)
                    {
                        Log($"Warning: Failed to set bitrate: {ex.Message}");
                    }

                    // Forward data channel events from native sender through manager
                    nativeSender.OnDataChannelMessage += (dcId, text) => { try { OnDataChannelMessage?.Invoke(dcId, text); } catch { } };
                    nativeSender.OnDataChannelBinaryMessage += (dcId, data) => { try { OnDataChannelBinaryMessage?.Invoke(dcId, data); } catch { } };
                    nativeSender.OnDataChannelStateChanged += (dcId, isOpen) => { try { OnDataChannelStateChanged?.Invoke(dcId, isOpen); } catch { } };

                    // The native sender now creates the default 'control' channel during initialization.
                    Log("Note: control data channel is created by native sender during initialization (if supported)");
                }

                // 5. Create Signaling Client
                _signalingClient = SignalingClient.CreateProducer(_serverBase, _clientKey);
                _signalingClient.OnLog += (m) => Log(m);

                // Propagate signaling-level viewer events to consumers of this manager
                _signalingClient.OnViewerJoined += async () =>
                {
                    try
                    {
                        Log("Signaling: viewer joined (manager)");
                        try { OnViewerJoined?.Invoke(); } catch { }
                    }
                    catch { }
                    await Task.CompletedTask;
                };

                _signalingClient.OnViewerLeft += async () =>
                {
                    try
                    {
                        Log("Signaling: viewer left (manager)");
                        try { OnViewerLeft?.Invoke(); } catch { }
                    }
                    catch { }
                    await Task.CompletedTask;
                };

                // Wire up local ICE candidates - با بافر کردن
                if (nativeSender != null)
                {
                    nativeSender.OnLocalIceCandidate += async (candidate, sdpMid, sdpMLineIndex) =>
                    {
                        try
                        {
                            if (_signalingClient != null)
                            {
                                Log($"Local ICE candidate generated (mid={sdpMid}, idx={sdpMLineIndex}): {candidate.Substring(0, Math.Min(200, candidate.Length))}");
                                
                                bool shouldSend = false;
                                lock (_localCandidateLock)
                                {
                                    if (_offerSent)
                                    {
                                        // Offer already sent, send immediately
                                        shouldSend = true;
                                    }
                                    else
                                    {
                                        // Buffer the candidate
                                        _localCandidateBuffer.Add((candidate, sdpMid, sdpMLineIndex));
                                        Log($"Buffered local ICE candidate (total buffered: {_localCandidateBuffer.Count})");
                                    }
                                }

                                if (shouldSend)
                                {
                                    await SendLocalCandidateAsync(candidate, sdpMid, sdpMLineIndex);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error handling local ICE candidate: {ex.Message}");
                        }
                    };
                }

                // 6. Setup signaling event handlers
                SetupSignalingHandlers();

                // 7. Join the signaling room
                await _signalingClient.JoinAsync();
                Log("Joined signaling room");

                _reconnectAttempts = 0;
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to start: {ex.Message}");
                lock (_stateLock)
                {
                    _state = ConnectionState.Failed;
                }
                await CleanupAsync();
                return false;
            }
        }

        /// <summary>
        /// Send a single local ICE candidate to signaling server
        /// </summary>
        private async Task SendLocalCandidateAsync(string candidate, string sdpMid, int sdpMLineIndex)
        {
            if (_signalingClient == null) return;

            try
            {
                string targetSession = _signalingClient.GetCurrentTargetSession();
                Log($"Sending local ICE candidate (mid={sdpMid}, idx={sdpMLineIndex}, target={targetSession})");
                
                bool sent = await _signalingClient.SendIceCandidateAsync(candidate, sdpMid, sdpMLineIndex, targetSession);
                if (sent)
                    Log("✅ Sent local ICE candidate to signaling server");
                else
                    Log("❌ Failed to send local ICE candidate to signaling server");
            }
            catch (Exception ex)
            {
                Log($"❌ Error sending local ICE candidate: {ex.Message}");
            }
        }

        /// <summary>
        /// Flush all buffered local ICE candidates after offer is sent
        /// </summary>
        private async Task FlushLocalCandidatesAsync()
        {
            List<(string candidate, string mid, int index)> candidates;
            
            lock (_localCandidateLock)
            {
                _offerSent = true;
                candidates = new List<(string, string, int)>(_localCandidateBuffer);
                _localCandidateBuffer.Clear();
            }

            if (candidates.Count > 0)
            {
                Log($"📤 Flushing {candidates.Count} buffered local ICE candidates...");
                foreach (var c in candidates)
                {
                    await SendLocalCandidateAsync(c.candidate, c.mid, c.index);
                }
                Log($"✅ Flushed all {candidates.Count} local ICE candidates");
            }
            else
            {
                Log("No buffered local ICE candidates to flush");
            }
        }

        /// <summary>
        /// Stop the connection gracefully.
        /// </summary>
        public async Task StopAsync()
        {
            lock (_stateLock)
            {
                if (_state == ConnectionState.Idle || _state == ConnectionState.Disconnecting)
                {
                    return;
                }
                _state = ConnectionState.Disconnecting;
            }

            Log("Stopping connection...");

            // Cancel any ongoing reconnect
            try
            {
                _reconnectCts?.Cancel();
                if (_reconnectTask != null)
                {
                    await _reconnectTask;
                }
            }
            catch { }

            await CleanupAsync();

            lock (_stateLock)
            {
                _state = ConnectionState.Idle;
            }

            Log("Connection stopped");
        }

        /// <summary>
        /// Send an encoded video frame.
        /// </summary>
        public async Task SendFrameAsync(byte[] nalUnit, uint timestamp90kHz, bool isKeyFrame)
        {
            if (_webRtcSender == null || !IsConnected)
            {
                return; // Silent drop
            }

            try
            {
                // Log($"SendFrameAsync: calling sender (len={nalUnit?.Length ?? 0}, ts90={timestamp90kHz}, isKey={isKeyFrame})");
                await _webRtcSender.SendEncodedFrameAsync(nalUnit, timestamp90kHz, isKeyFrame);
                // Log("SendFrameAsync: sender returned");
            }
            catch (Exception ex)
            {
                Log($"Error sending frame: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a data channel on the underlying WebRTC sender (returns data channel id or -1)
        /// </summary>
        public int CreateDataChannel(string label, bool ordered = true, bool reliable = true)
        {
            try
            {
                var native = _webRtcSender as NativeWebRtcSender;
                if (native == null) return -1;
                return native.CreateDataChannel(label, ordered, reliable);
            }
            catch { return -1; }
        }

        public bool SendDataChannelMessage(int dcId, string message)
        {
            try
            {
                var native = _webRtcSender as NativeWebRtcSender;
                if (native == null) return false;
                return native.SendDataChannelMessage(dcId, message);
            }
            catch { return false; }
        }

        public bool SendDataChannelBinary(int dcId, byte[] data)
        {
            try
            {
                var native = _webRtcSender as NativeWebRtcSender;
                if (native == null) return false;
                return native.SendDataChannelBinary(dcId, data);
            }
            catch { return false; }
        }

        // ============================================================================
        // Private Methods - State Management
        // ============================================================================

        private void HandleNativeStateChange(int nativeState)
        {
            Log($"Native connection state: {nativeState} ({GetNativeStateName(nativeState)})");

            switch (nativeState)
            {
                case 2: // Connected
                    lock (_stateLock)
                    {
                        if (_state == ConnectionState.Connecting || _state == ConnectionState.Reconnecting)
                        {
                            _state = ConnectionState.Connected;
                            _reconnectAttempts = 0;
                            Log("✅ Connection established");
                            try { OnConnected?.Invoke(); } catch { }
                        }
                    }
                    break;

                case 3: // Disconnected
                case 4: // Failed
                case 5: // Closed
                    lock (_stateLock)
                    {
                        if (_state == ConnectionState.Connected || _state == ConnectionState.Connecting)
                        {
                            _state = ConnectionState.Failed;
                            Log("❌ Connection lost");
                            try { OnDisconnected?.Invoke(); } catch { }
                            
                            // Trigger reconnection
                            _ = TriggerReconnectAsync();
                        }
                    }
                    break;
            }
        }

        private async Task TriggerReconnectAsync()
        {
            lock (_stateLock)
            {
                if (_state == ConnectionState.Reconnecting || _state == ConnectionState.Disconnecting)
                {
                    return; // Already reconnecting or stopping
                }
                _state = ConnectionState.Reconnecting;
            }

            // Cancel any existing reconnect task
            try
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();
            }
            catch { }

            _reconnectCts = new CancellationTokenSource();
            _reconnectTask = ReconnectLoopAsync(_reconnectCts.Token);
            await Task.CompletedTask;
        }

        private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
        {
            Log($"Starting reconnection attempts (max: {_maxReconnectAttempts})...");

            while (_reconnectAttempts < _maxReconnectAttempts && !cancellationToken.IsCancellationRequested)
            {
                _reconnectAttempts++;
                Log($"Reconnection attempt {_reconnectAttempts}/{_maxReconnectAttempts}");

                try { OnReconnectAttempt?.Invoke(_reconnectAttempts); } catch { }

                // Cleanup old connection
                await CleanupAsync();

                // Reset state to Idle BEFORE attempting to start (critical!)
                lock (_stateLock)
                {
                    _state = ConnectionState.Idle;
                }

                // Wait before retry
                try
                {
                    await Task.Delay(_reconnectDelayMs * _reconnectAttempts, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Attempt to reconnect
                bool success = await StartAsync();
                if (success)
                {
                    Log("✅ Reconnection successful");
                    return;
                }

                Log($"Reconnection attempt {_reconnectAttempts} failed");
            }

            if (_reconnectAttempts >= _maxReconnectAttempts)
            {
                Log($"❌ Max reconnection attempts ({_maxReconnectAttempts}) reached. Giving up.");
                lock (_stateLock)
                {
                    _state = ConnectionState.Failed;
                }
            }
        }

        // ============================================================================
        // Private Methods - Signaling Setup
        // ============================================================================

        private void SetupSignalingHandlers()
        {
            if (_signalingClient == null) return;

            _signalingClient.OnRequestOffer += async () =>
            {
                try
                {
                    Log("🔔 OnRequestOffer fired!");
                    if (_webRtcSender != null)
                    {
                        // Get the current target session from signaling client
                        string targetSession = _signalingClient.GetCurrentTargetSession();
                        Log($"OnRequestOffer: target session = '{targetSession}'");
                        
                        if (string.IsNullOrWhiteSpace(targetSession))
                        {
                            Log("⚠️  WARNING: target session is empty! Offer will be sent without target_session");
                        }
                        
                        Log("OnRequestOffer: calling GetLocalSdpOfferAsync()...");
                        var offer = await _webRtcSender.GetLocalSdpOfferAsync();
                        if (!string.IsNullOrWhiteSpace(offer))
                        {
                            Log($"OnRequestOffer: got SDP offer ({offer.Length} chars)");
                            // Log full SDP for debugging
                            Log("===== FULL SDP OFFER START =====");
                            foreach (var line in offer.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None))
                            {
                                if (!string.IsNullOrEmpty(line))
                                    Log($"  {line}");
                            }
                            Log("===== FULL SDP OFFER END =====");
                            // Diagnostic: check for datachannel/SCTP presence
                            try
                            {
                                bool hasApp = offer.Contains("m=application") || offer.Contains("a=sctpmap") || offer.Contains("a=mid:application");
                                bool hasSctp = offer.Contains("a=sctpmap") || offer.Contains("a=sctp-port") || offer.Contains("m=application");
                                Log($"OnRequestOffer: SDP contains m=application/a=sctpmap/a=mid:application? app={hasApp}, sctp={hasSctp}");
                            }
                            catch { }
                            // Apply SDP fixes (same as original code)
                            offer = offer.Replace("UDP/TLS/RTP/SAVPFF", "UDP/TLS/RTP/SAVPF");
                            offer = System.Text.RegularExpressions.Regex.Replace(offer, @"m=video \d+", "m=video 50000");
                            offer = offer.Replace("a=group:BUNDLE video\r\n", "");
                            offer = offer.Replace("a=group:LS video\r\n", "");

                            // Send offer with target session
                            Log($"OnRequestOffer: sending offer via SignalingClient.SendOfferAsync(targetSession='{targetSession}')...");
                            try
                            {
                                bool offerSent = await _signalingClient.SendOfferAsync(offer, targetSession);
                                if (offerSent)
                                {
                                    Log("✅ Sent offer to signaling server");
                                    
                                    // CRITICAL: Flush buffered local ICE candidates after offer is sent
                                    await FlushLocalCandidatesAsync();
                                }
                                else
                                {
                                    Log("❌ Failed to send offer to signaling server");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"❌ Exception while sending offer: {ex.Message}");
                            }
                        }
                        else
                        {
                            Log("❌ GetLocalSdpOfferAsync returned null or empty");
                        }
                    }
                    else
                    {
                        Log("❌ _webRtcSender is null");
                    }
                }
                catch (Exception ex)
                {
                    Log($"❌ OnRequestOffer error: {ex.Message} | {ex.StackTrace}");
                }
            };

            _signalingClient.OnConnectionEstablished += async () =>
            {
                Log("Signaling: connection established (SDP/ICE exchange done)");
                // Note: This is signaling state, not native WebRTC connection state
                // Native connection state (2=Connected) will trigger OnConnected event separately
                await Task.CompletedTask;
            };

            _signalingClient.OnConnectionLost += async () =>
            {
                Log("Signaling: connection lost");
                await TriggerReconnectAsync();
            };

            _signalingClient.OnViewerLeft += async () =>
            {
                Log("Signaling: viewer left");
                await Task.CompletedTask;
            };

            _signalingClient.OnSignalReceived += async (fromSession, type, payload) =>
            {
                try
                {
                    await HandleSignalAsync(type, payload);
                }
                catch (Exception ex)
                {
                    Log($"Signal handler error: {ex.Message}");
                }
            };
        }

        private async Task HandleSignalAsync(string type, System.Text.Json.JsonElement payload)
        {
            if (_webRtcSender == null) return;

            switch (type)
            {
                case "answer":
                    string? sdp = null;
                    if (payload.ValueKind == System.Text.Json.JsonValueKind.Object && 
                        payload.TryGetProperty("sdp", out var sdpEl))
                    {
                        sdp = sdpEl.GetString();
                    }
                    else if (payload.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        sdp = payload.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(sdp))
                    {
                        await _webRtcSender.SetRemoteSdpAnswerAsync(sdp);
                        _remoteDescriptionSet = true;
                        Log("Applied remote SDP answer");
                        
                        // Flush pending candidates
                        var candidates = _pendingCandidates.ToArray();
                        _pendingCandidates.Clear();
                        if (candidates.Length > 0)
                        {
                            Log($"Flushing {candidates.Length} pending ICE candidates");
                            foreach (var c in candidates)
                            {
                                try
                                {
                                    await _webRtcSender.AddRemoteIceCandidateAsync(c.candidate, c.mid, c.index);
                                }
                                catch (Exception ex)
                                {
                                    Log($"Error adding pending candidate: {ex.Message}");
                                }
                            }
                        }
                        
                        // Start streaming immediately after setting remote description
                        var nativeSender = _webRtcSender as NativeWebRtcSender;
                        if (nativeSender != null)
                        {
                            try
                            {
                                await nativeSender.StartStreamingAsync();
                                Log("Started streaming after SDP answer applied");
                            }
                            catch (Exception ex)
                            {
                                Log($"Error starting streaming: {ex.Message}");
                            }
                        }
                    }
                    break;

                case "candidate":
                case "candidates":
                    await HandleIceCandidatesAsync(payload);
                    break;

                case "hangup":
                    Log("Received hangup signal");
                    try
                    {
                        if (_signalingClient != null)
                        {
                            string targetSession = _signalingClient.GetCurrentTargetSession();
                            await _signalingClient.SendHangupAsync(targetSession);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error sending hangup response: {ex.Message}");
                    }
                    await TriggerReconnectAsync();
                    break;
                    
                case "join":
                    // Viewer joined - just log it
                    Log("Viewer joined the session");
                    break;
            }
        }

        private async Task HandleIceCandidatesAsync(System.Text.Json.JsonElement payload)
        {
            if (_webRtcSender == null) return;

            try
            {
                if (payload.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    string? cand = null;
                    string mid = "0";
                    int idx = 0;

                    if (payload.TryGetProperty("candidate", out var candEl))
                    {
                        if (candEl.ValueKind == System.Text.Json.JsonValueKind.String)
                            cand = candEl.GetString();
                        else if (candEl.ValueKind == System.Text.Json.JsonValueKind.Object && 
                                 candEl.TryGetProperty("candidate", out var nestedCand))
                            cand = nestedCand.GetString();
                    }

                    if (payload.TryGetProperty("sdpMid", out var midEl))
                        mid = midEl.GetString() ?? "0";
                    if (payload.TryGetProperty("sdpMLineIndex", out var idxEl) && 
                        idxEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        idx = idxEl.GetInt32();

                    if (!string.IsNullOrWhiteSpace(cand))
                    {
                        if (!_remoteDescriptionSet)
                        {
                            _pendingCandidates.Add((cand, mid, idx));
                            Log($"Buffered remote ICE candidate (waiting for remote description)");
                        }
                        else
                        {
                            await _webRtcSender.AddRemoteIceCandidateAsync(cand, mid, idx);
                            Log("Added remote ICE candidate");
                        }
                    }
                }
                else if (payload.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var c in payload.EnumerateArray())
                    {
                        string? cand = null;
                        string mid = "0";
                        int idx = 0;

                        if (c.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (c.TryGetProperty("candidate", out var ce))
                            {
                                if (ce.ValueKind == System.Text.Json.JsonValueKind.String)
                                    cand = ce.GetString();
                                else if (ce.ValueKind == System.Text.Json.JsonValueKind.Object && 
                                         ce.TryGetProperty("candidate", out var nestedCe))
                                    cand = nestedCe.GetString();
                            }
                            if (c.TryGetProperty("sdpMid", out var me)) 
                                mid = me.GetString() ?? "0";
                            if (c.TryGetProperty("sdpMLineIndex", out var ie) && 
                                ie.ValueKind == System.Text.Json.JsonValueKind.Number) 
                                idx = ie.GetInt32();
                        }

                        if (!string.IsNullOrWhiteSpace(cand))
                        {
                            if (!_remoteDescriptionSet)
                            {
                                _pendingCandidates.Add((cand, mid, idx));
                                Log($"Buffered remote ICE candidate (array, waiting for remote description)");
                            }
                            else
                            {
                                await _webRtcSender.AddRemoteIceCandidateAsync(cand, mid, idx);
                                Log("Added remote ICE candidate (array)");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error handling ICE candidates: {ex.Message}");
            }
        }

        // ============================================================================
        // Private Methods - Cleanup
        // ============================================================================

        private async Task CleanupAsync()
        {
            Log("Cleaning up connection resources...");

            // Reset flags
            _remoteDescriptionSet = false;
            _offerSent = false;
            _pendingCandidates.Clear();
            
            lock (_localCandidateLock)
            {
                _localCandidateBuffer.Clear();
            }

            // 1. Stop signaling
            if (_signalingClient != null)
            {
                try
                {
                    await _signalingClient.LeaveAsync();
                }
                catch (Exception ex)
                {
                    Log($"Error leaving signaling: {ex.Message}");
                }

                try
                {
                    _signalingClient.Dispose();
                }
                catch { }

                _signalingClient = null;
            }

            // 2. Cleanup WebRTC sender (IMPORTANT!)
            if (_webRtcSender != null)
            {
                try
                {
                    _webRtcSender.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"Error disposing WebRTC sender: {ex.Message}");
                }

                _webRtcSender = null;
            }

            // 3. Wait a bit for resources to be released
            await Task.Delay(100);

            Log("Cleanup completed");
        }

        private void Log(string message)
        {
            try
            {
                OnLog?.Invoke($"[ConnectionManager] {message}");
            }
            catch { }
        }

        private static string GetNativeStateName(int state)
        {
            return state switch
            {
                0 => "New",
                1 => "Connecting",
                2 => "Connected",
                3 => "Disconnected",
                4 => "Failed",
                5 => "Closed",
                _ => "Unknown"
            };
        }

        public void Dispose()
        {
            try
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();
                StopAsync().Wait(5000); // Wait max 5 seconds
            }
            catch { }
        }
    }
}