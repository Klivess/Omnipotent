using KliveLink.Protocol;

namespace KliveLink.Agent;

/// <summary>Headless screen capture placeholder for non-Windows agents.</summary>
public sealed class ScreenCaptureService : IDisposable
{
    public bool IsCapturing => false;
    public event Action<ScreenCaptureFramePayload>? OnFrameCaptured;
    public void StartCapture(ScreenCaptureRequestPayload request) { }
    public void StopCapture() { }
    public void Dispose() { }
}
