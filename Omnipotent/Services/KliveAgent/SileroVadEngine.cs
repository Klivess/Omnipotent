using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Silero VAD (Voice Activity Detection) engine using ONNX Runtime.
    /// Detects speech regions in audio using the pre-trained Silero VAD model.
    /// </summary>
    public sealed class SileroVadEngine : IDisposable
    {
        private InferenceSession? _session;
        private const string ModelFileName = "silero_vad.onnx";
        private const string ModelUrl = "https://huggingface.co/silero-vad/resolve/main/silero_vad.onnx";
        private const int SampleRate = 16000;
        private const int FrameSizeSamples = 512; // 32ms frames at 16kHz
        private const float SpeechThreshold = 0.5f;
        private const float SilenceThreshold = 0.4f;

        private float[]? _lastH;
        private float[]? _lastC;

        public bool IsInitialized => _session != null;

        /// <summary>
        /// Initialize the Silero VAD engine. Downloads model if needed.
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                var modelPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.SileroVadModelsDirectory), ModelFileName);

                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(modelPath) ?? OmniPaths.GetPath(OmniPaths.GlobalPaths.SileroVadModelsDirectory));

                // Download model if missing
                if (!File.Exists(modelPath))
                {
                    Debug.WriteLine($"[SileroVAD] Downloading model from {ModelUrl}");
                    await DownloadModelAsync(ModelUrl, modelPath);
                }

                // Initialize ONNX Runtime session
                var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                
                _session = new InferenceSession(modelPath, sessionOptions);
                
                // Initialize state tensors (H and C for LSTM)
                _lastH = new float[2 * 64]; // 2 layers * 64 hidden size
                _lastC = new float[2 * 64];

                Debug.WriteLine("[SileroVAD] Engine initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SileroVAD] Initialization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Process audio frame and return speech probability (0.0 to 1.0).
        /// Audio should be 16-bit mono PCM at 16kHz.
        /// </summary>
        public float? ScoreFrame(float[] audioFrame)
        {
            if (_session == null)
                return null;

            if (audioFrame.Length != FrameSizeSamples)
                return null;

            try
            {
                // Create ONNX inputs
                var audioTensor = new DenseTensor<float>(audioFrame, new[] { 1, audioFrame.Length });
                var srTensor = new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 });
                var hTensor = new DenseTensor<float>(_lastH!, new[] { 2, 1, 64 });
                var cTensor = new DenseTensor<float>(_lastC!, new[] { 2, 1, 64 });

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", audioTensor),
                    NamedOnnxValue.CreateFromTensor("sr", srTensor),
                    NamedOnnxValue.CreateFromTensor("h", hTensor),
                    NamedOnnxValue.CreateFromTensor("c", cTensor)
                };

                // Run inference
                using (var results = _session.Run(inputs))
                {
                    var output = results.FirstOrDefault(r => r.Name == "output");
                    if (output?.Value is Tensor<float> outputTensor && outputTensor.Length > 0)
                    {
                        var confidence = outputTensor.GetValue(0);

                        // Update state for next frame
                        var hOutput = results.FirstOrDefault(r => r.Name == "hn");
                        var cOutput = results.FirstOrDefault(r => r.Name == "cn");

                        if (hOutput?.Value is Tensor<float> hnTensor && _lastH != null)
                        {
                            var hArray = hnTensor.ToArray();
                            Array.Copy(hArray, _lastH, Math.Min(hArray.Length, _lastH.Length));
                        }

                        if (cOutput?.Value is Tensor<float> cnTensor && _lastC != null)
                        {
                            var cArray = cnTensor.ToArray();
                            Array.Copy(cArray, _lastC, Math.Min(cArray.Length, _lastC.Length));
                        }

                        return confidence;
                    }
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Process entire audio buffer and return list of speech segments.
        /// Returns list of (startSample, endSample) tuples.
        /// </summary>
        public List<(int startSample, int endSample)> DetectSpeechSegments(float[] audioBuffer)
        {
            if (_session == null)
                return new List<(int, int)>();

            var segments = new List<(int, int)>();
            var frameCount = audioBuffer.Length / FrameSizeSamples;
            var confidences = new List<float>();

            // Score all frames
            for (int i = 0; i < frameCount; i++)
            {
                var frameStart = i * FrameSizeSamples;
                var frameEnd = Math.Min(frameStart + FrameSizeSamples, audioBuffer.Length);
                var frameLength = frameEnd - frameStart;

                var frame = new float[FrameSizeSamples];
                Array.Copy(audioBuffer, frameStart, frame, 0, frameLength);

                var confidence = ScoreFrame(frame) ?? 0f;
                confidences.Add(confidence);
            }

            // Detect speech regions with hysteresis
            bool inSpeech = false;
            int segmentStart = 0;

            for (int i = 0; i < confidences.Count; i++)
            {
                var confidence = confidences[i];

                if (!inSpeech && confidence > SpeechThreshold)
                {
                    inSpeech = true;
                    segmentStart = i * FrameSizeSamples;
                }
                else if (inSpeech && confidence < SilenceThreshold)
                {
                    inSpeech = false;
                    var segmentEnd = i * FrameSizeSamples;
                    segments.Add((segmentStart, segmentEnd));

                    Debug.WriteLine($"[SileroVAD] Detected speech segment: {segmentStart / (float)SampleRate:F2}s - {segmentEnd / (float)SampleRate:F2}s");
                }
            }

            // Close any open segment at end
            if (inSpeech)
            {
                var segmentEnd = audioBuffer.Length;
                segments.Add((segmentStart, segmentEnd));
                Debug.WriteLine($"[SileroVAD] Detected speech segment: {segmentStart / (float)SampleRate:F2}s - {segmentEnd / (float)SampleRate:F2}s");
            }

            return segments;
        }

        /// <summary>
        /// Reset VAD state for next audio processing.
        /// </summary>
        public void Reset()
        {
            if (_lastH != null) Array.Clear(_lastH, 0, _lastH.Length);
            if (_lastC != null) Array.Clear(_lastC, 0, _lastC.Length);
        }

        private static async Task DownloadModelAsync(string url, string filePath)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                    using (var response = await client.GetAsync(url))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            await contentStream.CopyToAsync(fileStream);
                        }
                    }
                }

                Debug.WriteLine($"[SileroVAD] Model downloaded successfully to {filePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SileroVAD] Failed to download model: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
