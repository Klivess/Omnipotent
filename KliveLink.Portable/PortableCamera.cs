using KliveLink.Contracts;

namespace KliveLink.Agent.V2;

public sealed record CameraFrame(byte[] Jpeg, int Width, int Height, int CameraIndex);

/// <summary>
/// Camera placeholder for the portable agent. Windows uses the OpenCV-backed
/// CameraCaptureService in the full agent; Unix camera backends can be added
/// without changing the wire protocol (streamId 2).
/// </summary>
public sealed class CameraCaptureService : IDisposable
{
    public bool IsCapturing => false;
    public event Action<CameraFrame>? OnFrameCaptured;

    public IReadOnlyList<V2CameraInfo> ListCameras() => Array.Empty<V2CameraInfo>();

    public (bool Started, string? Error) Start(V2CameraStartRequest request)
        => (false, "Camera capture is not available in this portable build.");

    public void Stop() { }
    public void Dispose() { }
}
