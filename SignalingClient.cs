using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DxgiCapture
{
    /// <summary>
    /// WebRTC Signaling Client for Vigil Simple Mode API (Producer only).
    /// - Only /api/signal/* endpoints are used.
    /// - Room is always 'default'.
    /// - client_key is required in all requests.
    /// </summary>
    public class SignalingClient : IDisposable
    {
        private enum SignalingState { Waiting, Offering, Connected, Disconnected }
        private SignalingState _state = SignalingState.Waiting;
        private readonly object _stateLock2 = new();
        // ============================================================================
        // Configuration & State
        // ============================================================================
        
        private readonly string _serverBase;
        private readonly string _clientKey; // For producers - unique session identifier
        
        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        
        private CancellationTokenSource? _heartbeatCts;
        private Task? _heartbeatTask;
        
        // ICE candidate aggregation
        private readonly List<IceCandidateData> _pendingCandidates = new();
        private readonly object _candidateLock = new();
        private CancellationTokenSource? _candidateAggregationCts;
        private Task? _candidateAggregationTask;
        
        // State tracking
        private bool _isJoined = false;
        private bool _isDisposed = false;
        
        // Target session tracking (for request_offer)
        private string _currentTargetSession = string.Empty;
        private readonly object _targetSessionLock = new();
        
        // Events for signaling
        public delegate Task SignalReceivedHandler(string fromSession, string type, JsonElement payload);
        public event SignalReceivedHandler? OnSignalReceived;

        // Lifecycle / state events
        public delegate Task SimpleEventHandler();
        public event SimpleEventHandler? OnViewerJoined;
        public event SimpleEventHandler? OnViewerLeft;
        public event SimpleEventHandler? OnConnectionEstablished;
        public event SimpleEventHandler? OnConnectionLost;

        // When producer should create an offer (subscribe and create offer -> OnLocalSdpOffer will be fired by native sender)
        public event Func<Task>? OnRequestOffer;
        
        // Logging
        public delegate void LogHandler(string message);
        public event LogHandler? OnLog;
        
        // ============================================================================
        // Constructors
        // ============================================================================
        
        /// <summary>
        /// Initialize SignalingClient for Producer (C# client).
        /// </summary>
        /// <param name="serverBase">Base URL (e.g., https://vigil.aoaci.com)</param>
        /// <param name="clientKey">Unique client key (session identifier)</param>
        public static SignalingClient CreateProducer(string serverBase, string clientKey)
        {
            if (string.IsNullOrWhiteSpace(clientKey) || clientKey.Length < 5)
                throw new ArgumentException("clientKey must be at least 5 characters", nameof(clientKey));
            return new SignalingClient(serverBase, clientKey);
        }

        private SignalingClient(string serverBase, string clientKey)
        {
            _serverBase = serverBase ?? throw new ArgumentNullException(nameof(serverBase));
            _clientKey = clientKey;
            _httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    // Accept all certificates (for self-signed or untrusted certs)
                    // WARNING: This is NOT secure! Only use for development/testing!
                    Log($"[SignalingClient] Certificate validation warning: {errors}");
                    return true;
                }
            });
            Log($"[SignalingClient] Initialized: mode=Producer, server={serverBase}");
        }
        
        // ============================================================================
        // Public API - Common
        // ============================================================================
        
        /// <summary>
        /// Join the room (producer only).
        /// POST /api/signal/join
        /// </summary>
        public async Task JoinAsync(string? metadata = null)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(SignalingClient));
            if (_isJoined) return;
            Log("[SignalingClient] Starting signaling (producer). Entering WAITING state...");

            try
            {
                // Send actual POST request to /api/signal/join
                var joinPayload = new { client_key = _clientKey };
                var joinJson = JsonSerializer.Serialize(joinPayload);
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBase}/api/signal/join"))
                {
                    req.Content = new StringContent(joinJson, Encoding.UTF8, "application/json");
                    Log($"[SignalingClient] Posting to {_serverBase}/api/signal/join");
                    var resp = await _httpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Log($"[SignalingClient] /api/signal/join failed: {resp.StatusCode}");
                        throw new Exception($"Join request failed with status {resp.StatusCode}");
                    }
                    var txt = await resp.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        using var doc = JsonDocument.Parse(txt);
                        if (doc.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok")
                        {
                            Log("[SignalingClient] Successfully joined room (status=ok)");
                        }
                        else
                        {
                            Log($"[SignalingClient] Join response received but status not 'ok'");
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Log($"[SignalingClient] Network error during join: {ex.Message}");
                if (ex.InnerException != null)
                    Log($"[SignalingClient] Inner exception: {ex.InnerException.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Log($"[SignalingClient] Error joining room: {ex.Message}");
                throw;
            }

            _isJoined = true;

            // Start polling loop
            _pollingCts = new CancellationTokenSource();
            _pollingTask = PollForSignalsLoopAsync(_pollingCts.Token);
            // Start heartbeat (first heartbeat after 1s)
            _heartbeatCts = new CancellationTokenSource();
            _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);

            Log("[SignalingClient] Signaling started; polling and heartbeat running.");
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Leave the room (producer only).
        /// POST /api/signal/leave
        /// </summary>
        public async Task LeaveAsync()
        {
            if (!_isJoined) return;
            Log("[SignalingClient] Leaving room as producer...");
            try
            {
                // Stop loops
                if (_pollingCts != null)
                {
                    _pollingCts.Cancel();
                    if (_pollingTask != null)
                    {
                        try { await _pollingTask; }
                        catch (OperationCanceledException) { }
                    }
                }
                if (_heartbeatCts != null)
                {
                    _heartbeatCts.Cancel();
                    if (_heartbeatTask != null)
                    {
                        try { await _heartbeatTask; }
                        catch (OperationCanceledException) { }
                    }
                }
                // Send leave to server
                var leavePayload = new { client_key = _clientKey };
                var leaveJson = JsonSerializer.Serialize(leavePayload);
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBase}/api/signal/leave"))
                {
                    req.Content = new StringContent(leaveJson, Encoding.UTF8, "application/json");
                    var resp = await _httpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Log($"[SignalingClient] /signal/leave failed: {resp.StatusCode}");
                    }
                }
                _isJoined = false;
                Log("[SignalingClient] Successfully left room.");
            }
            catch (Exception ex)
            {
                Log($"[SignalingClient] Error leaving room: {ex.Message}");
            }
        }
        
        // ============================================================================
        // Public API - Signaling Methods
        // ============================================================================
        
        /// <summary>
        /// Send a WebRTC offer.
        /// Producer: POST /api/signal/offer
        /// </summary>
        public async Task<bool> SendOfferAsync(string offerSdp, string? targetSession = null)
        {
            try
            {
                var snippet = offerSdp != null && offerSdp.Length > 200 ? offerSdp.Substring(0, 200).Replace("\r\n", "\\r\\n") : (offerSdp ?? string.Empty);
                Log($"[SignalingClient] Preparing to send offer (len={offerSdp?.Length ?? 0}, targetSession={targetSession}) snippet={snippet}");
            }
            catch { }
            
            var session = targetSession ?? _currentTargetSession;
            var payload = new 
            { 
                sdp = offerSdp, 
                client_key = _clientKey,
                target_session = string.IsNullOrWhiteSpace(session) ? null : session
            };
            var json = JsonSerializer.Serialize(payload);
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBase}/api/signal/offer"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return await SendWithRetryAsync(req, "SendOfferAsync");
            }
        }
        
        
        /// <summary>
        /// Send a single ICE candidate.
        /// Producer: POST /api/signal/candidate
        /// </summary>
        public async Task<bool> SendIceCandidateAsync(string candidate, string sdpMid, int sdpMLineIndex, string? targetSession = null)
        {
            var session = targetSession ?? _currentTargetSession;
            var payload = new
            {
                candidate = candidate,
                sdpMid = sdpMid ?? "0",
                sdpMLineIndex = sdpMLineIndex,
                client_key = _clientKey,
                target_session = string.IsNullOrWhiteSpace(session) ? null : session
            };
            var json = JsonSerializer.Serialize(payload);
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBase}/api/signal/candidate"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return await SendWithRetryAsync(req, "SendIceCandidateAsync");
            }
        }
        
        /// <summary>
        /// Send a hangup signal.
        /// Producer: POST /api/signal/hangup
        /// </summary>
        public async Task<bool> SendHangupAsync(string? targetSession = null)
        {
            var session = targetSession ?? _currentTargetSession;
            var payload = new 
            { 
                client_key = _clientKey,
                target_session = string.IsNullOrWhiteSpace(session) ? null : session
            };
            var json = JsonSerializer.Serialize(payload);
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBase}/api/signal/hangup"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return await SendWithRetryAsync(req, "SendHangupAsync");
            }
        }

        /// <summary>
        /// Send a generic signaling message to server (e.g., custom events).
        /// POST /api/signal/message
        /// </summary>
        public async Task<bool> SendMessageAsync(string type, object payload)
        {
            var body = new
            {
                client_key = _clientKey,
                type = type,
                payload = payload
            };
            var json = JsonSerializer.Serialize(body);
            using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBase}/api/signal/message"))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return await SendWithRetryAsync(req, "SendMessageAsync");
            }
        }

        /// <summary>
        /// Get list of participants in the room for this client_key.
        /// GET /api/signal/participants?client_key=...
        /// Returns raw JsonElement for flexibility.
        /// </summary>
        public async Task<JsonElement?> GetParticipantsAsync()
        {
            try
            {
                var url = $"{_serverBase}/api/signal/participants?client_key={Uri.EscapeDataString(_clientKey)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    // Log($"[SignalingClient] GetParticipants failed: {resp.StatusCode}");
                    return null;
                }
                var txt = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(txt)) return null;
                using var doc = JsonDocument.Parse(txt);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                Log($"[SignalingClient] GetParticipants error: {ex.Message}");
                return null;
            }
        }
        
        // ============================================================================
        // Private Methods - Polling Loop
        // ============================================================================
        
        /// <summary>
        /// Continuously poll the server for incoming signals.
        /// - Producer: GET /api/signal/poll?client_key=...  
        /// </summary>
        private async Task PollForSignalsLoopAsync(CancellationToken cancellationToken)
        {
            const int pollIntervalMs = 1500;
            
            Log("[SignalingClient] Starting polling loop");
            
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        string pollUrl = $"{_serverBase}/api/signal/poll?client_key={Uri.EscapeDataString(_clientKey)}";
                        using (var request = new HttpRequestMessage(HttpMethod.Get, pollUrl))
                        {
                            var response = await _httpClient.SendAsync(request, cancellationToken);
                            if (response.IsSuccessStatusCode)
                            {
                                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    try
                                    {
                                        var doc = JsonDocument.Parse(text);
                                        await ProcessSignalsAsync(doc.RootElement);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"[SignalingClient] Error processing poll response: {ex.Message}");
                                    }
                                }
                            }
                            else
                            {
                                Log($"[SignalingClient] Poll request failed: {response.StatusCode}");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"[SignalingClient] Poll error: {ex.Message}");
                    }
                    try
                    {
                        await Task.Delay(pollIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                Log("[SignalingClient] Polling loop stopped");
            }
        }
        
        /// <summary>
        /// Process incoming signals from poll response.
        /// </summary>
        private async Task ProcessSignalsAsync(JsonElement root)
        {
            try
            {
                // New API wraps messages as { messages: [ ... ] } or returns an array of messages
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var msg in msgs.EnumerateArray())
                    {
                        await ProcessSingleSignalAsync(msg);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var signal in root.EnumerateArray())
                    {
                        await ProcessSingleSignalAsync(signal);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    await ProcessSingleSignalAsync(root);
                }
            }
            catch (Exception ex)
            {
                Log($"[SignalingClient] Error processing signals: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Process a single incoming signal.
        /// </summary>
        private async Task ProcessSingleSignalAsync(JsonElement signal)
        {
            try
            {
                // Message shape may be { id, type, payload, to_session, from_session?, delivered }
                // Debug: log all session fields
                string? fromSession = null;
                string? toSession = null;
                string? sessionId = null;
                
                if (signal.TryGetProperty("from_session", out var fromEl) && fromEl.ValueKind == JsonValueKind.String)
                    fromSession = fromEl.GetString();
                if (signal.TryGetProperty("to_session", out var toEl) && toEl.ValueKind == JsonValueKind.String)
                    toSession = toEl.GetString();
                if (signal.TryGetProperty("session_id", out var sessEl) && sessEl.ValueKind == JsonValueKind.String)
                    sessionId = sessEl.GetString();
                
                // Priority: from_session > session_id > to_session (fallback)
                string from = string.Empty;
                if (!string.IsNullOrWhiteSpace(fromSession))
                    from = fromSession;
                else if (!string.IsNullOrWhiteSpace(sessionId))
                    from = sessionId;
                else if (!string.IsNullOrWhiteSpace(toSession))
                    from = toSession; // fallback

                string type = string.Empty;
                JsonElement payload = default;

                if (signal.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
                {
                    type = typeEl.GetString() ?? string.Empty;
                }

                if (signal.TryGetProperty("payload", out var payloadEl))
                {
                    payload = payloadEl;
                }
                else if (signal.TryGetProperty("data", out var dataEl))
                {
                    // some endpoints may use 'data' instead of 'payload'
                    payload = dataEl;
                }

                // Log($"[SignalingClient] Received message: type={type}, from_session={fromSession}, to_session={toSession}, session_id={sessionId}, resolved from={from}");

                if (OnSignalReceived != null && !string.IsNullOrEmpty(type))
                {
                    // Internal handling for important message types
                    if (type == "request_offer")
                    {
                        // Store the target session for all subsequent offers/candidates
                        lock (_targetSessionLock)
                        {
                            _currentTargetSession = from;
                            // Log($"[SignalingClient] Stored target session: {from}");
                            
                            // Debug warning if we used fallback
                            if (string.IsNullOrWhiteSpace(fromSession))
                            {
                                Log($"[SignalingClient] ⚠️  WARNING: from_session was not in message! Used fallback. from_session={fromSession}, to_session={toSession}, session_id={sessionId}");
                            }
                        }
                        // Log("[SignalingClient] Received request_offer - triggering OnRequestOffer event");
                        // تغییر state به Offering
                        lock (_stateLock2)
                        {
                            if (_state == SignalingState.Waiting)
                            {
                                _state = SignalingState.Offering;
                                // Log("[SignalingClient] State transition: WAITING -> OFFERING");
                            }
                        }
                        // فراخوانی event برای ایجاد Offer
                        try
                        {
                            if (OnRequestOffer != null)
                            {
                                // Log("[SignalingClient] Poll/request_offer: Invoking OnRequestOffer handler");
                                await OnRequestOffer.Invoke();
                                // Log("[SignalingClient] Poll/request_offer: OnRequestOffer handler completed");
                            }
                            else
                            {
                                // Log("[SignalingClient] WARNING: OnRequestOffer is null (poll/request_offer)!");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[SignalingClient] Error invoking OnRequestOffer (poll): {ex.Message}");
                        }
                    }
                    else if (type == "answer")
                    {
                        lock (_stateLock2)
                        {
                            if (_state == SignalingState.Offering)
                            {
                                _state = SignalingState.Connected;
                                // Log("[SignalingClient] State transition: OFFERING -> CONNECTED");
                                _ = InvokeSafeAsync(OnConnectionEstablished);
                            }
                        }
                    }

                    await OnSignalReceived.Invoke(from, type, payload);
                }
            }
            catch (Exception ex)
            {
                Log($"[SignalingClient] Error processing single signal: {ex.Message}");
            }
        }
        
        // ============================================================================
        // Private Methods - Heartbeat (Producer only)
        // ============================================================================
        
        /// <summary>
        /// Send heartbeat every 30 seconds (producer only).
        /// POST /api/signal/heartbeat
        /// Purpose: Update last_seen_at and keep status='online'
        /// </summary>
        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            const int initialDelayMs = 1000; // first heartbeat after 1 second
            const int heartbeatIntervalMs = 30_000; // 30 seconds

            Log("[SignalingClient] Starting heartbeat loop (producer)");

            try
            {
                // initial small delay
                try { await Task.Delay(initialDelayMs, cancellationToken); } catch (OperationCanceledException) { return; }

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var heartbeatPayload = new
                        {
                            client_key = _clientKey,
                            metadata = new { }
                        };
                        var json = JsonSerializer.Serialize(heartbeatPayload);

                        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{_serverBase}/api/signal/heartbeat"))
                        {
                            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                            var resp = await _httpClient.SendAsync(req, cancellationToken);

                            if (resp.IsSuccessStatusCode)
                            {
                                var txt = await resp.Content.ReadAsStringAsync(cancellationToken);
                                Log("[SignalingClient] Heartbeat response received");
                                try
                                {
                                    if (!string.IsNullOrWhiteSpace(txt))
                                    {
                                        using var doc = JsonDocument.Parse(txt);
                                        ProcessHeartbeatResponse(doc.RootElement);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"[SignalingClient] Heartbeat parse error: {ex.Message}");
                                }
                            }
                            else
                            {
                                // Log($"[SignalingClient] Heartbeat failed: {resp.StatusCode}");
                                // on error, ensure we go to WAITING
                                TransitionToState(SignalingState.Waiting);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"[SignalingClient] Heartbeat error: {ex.Message}");
                        TransitionToState(SignalingState.Waiting);
                    }

                    try { await Task.Delay(heartbeatIntervalMs, cancellationToken); } catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                Log("[SignalingClient] Heartbeat loop stopped");
            }
        }

        private void ProcessHeartbeatResponse(JsonElement root)
        {
            try
            {
                // Expected shape: { status: 'ok', online_viewer: bool, participant_id: '...' }
                bool onlineViewer = false;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("online_viewer", out var ov) && ov.ValueKind == JsonValueKind.True)
                {
                    onlineViewer = true;
                }

                if (onlineViewer)
                {
                    // Viewer appeared
                    // Log("[SignalingClient] Heartbeat: viewer online=true");
                    // If we were waiting, move to OFFERING and request an offer
                    lock (_stateLock2)
                    {
                        if (_state == SignalingState.Waiting)
                        {
                            // Only transition to OFFERING and request an offer if we already know
                            // the target session (i.e. we've received a poll/request_offer),
                            // otherwise wait for an explicit request_offer message from poll.
                            lock (_targetSessionLock)
                            {
                                if (!string.IsNullOrWhiteSpace(_currentTargetSession))
                                {
                                    _state = SignalingState.Offering;
                                    // Log("[SignalingClient] State transition: WAITING -> OFFERING");
                                    _ = InvokeSafeAsync(OnViewerJoined);
                                    // Request the application to create an offer
                                    if (OnRequestOffer != null)
                                    {
                                        // Log("[SignalingClient] Heartbeat: Invoking OnRequestOffer (online_viewer=true, target_session present)");
                                        _ = InvokeSafeAsync(OnRequestOffer);
                                    }
                                    else
                                    {
                                        Log("[SignalingClient] WARNING: OnRequestOffer is null (heartbeat handler)");
                                    }
                                }
                                else
                                {
                                    // We know a viewer exists according to heartbeat, but we haven't
                                    // received the explicit request_offer/poll with session id yet.
                                    // Wait for poll/request_offer which contains the target session.
                                    // Log("[SignalingClient] Heartbeat: viewer online but no target_session yet; waiting for poll/request_offer to trigger offer creation");
                                    // Still notify viewer joined so upper layers can react if needed
                                    _ = InvokeSafeAsync(OnViewerJoined);
                                }
                            }
                        }
                        else
                        {
                            // Log($"[SignalingClient] Heartbeat: viewer online but state is {_state}, not invoking OnRequestOffer");
                        }
                    }
                }
                else
                {
                    // Viewer not present
                    // Log("[SignalingClient] Heartbeat: viewer online=false");
                    lock (_stateLock2)
                    {
                        if (_state == SignalingState.Offering || _state == SignalingState.Connected)
                        {
                            _state = SignalingState.Disconnected;
                            // Log("[SignalingClient] State transition: -> DISCONNECTED");
                            // Notify viewers/connection lost
                            _ = InvokeSafeAsync(OnViewerLeft);
                            _ = InvokeSafeAsync(OnConnectionLost);
                            // Cleanup and return to waiting
                            CleanupAfterDisconnect();
                            _state = SignalingState.Waiting;
                            // Log("[SignalingClient] State transition: DISCONNECTED -> WAITING");
                        }
                        else
                        {
                            // remain waiting
                            _state = SignalingState.Waiting;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log($"[SignalingClient] Error processing heartbeat response: {ex.Message}");
                TransitionToState(SignalingState.Waiting);
            }
        }

        private Task InvokeSafeAsync(Func<Task>? fn)
        {
            try
            {
                if (fn == null) return Task.CompletedTask;
                return fn();
            }
            catch (Exception ex)
            {
                // Log($"[SignalingClient] Event handler error: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private Task InvokeSafeAsync(SimpleEventHandler? fn)
        {
            try
            {
                if (fn == null) return Task.CompletedTask;
                return fn();
            }
            catch (Exception ex)
            {
                // Log($"[SignalingClient] Event handler error: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private void TransitionToState(SignalingState target)
        {
            lock (_stateLock2)
            {
                if (_state == target) return;
                // Log($"[SignalingClient] State transition: {_state} -> {target}");
                _state = target;
            }
        }

        private void CleanupAfterDisconnect()
        {
            try
            {
                lock (_candidateLock)
                {
                    _pendingCandidates.Clear();
                }
                // Cancel any candidate aggregation tasks
                if (_candidateAggregationCts != null)
                {
                    try { _candidateAggregationCts.Cancel(); } catch { }
                    try { _candidateAggregationCts.Dispose(); } catch { }
                    _candidateAggregationCts = null;
                }
                // Log("[SignalingClient] Cleanup after disconnect completed");
            }
            catch (Exception ex)
            {
                // Log($"[SignalingClient] Cleanup error: {ex.Message}");
            }
        }
        
        // ICE candidate aggregation for viewers removed (not needed for producer-only client)
        
        // ============================================================================
        // Private Helpers
        // ============================================================================
        
        // SendPollingSignalAsync removed (not needed for producer-only client)
        
        /// <summary>
        /// Send HTTP request with retry logic (for producer signals).
        /// </summary>
        private async Task<bool> SendWithRetryAsync(HttpRequestMessage request, string operationName)
        {
            const int maxRetries = 3;
            int[] backoffDelaysMs = { 1000, 2000, 4000 };
            
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    if (_httpClient == null)
                    {
                        // Log($"[SignalingClient] {operationName}: HttpClient disposed, cannot send");
                        return false;
                    }

                    // Log($"[SignalingClient] {operationName}: attempt {attempt + 1}/{maxRetries}");
                    
                    var response = await _httpClient.SendAsync(request);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        // Log($"[SignalingClient] {operationName} sent successfully (Status: {(int)response.StatusCode})");
                        return true;
                    }
                    else
                    {
                        string respText = string.Empty;
                        try { respText = await response.Content.ReadAsStringAsync(); } catch { }
                        if ((int)response.StatusCode >= 500)
                        {
                            // Log($"[SignalingClient] Server error ({response.StatusCode}); will retry. Response body: {respText}");
                        }
                        else
                        {
                            // Log($"[SignalingClient] Client error ({response.StatusCode}); not retrying. Response body: {respText}");
                            return false;
                        }
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    // Log($"[SignalingClient] {operationName}: HttpClient disposed ({ex.Message})");
                    return false;
                }
                catch (Exception ex)
                {
                    // Log($"[SignalingClient] {operationName}: Network error: {ex.Message}; will retry");
                }
                
                if (attempt < maxRetries - 1)
                {
                    await Task.Delay(backoffDelaysMs[attempt]);
                }
            }
            
            // Log($"[SignalingClient] {operationName} failed after {maxRetries} attempts");
            return false;
        }
        
        private void Log(string message)
        {
            OnLog?.Invoke(message);
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            
            // Log("[SignalingClient] Disposing");
            
            if (_pollingCts != null)
            {
                _pollingCts.Cancel();
                _pollingCts.Dispose();
            }
            
            if (_heartbeatCts != null)
            {
                _heartbeatCts.Cancel();
                _heartbeatCts.Dispose();
            }
            
            if (_candidateAggregationCts != null)
            {
                _candidateAggregationCts.Cancel();
                _candidateAggregationCts.Dispose();
            }
            
            _httpClient?.Dispose();
            
            _isDisposed = true;
        }
        
        /// <summary>
        /// Get the current target session (for testing/debugging)
        /// </summary>
        public string GetCurrentTargetSession()
        {
            lock (_targetSessionLock)
            {
                return _currentTargetSession;
            }
        }
        
        /// <summary>
        /// Set the current target session manually
        /// </summary>
        public void SetCurrentTargetSession(string session)
        {
            lock (_targetSessionLock)
            {
                _currentTargetSession = session;
                // Log($"[SignalingClient] Manual target session set: {session}");
            }
        }
        
        // ============================================================================
        // Helper Data Structures
        // ============================================================================
        
        private class IceCandidateData
        {
            public string ToSession { get; set; } = string.Empty;
            public string Candidate { get; set; } = string.Empty;
            public string SdpMid { get; set; } = "0";
            public int SdpMLineIndex { get; set; } = 0;
        }
    }
}
