using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// KliveAgent Voice Service: orchestrates VAD (silence detection) and STT (speech-to-text).
    /// Provides complete voice command processing pipeline from raw audio to transcribed text.
    /// </summary>
    public sealed class KliveAgentVoiceService : IDisposable
    {
        private readonly SileroVadEngine _vadEngine;
        private readonly FastSttEngine _sttEngine;

        private const int MaxAudioDurationMs = 120000; // 120 seconds
        private const int MaxAudioSizeBytes = 15 * 1024 * 1024; // 15 MB
        private const int SampleRate = 16000;
        private const int MinSpeechDurationMs = 200; // Minimum 200ms of speech

        public bool IsInitialized => _vadEngine.IsInitialized && _sttEngine.IsInitialized;

        public KliveAgentVoiceService()
        {
            _vadEngine = new SileroVadEngine();
            _sttEngine = new FastSttEngine();
        }

        /// <summary>
        /// Initialize voice engines. Must be called before processing audio.
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                Debug.WriteLine("[KliveAgentVoice] Initializing voice service...");

                var vadInit = await _vadEngine.InitializeAsync();
                if (!vadInit)
                {
                    Debug.WriteLine("[KliveAgentVoice] VAD initialization failed");
                    return false;
                }

                var sttInit = await _sttEngine.InitializeAsync();
                if (!sttInit)
                {
                    Debug.WriteLine("[KliveAgentVoice] STT initialization failed");
                    return false;
                }

                Debug.WriteLine("[KliveAgentVoice] Voice service initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KliveAgentVoice] Initialization error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Process voice command: validate audio, detect speech, transcribe, and return results.
        /// Input: raw audio bytes (16-bit mono PCM at 16kHz)
        /// Output: (transcript, diagnostics)
        /// </summary>
        public async Task<(string transcript, VoiceDiagnostics diagnostics)> ProcessVoiceCommandAsync(
            byte[] audioBytes,
            TimeSpan sttTimeout = default)
        {
            var diagnostics = new VoiceDiagnostics { StartTimeUtc = DateTime.UtcNow };
            var diagnosticsTimer = Stopwatch.StartNew();

            try
            {
                // Validate input
                var validationResult = ValidateAudioInput(audioBytes);
                if (!validationResult.isValid)
                {
                    diagnostics.ValidationError = validationResult.error;
                    diagnostics.TotalDurationMs = (int)diagnosticsTimer.ElapsedMilliseconds;
                    Debug.WriteLine($"[KliveAgentVoice] Audio validation failed: {validationResult.error}");
                    return ("", diagnostics);
                }

                // Normalize audio to mono 16-bit float
                var audioSamples = BytesToSamples(audioBytes);
                var normalizedSamples = NormalizeAudio(audioSamples);

                diagnostics.InputAudioDurationMs = (int)Math.Round(audioSamples.Length * 1000.0 / SampleRate);
                diagnostics.AudioNormalizationMs = (int)diagnosticsTimer.ElapsedMilliseconds;

                // VAD processing: detect speech regions
                var vadTimer = Stopwatch.StartNew();
                _vadEngine.Reset();

                var speechSegments = _vadEngine.DetectSpeechSegments(normalizedSamples);
                diagnostics.VadDurationMs = (int)vadTimer.ElapsedMilliseconds;
                diagnostics.SpeechSegmentCount = speechSegments.Count;

                if (speechSegments.Count == 0)
                {
                    diagnostics.ValidationError = "No speech detected in audio";
                    diagnostics.TotalDurationMs = (int)diagnosticsTimer.ElapsedMilliseconds;
                    Debug.WriteLine("[KliveAgentVoice] No speech detected by VAD");
                    return ("", diagnostics);
                }

                // Extract speech regions with padding
                var extractedAudio = ExtractSpeechSegments(normalizedSamples, speechSegments);
                diagnostics.SpeechDurationMs = (int)Math.Round(extractedAudio.Length * 1000.0 / SampleRate);
                diagnostics.TrimmedSilenceDurationMs = diagnostics.InputAudioDurationMs - diagnostics.SpeechDurationMs;

                if (diagnostics.SpeechDurationMs < MinSpeechDurationMs)
                {
                    diagnostics.ValidationError = $"Speech duration {diagnostics.SpeechDurationMs}ms below minimum {MinSpeechDurationMs}ms";
                    diagnostics.TotalDurationMs = (int)diagnosticsTimer.ElapsedMilliseconds;
                    Debug.WriteLine($"[KliveAgentVoice] Speech too short: {diagnostics.SpeechDurationMs}ms");
                    return ("", diagnostics);
                }

                // STT processing: transcribe speech
                var sttTimer = Stopwatch.StartNew();
                var actualSttTimeout = sttTimeout != default ? sttTimeout : TimeSpan.FromSeconds(30);

                var extractedAudioBytes = SamplesToBytes(extractedAudio);
                var (transcript, confidence) = await _sttEngine.TranscribeAsync(
                    extractedAudioBytes,
                    actualSttTimeout);

                diagnostics.SttDurationMs = (int)sttTimer.ElapsedMilliseconds;
                diagnostics.TranscriptConfidence = confidence;

                diagnostics.TotalDurationMs = (int)diagnosticsTimer.ElapsedMilliseconds;

                Debug.WriteLine(
                    $"[KliveAgentVoice] Processed voice command: " +
                    $"transcript_len={transcript.Length}, " +
                    $"confidence={confidence:F2}, " +
                    $"vad_ms={diagnostics.VadDurationMs}, " +
                    $"stt_ms={diagnostics.SttDurationMs}, " +
                    $"total_ms={diagnostics.TotalDurationMs}");

                return (transcript.Trim(), diagnostics);
            }
            catch (Exception ex)
            {
                diagnostics.ValidationError = $"Processing error: {ex.Message}";
                diagnostics.TotalDurationMs = (int)diagnosticsTimer.ElapsedMilliseconds;
                Debug.WriteLine($"[KliveAgentVoice] Processing failed: {ex.Message}");
                return ("", diagnostics);
            }
        }

        /// <summary>
        /// Validate audio input constraints.
        /// </summary>
        private (bool isValid, string error) ValidateAudioInput(byte[] audioBytes)
        {
            if (audioBytes == null || audioBytes.Length == 0)
                return (false, "Audio buffer is empty");

            if (audioBytes.Length % 2 != 0)
                return (false, "Audio buffer length is not even (16-bit PCM)");

            if (audioBytes.Length > MaxAudioSizeBytes)
                return (false, $"Audio exceeds maximum size: {audioBytes.Length} > {MaxAudioSizeBytes}");

            var durationMs = (audioBytes.Length / 2) * 1000 / SampleRate;
            if (durationMs > MaxAudioDurationMs)
                return (false, $"Audio duration exceeds maximum: {durationMs}ms > {MaxAudioDurationMs}ms");

            return (true, "");
        }

        /// <summary>
        /// Convert 16-bit PCM bytes to float samples (-1.0 to 1.0).
        /// </summary>
        private static float[] BytesToSamples(byte[] audioBytes)
        {
            var samples = new float[audioBytes.Length / 2];

            for (int i = 0; i < samples.Length; i++)
            {
                var sample = BitConverter.ToInt16(audioBytes, i * 2);
                samples[i] = sample / 32768.0f;
            }

            return samples;
        }

        /// <summary>
        /// Convert float samples back to 16-bit PCM bytes.
        /// </summary>
        private static byte[] SamplesToBytes(float[] samples)
        {
            var bytes = new byte[samples.Length * 2];

            for (int i = 0; i < samples.Length; i++)
            {
                var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
                var sample = (short)(clamped * 32767.0f);
                BitConverter.GetBytes(sample).CopyTo(bytes, i * 2);
            }

            return bytes;
        }

        /// <summary>
        /// Normalize audio to prevent clipping and ensure consistent levels.
        /// </summary>
        private static float[] NormalizeAudio(float[] samples)
        {
            if (samples.Length == 0)
                return samples;

            // Find peak amplitude
            var peak = 0.0f;
            foreach (var sample in samples)
            {
                var abs = Math.Abs(sample);
                if (abs > peak) peak = abs;
            }

            if (peak < 0.001f)
                return samples; // Silent audio, return as-is

            // Normalize to 0.9 of max to prevent clipping
            var targetPeak = 0.9f;
            var scale = targetPeak / peak;

            var normalized = new float[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                normalized[i] = samples[i] * scale;
            }

            return normalized;
        }

        /// <summary>
        /// Extract speech segments from audio with padding.
        /// </summary>
        private static float[] ExtractSpeechSegments(
            float[] audioBuffer,
            List<(int startSample, int endSample)> segments)
        {
            if (segments.Count == 0)
                return audioBuffer;

            const int PadSamples = 8000; // 500ms padding at 16kHz

            var mergedSegments = new List<(int, int)>();
            int currentStart = segments[0].startSample;
            int currentEnd = segments[0].endSample;

            // Merge overlapping/close segments
            for (int i = 1; i < segments.Count; i++)
            {
                var (segStart, segEnd) = segments[i];
                if (segStart - currentEnd < PadSamples * 2) // Segments close together
                {
                    currentEnd = segEnd;
                }
                else
                {
                    mergedSegments.Add((currentStart, currentEnd));
                    currentStart = segStart;
                    currentEnd = segEnd;
                }
            }
            mergedSegments.Add((currentStart, currentEnd));

            // Calculate output size
            int outputSize = 0;
            foreach (var (start, end) in mergedSegments)
            {
                var padStart = Math.Max(0, start - PadSamples);
                var padEnd = Math.Min(audioBuffer.Length, end + PadSamples);
                outputSize += padEnd - padStart;
            }

            // Extract with padding
            var output = new float[outputSize];
            int outIdx = 0;

            foreach (var (start, end) in mergedSegments)
            {
                var padStart = Math.Max(0, start - PadSamples);
                var padEnd = Math.Min(audioBuffer.Length, end + PadSamples);
                var length = padEnd - padStart;

                Array.Copy(audioBuffer, padStart, output, outIdx, length);
                outIdx += length;
            }

            return output;
        }

        public void Dispose()
        {
            _vadEngine?.Dispose();
            _sttEngine?.Dispose();
        }
    }

    /// <summary>
    /// Diagnostic telemetry for voice command processing.
    /// </summary>
    public class VoiceDiagnostics
    {
        public DateTime StartTimeUtc { get; set; }
        public int InputAudioDurationMs { get; set; }
        public int AudioNormalizationMs { get; set; }
        public int VadDurationMs { get; set; }
        public int SpeechDurationMs { get; set; }
        public int TrimmedSilenceDurationMs { get; set; }
        public int SpeechSegmentCount { get; set; }
        public int SttDurationMs { get; set; }
        public float TranscriptConfidence { get; set; }
        public int TotalDurationMs { get; set; }
        public string? ValidationError { get; set; }

        public override string ToString()
        {
            return $"VoiceDiagnostics: " +
                   $"inputMs={InputAudioDurationMs}, " +
                   $"vadMs={VadDurationMs}, " +
                   $"speechMs={SpeechDurationMs}, " +
                   $"trimmedMs={TrimmedSilenceDurationMs}, " +
                   $"segments={SpeechSegmentCount}, " +
                   $"sttMs={SttDurationMs}, " +
                   $"confidence={TranscriptConfidence:F2}, " +
                   $"totalMs={TotalDurationMs}, " +
                   $"error={ValidationError ?? "none"}";
        }
    }
}
