using System;
using System.Threading.Tasks;

namespace DxgiCapture
{
    // Minimal interface for the native WebRTC sender interop.
    // Implemented by `NativeWebRtcSender` (P/Invoke wrapper) to provide
    // SDP/ICE/DTLS setup and a way to inject already-encoded H.264 NAL units
    // into the secure WebRTC transport.
    public interface IWebRtcSender : IDisposable
    {
        // Initialize the sender with sprop-parameter-sets (base64 SPS/PPS) if available.
        // Native implementation performs synchronous native peer connection creation.
        void Initialize(string spropParameterSets, string? turnUrl = null, string? turnUsername = null, string? turnPassword = null, string? serverBase = null, string? sessionId = null, int initialBitrate = 1500);

        // Send a single H.264 NAL unit (Annex-B or raw) with an RTP timestamp expressed
        // in 90kHz units and an explicit keyframe flag.
        Task SendEncodedFrameAsync(byte[] nalUnit, uint timestamp90kHz, bool isKeyFrame);

        // SDP signaling methods for offer/answer exchange
        Task<string> GetLocalSdpOfferAsync();
        Task SetRemoteSdpAnswerAsync(string sdpAnswer);

        // Add remote ICE candidate (from signaling)
        Task AddRemoteIceCandidateAsync(string candidate, string sdpMid, int sdpMLineIndex);

        // Control bitrate on the native sender (kbps)
        void SetTargetBitrate(int bitrateKbps);

        // Query current target bitrate (kbps)
        int GetTargetBitrate();

        // Return whether the native stack is requesting an immediate keyframe (PLI/FIR)
        bool ShouldGenerateKeyframe();

        // Event raised when the native stack requests a keyframe (via polling or callback)
        event Action? OnKeyFrameRequested;

        // Disposal is handled via IDisposable.Dispose().
    }
}
