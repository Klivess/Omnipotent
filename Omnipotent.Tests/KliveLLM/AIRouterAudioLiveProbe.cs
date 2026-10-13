using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent.Talk;
using Omnipotent.Services.KliveLLM;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>Runs only with OMNI_LIVE_AIROUTER=1 and AIROUTER_API_KEY set: these call the real router.</summary>
    public sealed class AIRouterLiveFactAttribute : FactAttribute
    {
        public AIRouterLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OMNI_LIVE_AIROUTER") != "1"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIROUTER_API_KEY")))
                Skip = "Live AIRouter probe; set OMNI_LIVE_AIROUTER=1 and AIROUTER_API_KEY (makes a handful of audio requests).";
        }
    }

    /// <summary>
    /// What AIRouter's undocumented speech endpoints actually accept, and how fast they are. Talk mode is
    /// designed to work either way (the client learns refused fields, and segments are synthesised whole),
    /// but its defaults — WAV segments, verbose transcripts, speed control — are worth confirming against
    /// the live router, and the latency numbers are the budget the whole design is tuned against.
    ///
    /// Not covered here, deliberately: whether audio requests count toward the 3-parallel limit. Testing
    /// that means holding three chat requests open on the shared key, which would collide with the
    /// Projects fleet. Pause the fleet first, or ask support@airouter.ch, then set
    /// AIRouterAudioCountsTowardParallel accordingly.
    /// </summary>
    public class AIRouterAudioLiveProbe(ITestOutputHelper output)
    {
        private const string Base = "https://api.airouter.ch/v1";
        private static string Key => Environment.GetEnvironmentVariable("AIROUTER_API_KEY")!;

        [AIRouterLiveFact]
        public async Task Speech_FormatsSpeedAndLatency()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            string[] inputs =
            {
                "Checking the trader now.",
                "The trader is running normally, the last fill was eleven minutes ago, and there are no open alerts on any venue right now.",
                string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog near the riverbank.", 10)),
            };
            foreach (string format in new[] { "wav", "mp3", "pcm", "opus" })
            {
                foreach (string input in inputs)
                {
                    var (status, ttfb, total, bytes, chunked) = await TimeSpeechAsync(http, input, format, speed: null, stream: false);
                    output.WriteLine($"tts {format,-4} {input.Split(' ').Length,3} words: {(int)status} first byte {ttfb,5} ms, total {total,5} ms, {bytes,8:N0} bytes, chunked={chunked}");
                }
            }
            var (speedStatus, _, _, _, _) = await TimeSpeechAsync(http, inputs[1], "wav", speed: 1.2, stream: false);
            output.WriteLine($"tts speed=1.2 → {(int)speedStatus}");
            var (streamStatus, streamTtfb, streamTotal, _, streamChunked) = await TimeSpeechAsync(http, inputs[2], "pcm", speed: null, stream: true);
            output.WriteLine($"tts stream=true pcm → {(int)streamStatus}, first byte {streamTtfb} ms of {streamTotal} ms, chunked={streamChunked}");
        }

        [AIRouterLiveFact]
        public async Task Transcription_RoundTripThroughKokoro()
        {
            var client = new AIRouterAudioClient(Base, _ => Task.FromResult<string?>(Key), log: output.WriteLine);
            var voices = await client.ListVoicesAsync();
            output.WriteLine($"voices: {voices.Count} (e.g. {string.Join(", ", voices.Take(5).Select(v => v.Id))})");

            foreach (string phrase in new[] { "Restart OmniTrader please.", "Klives wants the MemeScraper logs from yesterday, and then check KliveRAG." })
            {
                var (speech, format) = await client.SynthesizeAsync(phrase, "bm_george", 1.0, "wav");
                output.WriteLine($"synthesised '{phrase}' as {format}, {speech.Length:N0} bytes, {TalkWire.WavDurationMs(speech)} ms");

                var clock = Stopwatch.StartNew();
                var plain = await client.TranscribeAsync(speech, "speech.wav", "audio/wav", "en", null);
                long plainMs = clock.ElapsedMilliseconds;
                clock.Restart();
                var biased = await client.TranscribeAsync(speech, "speech.wav", "audio/wav", "en", TalkText.WhisperPrompt(null, null));
                long biasedMs = clock.ElapsedMilliseconds;
                output.WriteLine($"  stt {plainMs} ms: '{plain.Text}' (detailed={plain.Detailed}, no_speech={plain.NoSpeechProbability:0.00})");
                output.WriteLine($"  stt+vocabulary {biasedMs} ms: '{biased.Text}'");
            }

            // Silence: what Whisper invents from nothing, and whether the phantom filter would catch it.
            var silence = TalkWire.Wav(new byte[TalkWire.CaptureBytesPerSecond]);
            var phantom = await client.TranscribeAsync(silence, "silence.wav", "audio/wav", "en", null);
            output.WriteLine($"silence → '{phantom.Text}', no_speech={phantom.NoSpeechProbability:0.00}, filtered={TalkText.LooksLikePhantom(phantom, 1000, 0)}");
        }

        private static async Task<(HttpStatusCode Status, long FirstByteMs, long TotalMs, long Bytes, bool Chunked)> TimeSpeechAsync(
            HttpClient http, string input, string format, double? speed, bool stream)
        {
            var body = new JObject { ["model"] = "kokoro", ["input"] = input, ["voice"] = "bm_george", ["response_format"] = format };
            if (speed.HasValue) body["speed"] = speed.Value;
            if (stream) body["stream"] = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/audio/speech")
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await using var content = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[16 * 1024];
            long firstByte = -1, total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer)) > 0)
            {
                if (firstByte < 0) firstByte = clock.ElapsedMilliseconds;
                total += read;
            }
            return (response.StatusCode, firstByte, clock.ElapsedMilliseconds, total, response.Headers.TransferEncodingChunked == true);
        }
    }
}
