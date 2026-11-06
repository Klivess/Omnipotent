using OpenCvSharp;
using KliveLink.Contracts;

namespace KliveLink.Agent.V2;

public sealed record CameraFrame(byte[] Jpeg, int Width, int Height, int CameraIndex);

/// <summary>
/// Windows camera capture built on OpenCV/DirectShow. Frames are JPEG encoded and
/// streamed over the existing v2 stream frame with streamId 2.
/// </summary>
public sealed class CameraCaptureService : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _cameraIndex;

    public bool IsCapturing => _task != null && !_task.IsCompleted;
    public event Action<CameraFrame>? OnFrameCaptured;

    public IReadOnlyList<V2CameraInfo> ListCameras()
    {
        var cameras = new List<V2CameraInfo>();
        for (int index = 0; index < 6; index++)
        {
            try
            {
                using var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
                if (!capture.IsOpened()) continue;
                cameras.Add(new V2CameraInfo { Index = index, Name = $"Camera {index}" });
            }
            catch { }
        }
        return cameras;
    }

    public (bool Started, string? Error) Start(V2CameraStartRequest request)
    {
        lock (_gate)
        {
            StopLocked();
            _cameraIndex = Math.Max(0, request.CameraIndex);
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            int quality = Math.Clamp(request.Quality, 20, 95);
            int interval = Math.Clamp(request.IntervalMs, 50, 5000);
            int width = Math.Clamp(request.Width, 160, 3840);
            int height = Math.Clamp(request.Height, 120, 2160);
            string? error = null;
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _task = Task.Run(() =>
            {
                VideoCapture? capture = null;
                try
                {
                    capture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.DSHOW);
                    if (!capture.IsOpened())
                    {
                        error = $"Camera {_cameraIndex} could not be opened.";
                        ready.TrySetResult(false);
                        return;
                    }
                    capture.Set(VideoCaptureProperties.FrameWidth, width);
                    capture.Set(VideoCaptureProperties.FrameHeight, height);
                    ready.TrySetResult(true);
                    using var mat = new Mat();
                    while (!token.IsCancellationRequested)
                    {
                        if (!capture.Read(mat) || mat.Empty())
                        {
                            Thread.Sleep(50);
                            continue;
                        }
                        Cv2.ImEncode(".jpg", mat, out byte[] jpeg,
                            new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
                        OnFrameCaptured?.Invoke(new CameraFrame(jpeg, mat.Width, mat.Height, _cameraIndex));
                        Thread.Sleep(interval);
                    }
                }
                catch (Exception ex)
                {
                    error ??= ex.Message;
                    ready.TrySetResult(false);
                }
                finally
                {
                    capture?.Dispose();
                }
            }, token);

            if (!ready.Task.Wait(TimeSpan.FromSeconds(8)))
                return (false, "Camera start timed out.");
            return (ready.Task.Result, ready.Task.Result ? null : error ?? "Camera could not be started.");
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { }
        try { _task?.Wait(2000); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _task = null;
    }

    public void Dispose() => Stop();
}
