using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>One synthesis voice AIRouter serves (from <c>GET /v1/audio/voices</c>).</summary>
    public sealed record AIRouterVoice(string Id, string Name);

    /// <summary>
    /// A transcription result. <see cref="NoSpeechProbability"/> and <see cref="AverageLogProbability"/>
    /// are only meaningful when <see cref="Detailed"/> is true (the router answered verbose_json); they
    /// are what tell a real utterance apart from Whisper inventing "Thank you." over a cough.
    /// </summary>
    public sealed record AIRouterTranscript(
        string Text,
        string? Language,
        double DurationSeconds,
        double NoSpeechProbability,
        double AverageLogProbability,
        bool Detailed);

    /// <summary>An audio request AIRouter refused or failed. 429s have already penalised the shared queue.</summary>
    public sealed class AIRouterAudioException : Exception
    {
        public AIRouterAudioException(string message, HttpStatusCode? status = null, string? body = null, Exception? inner = null)
            : base(message, inner)
        {
            Status = status;
            Body = body;
        }

        public HttpStatusCode? Status { get; }
        public string? Body { get; }
        public bool RateLimited => Status == HttpStatusCode.TooManyRequests;
    }

    /// <summary>
    /// AIRouter's speech endpoints: Whisper (<c>/audio/transcriptions</c>) and Kokoro
    /// (<c>/audio/speech</c>). Neither documents its parameters, so this speaks the OpenAI shape and
    /// learns what the router actually accepts: the first 4xx on an optional field drops that field for
    /// the rest of the process instead of failing every later call the same way.
    ///
    /// It deliberately does NOT go through the chat path. That path rewrites any model AIRouter's
    /// catalogue does not list to the configured chat model, which would turn a kokoro request into a
    /// Qwen3.8 one if the catalogue lookup ever failed.
    ///
    /// Slots: AIRouter's 3-parallel limit is per key. Until it is measured whether audio counts toward
    /// it, every request is admitted through the shared limiter as <see cref="AIRouterWorkClass.Audio"/>
    /// (first in line, never parked, never granted a residency). <c>gateAudio</c> turns that off once
    /// it is known to be unnecessary.
    /// </summary>
    public sealed class AIRouterAudioClient
    {
        public const string TranscriptionModel = "whisper-large-v3-turbo";
        public const string SpeechModel = "kokoro";
        public const string DefaultVoice = "af_heart";

        /// <summary>Kokoro takes up to ~1K tokens per request; stay well inside it.</summary>
        internal const int MaxSpeechInputChars = 3000;

        /// <summary>Whisper's prompt is the decoder's previous-text window, which holds 224 tokens.</summary>
        internal const int MaxPromptChars = 800;

        private static readonly TimeSpan VoiceListTtl = TimeSpan.FromHours(6);
        private static readonly TimeSpan DefaultCoolOff = TimeSpan.FromSeconds(10);

        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly Func<CancellationToken, Task<string?>> apiKey;
        private readonly AIRouterFairUseLimiter? limiter;
        private readonly Func<Task<bool>> gateAudio;
        private readonly Action<string>? log;

        private readonly object voicesLock = new();
        private IReadOnlyList<AIRouterVoice>? voices;
        private DateTime voicesFetchedUtc = DateTime.MinValue;

        // Learned capabilities. 0 = untested, 1 = accepted, -1 = rejected.
        private int verboseJson;
        private int promptField;
        private int speedField;
        private readonly Dictionary<string, int> formats = new(StringComparer.OrdinalIgnoreCase);

        public AIRouterAudioClient(
            string baseUrl,
            Func<CancellationToken, Task<string?>> apiKey,
            AIRouterFairUseLimiter? limiter = null,
            Func<Task<bool>>? gateAudio = null,
            Action<string>? log = null,
            HttpClient? httpClient = null)
        {
            this.baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
            this.apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            this.limiter = limiter;
            this.gateAudio = gateAudio ?? (() => Task.FromResult(limiter != null));
            this.log = log;
            http = httpClient ?? CreateHttpClient();
        }

        /// <summary>
        /// A dedicated client: speech calls are short and latency-bound, so they must never queue behind
        /// a five-minute chat stream on a shared connection pool, and the connection must stay warm
        /// between turns of a conversation (TLS + TCP setup to Switzerland is a round trip we only want
        /// to pay once).
        /// </summary>
        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(30),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
        }

        // ── Speech to text ──

        /// <summary>
        /// Transcribes one utterance. <paramref name="language"/> skips Whisper's detection pass (faster,
        /// and it cannot mistake a two-word English command for Welsh); null or empty auto-detects.
        /// <paramref name="prompt"/> biases spelling toward names Whisper would otherwise mangle.
        /// </summary>
        public async Task<AIRouterTranscript> TranscribeAsync(byte[] audio, string fileName, string mimeType,
            string? language = null, string? prompt = null, CancellationToken cancellationToken = default)
        {
            if (audio == null || audio.Length == 0) throw new ArgumentException("No audio.", nameof(audio));

            bool wantDetail = Volatile.Read(ref verboseJson) >= 0;
            bool sendPrompt = !string.IsNullOrWhiteSpace(prompt) && Volatile.Read(ref promptField) >= 0;
            string? clippedPrompt = sendPrompt ? ClipPrompt(prompt!) : null;

            var (status, body) = await SendTranscriptionAsync(audio, fileName, mimeType, language, clippedPrompt,
                wantDetail ? "verbose_json" : "json", cancellationToken);

            if (IsParameterRejection(status) && (wantDetail || clippedPrompt != null))
            {
                // Something optional was refused. Learn which by retrying with neither; if that works,
                // both stay off — the minimal request is the one this router is known to accept.
                var (retryStatus, retryBody) = await SendTranscriptionAsync(audio, fileName, mimeType, language,
                    null, "json", cancellationToken);
                if (retryStatus == HttpStatusCode.OK)
                {
                    if (wantDetail) Interlocked.Exchange(ref verboseJson, -1);
                    if (clippedPrompt != null) Interlocked.Exchange(ref promptField, -1);
                    log?.Invoke($"AIRouter transcription refused an optional field ({(int)status}); continuing with response_format=json and no prompt.");
                    return ParseTranscript(retryBody, detailed: false);
                }
                status = retryStatus;
                body = retryBody;
            }

            if (status != HttpStatusCode.OK)
                throw new AIRouterAudioException($"AIRouter transcription failed with {(int)status}.", status, body);

            if (wantDetail) Interlocked.CompareExchange(ref verboseJson, 1, 0);
            if (clippedPrompt != null) Interlocked.CompareExchange(ref promptField, 1, 0);
            return ParseTranscript(body, wantDetail);
        }

        private async Task<(HttpStatusCode status, string body)> SendTranscriptionAsync(byte[] audio, string fileName,
            string mimeType, string? language, string? prompt, string responseFormat, CancellationToken cancellationToken)
        {
            using var lease = await AcquireAsync("audio:stt", cancellationToken);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "audio/wav" : mimeType);
            form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "speech.wav" : fileName);
            form.Add(new StringContent(TranscriptionModel), "model");
            form.Add(new StringContent(responseFormat), "response_format");
            form.Add(new StringContent("0"), "temperature");
            if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language.Trim()), "language");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/transcriptions") { Content = form };
            await AuthorizeAsync(request, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            HandleRateLimit(response);
            return (response.StatusCode, body);
        }

        internal static AIRouterTranscript ParseTranscript(string body, bool detailed)
        {
            body = body?.Trim() ?? string.Empty;
            if (body.Length == 0) return new AIRouterTranscript("", null, 0, 0, 0, false);
            if (body[0] != '{')
                // Some servers answer response_format=json with plain text anyway.
                return new AIRouterTranscript(body, null, 0, 0, 0, false);

            var json = JObject.Parse(body);
            string text = ((string?)json["text"] ?? string.Empty).Trim();
            string? language = (string?)json["language"];
            double duration = (double?)json["duration"] ?? 0;
            var segments = (json["segments"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            bool hasDetail = detailed && segments.Count > 0;
            double noSpeech = hasDetail ? segments.Average(s => (double?)s["no_speech_prob"] ?? 0) : 0;
            double logProb = hasDetail ? segments.Average(s => (double?)s["avg_logprob"] ?? 0) : 0;
            return new AIRouterTranscript(text, language, duration, noSpeech, logProb, hasDetail);
        }

        private static string ClipPrompt(string prompt)
        {
            string trimmed = prompt.Trim();
            // Whisper conditions on the END of its prompt window, so keep the tail.
            return trimmed.Length <= MaxPromptChars ? trimmed : trimmed[^MaxPromptChars..];
        }

        // ── Text to speech ──

        /// <summary>
        /// Synthesises one segment and returns the encoded audio. <paramref name="format"/> is a
        /// preference: if the router refuses it, mp3 (which every OpenAI-compatible server produces) is
        /// used instead and remembered. Check <c>result.Format</c> for what actually came back.
        /// </summary>
        public async Task<(byte[] Audio, string Format)> SynthesizeAsync(string text, string? voice = null,
            double speed = 1.0, string format = "wav", CancellationToken cancellationToken = default)
        {
            string input = (text ?? string.Empty).Trim();
            if (input.Length == 0) throw new ArgumentException("Nothing to say.", nameof(text));
            if (input.Length > MaxSpeechInputChars) input = input[..MaxSpeechInputChars];
            string chosenVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();
            string chosenFormat = FormatState(format) < 0 ? "mp3" : format;
            bool sendSpeed = Math.Abs(speed - 1.0) > 0.001 && Volatile.Read(ref speedField) >= 0;

            var (status, bytes, error) = await SendSpeechAsync(input, chosenVoice, chosenFormat,
                sendSpeed ? speed : null, cancellationToken);

            if (IsParameterRejection(status) && (sendSpeed || !string.Equals(chosenFormat, "mp3", StringComparison.OrdinalIgnoreCase)))
            {
                var (retryStatus, retryBytes, retryError) = await SendSpeechAsync(input, chosenVoice, "mp3", null, cancellationToken);
                if (retryStatus == HttpStatusCode.OK)
                {
                    if (sendSpeed) Interlocked.Exchange(ref speedField, -1);
                    if (!string.Equals(chosenFormat, "mp3", StringComparison.OrdinalIgnoreCase)) SetFormatState(chosenFormat, -1);
                    log?.Invoke($"AIRouter speech refused '{chosenFormat}'{(sendSpeed ? " or speed" : "")} ({(int)status}); using mp3 at normal speed.");
                    return (retryBytes, "mp3");
                }
                status = retryStatus;
                bytes = retryBytes;
                error = retryError;
            }

            if (status != HttpStatusCode.OK || bytes.Length == 0)
                throw new AIRouterAudioException($"AIRouter speech synthesis failed with {(int)status}.", status, error);

            SetFormatState(chosenFormat, 1);
            if (sendSpeed) Interlocked.CompareExchange(ref speedField, 1, 0);
            return (bytes, chosenFormat);
        }

        private async Task<(HttpStatusCode status, byte[] audio, string? error)> SendSpeechAsync(string input, string voice,
            string format, double? speed, CancellationToken cancellationToken)
        {
            var payload = new JObject
            {
                ["model"] = SpeechModel,
                ["input"] = input,
                ["voice"] = voice,
                ["response_format"] = format,
            };
            if (speed.HasValue) payload["speed"] = Math.Round(Math.Clamp(speed.Value, 0.5, 2.0), 2);

            using var lease = await AcquireAsync("audio:tts", cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/speech")
            {
                Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            await AuthorizeAsync(request, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            HandleRateLimit(response);
            if (!response.IsSuccessStatusCode)
                return (response.StatusCode, Array.Empty<byte>(), await response.Content.ReadAsStringAsync(cancellationToken));
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancellationToken), null);
        }

        private int FormatState(string format)
        {
            lock (formats) return formats.TryGetValue(format, out int state) ? state : 0;
        }

        private void SetFormatState(string format, int state)
        {
            lock (formats)
            {
                if (state > 0 && formats.TryGetValue(format, out int existing) && existing < 0) return;
                formats[format] = state;
            }
        }

        // ── Voices ──

        public async Task<IReadOnlyList<AIRouterVoice>> ListVoicesAsync(CancellationToken cancellationToken = default)
        {
            lock (voicesLock)
            {
                if (voices != null && DateTime.UtcNow - voicesFetchedUtc < VoiceListTtl) return voices;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/audio/voices");
            await AuthorizeAsync(request, cancellationToken);
            using var response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new AIRouterAudioException($"AIRouter voice list failed with {(int)response.StatusCode}.", response.StatusCode, body);

            var parsed = ParseVoices(body);
            lock (voicesLock)
            {
                voices = parsed;
                voicesFetchedUtc = DateTime.UtcNow;
            }
            return parsed;
        }

        internal static IReadOnlyList<AIRouterVoice> ParseVoices(string body)
        {
            var token = JToken.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var list = token is JObject obj ? obj["voices"] as JArray : token as JArray;
            var result = new List<AIRouterVoice>();
            foreach (var entry in list ?? new JArray())
            {
                if (entry.Type == JTokenType.String)
                {
                    string id = (string?)entry ?? "";
                    if (id.Length > 0) result.Add(new AIRouterVoice(id, id));
                    continue;
                }
                if (entry is not JObject voice) continue;
                string? voiceId = (string?)voice["id"] ?? (string?)voice["voice_id"] ?? (string?)voice["name"];
                if (string.IsNullOrWhiteSpace(voiceId)) continue;
                result.Add(new AIRouterVoice(voiceId, (string?)voice["name"] ?? voiceId));
            }
            return result;
        }

        /// <summary>
        /// Opens (and pools) the connection before the first real request needs it, and fills the voice
        /// cache on the way. Best-effort: a failed warm-up never surfaces.
        /// </summary>
        public async Task WarmAsync(CancellationToken cancellationToken = default)
        {
            try { await ListVoicesAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { log?.Invoke($"AIRouter audio warm-up failed: {ex.Message}"); }
        }

        // ── Shared plumbing ──

        private async Task<AIRouterFairUseLease?> AcquireAsync(string kind, CancellationToken cancellationToken)
        {
            if (limiter == null || !await gateAudio()) return null;
            // Every audio call shares one prefix key per kind. It never reaches the residency table
            // (Audio is never granted one), so the key is only an identity for diagnostics.
            var ticket = new AIRouterWorkTicket(kind, AIRouterWorkClass.Audio, AIRouterSlotIntent.Turn);
            return await limiter.AcquireAsync(1, ticket, cancellationToken);
        }

        private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? key = await apiKey(cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
                throw new AIRouterAudioException("AIRouterLLMToken is not set, so speech is unavailable.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        private void HandleRateLimit(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.TooManyRequests || limiter == null) return;
            TimeSpan coolOff = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : DefaultCoolOff);
            if (coolOff <= TimeSpan.Zero) coolOff = DefaultCoolOff;
            // The envelope is per key: a 429 here means chat requests would get one too, so the whole
            // queue backs off, exactly as the chat path does.
            limiter.Penalize(coolOff);
            log?.Invoke($"AIRouter rate-limited an audio request; queue held for {coolOff.TotalSeconds:0}s.");
        }

        private static bool IsParameterRejection(HttpStatusCode status) =>
            status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.UnsupportedMediaType;
    }
}
