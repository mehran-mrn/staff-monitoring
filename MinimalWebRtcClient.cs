using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DxgiCapture.Optimized
{
    /// <summary>
    /// کلاینت ساده WebRTC با قابلیت ارسال فریم و دریافت دستورات
    /// </summary>
    public class MinimalWebRtcClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverUrl;
        private readonly string _sessionId;
        
        private IWebRtcSender? _sender;
        private bool _connected = false;
        
        // کنترل‌های ورودی
        public event Action<int, int, int>? OnMouseMove;  // x, y, buttons
        public event Action<int, bool>? OnKeyPress;       // keyCode, isDown

        public MinimalWebRtcClient(string serverUrl, IWebRtcSender sender)
        {
            _serverUrl = serverUrl.TrimEnd('/');
            _sessionId = Guid.NewGuid().ToString("N");
            _sender = sender;
            
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            
            Console.WriteLine($"[WebRTC Client] Session: {_sessionId}");
        }

        /// <summary>
        /// برقراری ارتباط با سرور
        /// </summary>
        public async Task<bool> ConnectAsync(string spropParameterSets)
        {
            try
            {
                // 1. Initialize sender
                _sender?.Initialize(spropParameterSets, initialBitrate: 2000);
                
                // 2. Create SDP offer
                var offer = await _sender!.GetLocalSdpOfferAsync();
                
                // 3. ارسال offer به سرور
                var offerJson = JsonSerializer.Serialize(new
                {
                    sessionId = _sessionId,
                    type = "offer",
                    sdp = offer
                });
                
                var response = await _httpClient.PostAsync(
                    $"{_serverUrl}/api/webrtc/offer",
                    new StringContent(offerJson, Encoding.UTF8, "application/json")
                );
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[WebRTC Client] Server rejected offer: {response.StatusCode}");
                    return false;
                }
                
                // 4. دریافت answer
                var answerJson = await response.Content.ReadAsStringAsync();
                var answerDoc = JsonDocument.Parse(answerJson);
                var answerSdp = answerDoc.RootElement.GetProperty("sdp").GetString();
                
                if (string.IsNullOrEmpty(answerSdp))
                {
                    Console.WriteLine("[WebRTC Client] Invalid answer from server");
                    return false;
                }
                
                // 5. Set remote answer
                await _sender.SetRemoteSdpAnswerAsync(answerSdp);
                
                _connected = true;
                Console.WriteLine("[WebRTC Client] ✓ Connected");
                
                // شروع polling برای دستورات
                _ = Task.Run(PollCommandsAsync);
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC Client] Connection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ارسال فریم encode شده
        /// </summary>
        public async Task SendFrameAsync(byte[] encodedFrame, uint timestamp90kHz, bool isKeyFrame)
        {
            if (!_connected || _sender == null)
                return;
            
            try
            {
                await _sender.SendEncodedFrameAsync(encodedFrame, timestamp90kHz, isKeyFrame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC Client] Send frame error: {ex.Message}");
            }
        }

        /// <summary>
        /// دریافت دستورات از سرور (mouse/keyboard)
        /// </summary>
        private async Task PollCommandsAsync()
        {
            while (_connected)
            {
                try
                {
                    var response = await _httpClient.GetAsync(
                        $"{_serverUrl}/api/control/poll?sessionId={_sessionId}"
                    );
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var commandsJson = await response.Content.ReadAsStringAsync();
                        ProcessCommands(commandsJson);
                    }
                    
                    await Task.Delay(50); // 20Hz polling
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        }

        private void ProcessCommands(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                
                if (!doc.RootElement.TryGetProperty("commands", out var commands))
                    return;
                
                foreach (var cmd in commands.EnumerateArray())
                {
                    var type = cmd.GetProperty("type").GetString();
                    
                    switch (type)
                    {
                        case "mouse":
                            var x = cmd.GetProperty("x").GetInt32();
                            var y = cmd.GetProperty("y").GetInt32();
                            var buttons = cmd.GetProperty("buttons").GetInt32();
                            OnMouseMove?.Invoke(x, y, buttons);
                            break;
                        
                        case "key":
                            var keyCode = cmd.GetProperty("keyCode").GetInt32();
                            var isDown = cmd.GetProperty("isDown").GetBoolean();
                            OnKeyPress?.Invoke(keyCode, isDown);
                            break;
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _connected = false;
            _sender?.Dispose();
            _httpClient.Dispose();
        }
    }
}
