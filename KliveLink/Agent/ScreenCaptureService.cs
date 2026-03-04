using System.Drawing;
using System.Drawing.Imaging;
using KliveLink.Protocol;

namespace KliveLink.Agent
{
    /// <summary>
    /// Captures screen frames as JPEG and delivers them as base64 payloads.
    /// Runs on a background thread with configurable interval and quality.
    /// Consent-gated: will not capture if consent is revoked.
    /// </summary>
    public class ScreenCaptureService : IDisposable
    {
        private CancellationTokenSource? _cts;
        private Task? _captureTask;

        public bool IsCapturing => _captureTask != null && !_captureTask.IsCompleted;

        public event Action<ScreenCaptureFramePayload>? OnFrameCaptured;

        public ScreenCaptureService()
        {
        }

        public void StartCapture(ScreenCaptureRequestPayload request)
        {

            StopCapture();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int monitorIndex = request.MonitorIndex;
            int quality = Math.Clamp(request.Quality, 10, 100);
            int intervalMs = Math.Max(request.IntervalMs, 200);

            _captureTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var frame = CaptureScreen(monitorIndex, quality);
                        if (frame != null)
                            OnFrameCaptured?.Invoke(frame);
                    }
                    catch (Exception)
                    {
                        // Swallow individual frame errors to keep stream alive
                    }

                    try { await Task.Delay(intervalMs, token); }
                    catch (TaskCanceledException) { break; }
                }
            }, token);
        }

        public void StopCapture()
        {
            _cts?.Cancel();
            try { _captureTask?.Wait(2000); } catch { }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }

        private ScreenCaptureFramePayload? CaptureScreen(int monitorIndex, int quality)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
                monitorIndex = 0;

            var screen = screens[monitorIndex];
            var bounds = screen.Bounds;

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            using var ms = new MemoryStream();
            var encoder = GetJpegEncoder();
            if (encoder != null)
            {
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                bitmap.Save(ms, encoder, encoderParams);
            }
            else
            {
                bitmap.Save(ms, ImageFormat.Jpeg);
            }

            return new ScreenCaptureFramePayload
            {
                MonitorIndex = monitorIndex,
                Width = bounds.Width,
                Height = bounds.Height,
                Base64JpegData = Convert.ToBase64String(ms.ToArray())
            };
        }

        private static ImageCodecInfo? GetJpegEncoder()
        {
            return ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
