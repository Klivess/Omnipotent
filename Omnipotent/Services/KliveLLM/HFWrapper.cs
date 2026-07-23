using System.Collections.Generic;
using LLama.Common;
using SteamKit2.GC.Dota.Internal;
using Newtonsoft.Json;

namespace Omnipotent.Services.KliveLLM
{
    public class HFWrapper
    {
        // Was a struct with only role/content. Now a class so it can also carry the OpenAI/OpenRouter
        // tool-calling fields (tool_calls on assistant turns; tool_call_id + name on tool-result turns)
        // and a "tool" role. Reused for both request messages and the response message.
        public class HFMessage
        {
            [JsonProperty("role")]
            public string role;

            // OpenAI permits null content when tool_calls is present, but some providers (Alibaba/Qwen
            // via OpenRouter) 400 on "content": null. Callers should coalesce to "" — never emit null.
            //
            // Either a plain string OR a content-parts array (List<object> of HFTextPart / HFImagePart /
            // HFInputAudioPart / HFFilePart) for multimodal models. Use ContentToText() to read it as text —
            // never cast directly. Non-text parts (image/audio/file) attach only to USER turns, never a
            // system message, so the prompt-cache split (KliveLLM.ApplyPromptCaching) is unaffected.
            [JsonProperty("content")]
            public object content;

            // Present on an assistant turn that requested tool invocations.
            [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
            public List<HFToolCall> tool_calls;

            // Present on a tool-result turn (role == "tool"): which call this answers.
            [JsonProperty("tool_call_id", NullValueHandling = NullValueHandling.Ignore)]
            public string tool_call_id;

            // Optional function name echoed back on a tool-result turn.
            [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
            public string name;
        }

        /// <summary>Text element of a content-parts array (OpenAI/OpenRouter vision format).</summary>
        public class HFTextPart
        {
            [JsonProperty("type")]
            public string type = "text";

            [JsonProperty("text")]
            public string text = "";

            // Anthropic/OpenRouter prompt-caching breakpoint, e.g. { "type": "ephemeral" }. When set on
            // a text part, the provider caches everything up to and including it so the prefix prefill is
            // reused on later requests. Omitted from the payload when null (no effect on the vision path).
            [JsonProperty("cache_control", NullValueHandling = NullValueHandling.Ignore)]
            public object cache_control;
        }

        /// <summary>Image element of a content-parts array. Url is typically a base64 data URI.</summary>
        public class HFImagePart
        {
            [JsonProperty("type")]
            public string type = "image_url";

            [JsonProperty("image_url")]
            public HFImageUrl image_url = new();
        }

        public class HFImageUrl
        {
            [JsonProperty("url")]
            public string url = "";
        }

        /// <summary>Audio element of a content-parts array (OpenAI/OpenRouter "input_audio" format). Carries
        /// base64-encoded audio plus its container format. Only send this to a model whose capabilities
        /// report AudioInput — see KliveLLM.GetModelCapabilitiesAsync — otherwise the provider 400s.</summary>
        public class HFInputAudioPart
        {
            [JsonProperty("type")]
            public string type = "input_audio";

            [JsonProperty("input_audio")]
            public HFInputAudio input_audio = new();
        }

        public class HFInputAudio
        {
            // Base64-encoded audio bytes — the RAW base64 payload, not a data: URI.
            [JsonProperty("data")]
            public string data = "";

            // Container/codec hint the provider needs to decode: "wav" | "mp3" | "ogg" | "flac" | "m4a" | "pcm16" …
            [JsonProperty("format")]
            public string format = "wav";
        }

        /// <summary>Document element of a content-parts array (OpenAI/OpenRouter "file" format). SCAFFOLD:
        /// declared now so the gateway content-model is complete; wired into a read_document / chat-upload
        /// path in a later phase. file_data is a base64 data URI, e.g. "data:application/pdf;base64,…".</summary>
        public class HFFilePart
        {
            [JsonProperty("type")]
            public string type = "file";

            [JsonProperty("file")]
            public HFFileData file = new();
        }

        public class HFFileData
        {
            [JsonProperty("filename", NullValueHandling = NullValueHandling.Ignore)]
            public string filename;

            [JsonProperty("file_data")]
            public string file_data = "";
        }

        /// <summary>
        /// Reads HFMessage.content as text regardless of shape: plain string, content-parts list,
        /// or a JArray deserialized from a provider response. Image parts contribute nothing; audio/file
        /// parts contribute a short placeholder (never their base64 payload) so digests/logs stay small.
        /// </summary>
        public static string ContentToText(object content)
        {
            switch (content)
            {
                case null:
                    return string.Empty;
                case string s:
                    return s;
                case HFTextPart tp:
                    return tp.text ?? string.Empty;
                case Newtonsoft.Json.Linq.JValue jv:
                    return jv.ToString();
                case System.Collections.IEnumerable parts and not string:
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var part in parts)
                    {
                        switch (part)
                        {
                            case HFTextPart t:
                                sb.Append(t.text);
                                break;
                            case Newtonsoft.Json.Linq.JObject jo when (string?)jo["type"] == "text":
                                sb.Append((string?)jo["text"]);
                                break;
                            // Non-text parts render a compact placeholder — never their base64 data — so the
                            // in-task compaction digest, token estimate and any logging stay tiny.
                            case HFInputAudioPart:
                                sb.Append("[audio]");
                                break;
                            case HFFilePart fp:
                                sb.Append(string.IsNullOrEmpty(fp.file?.filename) ? "[file]" : $"[file: {fp.file.filename}]");
                                break;
                            case Newtonsoft.Json.Linq.JObject jai when (string?)jai["type"] == "input_audio":
                                sb.Append("[audio]");
                                break;
                            case Newtonsoft.Json.Linq.JObject jf when (string?)jf["type"] == "file":
                                sb.Append("[file]");
                                break;
                            case string str:
                                sb.Append(str);
                                break;
                        }
                    }
                    return sb.ToString();
                }
                default:
                    return content.ToString() ?? string.Empty;
            }
        }

        public class HFToolCall
        {
            [JsonProperty("id")]
            public string id { get; set; }

            [JsonProperty("type")]
            public string type { get; set; } = "function";

            [JsonProperty("function")]
            public HFFunctionCall function { get; set; }
        }

        public class HFFunctionCall
        {
            [JsonProperty("name")]
            public string name { get; set; }

            // OpenAI delivers arguments as a JSON string (not an object).
            [JsonProperty("arguments")]
            public string arguments { get; set; }
        }

        public class HFTool
        {
            [JsonProperty("type")]
            public string type { get; set; } = "function";

            [JsonProperty("function")]
            public HFFunctionDefinition function { get; set; }
        }

        public class HFFunctionDefinition
        {
            [JsonProperty("name")]
            public string name { get; set; }

            [JsonProperty("description")]
            public string description { get; set; }

            // A JSON-schema object describing the function's parameters.
            [JsonProperty("parameters")]
            public object parameters { get; set; }
        }

        public struct HFLLMInferenceRequest
        {
            public HFMessage[] messages;
            public string model;

            // OpenRouter model routing / fallback: ordered backups after `model`. OpenRouter attempts them
            // in turn if the primary fails, so provider-side fallback replaces app-side route loops.
            // Only sent for OpenRouter and only when a distinct backup route is configured; ignored otherwise.
            [JsonProperty("models", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> models;

            public bool stream;

            [JsonProperty("max_tokens", NullValueHandling = NullValueHandling.Ignore)]
            public int? max_tokens;

            [JsonProperty("service_tier", NullValueHandling = NullValueHandling.Ignore)]
            public string service_tier;

            // Native structured tool calling. NullValueHandling.Ignore means a request with no tools
            // serializes byte-identically to the pre-tool-calling payload.
            [JsonProperty("tools", NullValueHandling = NullValueHandling.Ignore)]
            public List<HFTool> tools;

            [JsonProperty("tool_choice", NullValueHandling = NullValueHandling.Ignore)]
            public object tool_choice;

            // When streaming, ask the provider to include a final usage chunk (OpenAI/OpenRouter/HF).
            [JsonProperty("stream_options", NullValueHandling = NullValueHandling.Ignore)]
            public object stream_options;

            // OpenRouter usage accounting: { "include": true } asks the response's usage object to
            // carry the actual `cost` charged for this request (in credits == USD). This is the
            // authoritative spend figure the Projects budget ledger meters against — far more accurate
            // than a flat per-model estimate. Ignored by providers that don't support it.
            [JsonProperty("usage", NullValueHandling = NullValueHandling.Ignore)]
            public object usage;

            // OpenRouter reasoning control: e.g. { "enabled": true/false }. Lets us turn the model's
            // thinking on/off for reasoning-capable models. Ignored by providers that don't support it.
            [JsonProperty("reasoning", NullValueHandling = NullValueHandling.Ignore)]
            public object reasoning;

            public void BuildMessagesFromChatHistory(ChatHistory history)
            {
                List<HFMessage> hFMessages = new();
                foreach(var msg in history.Messages)
                {
                    // Some providers (e.g. Alibaba/Qwen via OpenRouter) reject any message whose
                    // "content" serializes to null with a 400 "content field is a required field".
                    // Models occasionally return assistant turns with null content, so coalesce to
                    // an empty string here to guarantee we never emit "content": null.
                    string content = msg.Content ?? string.Empty;
                    if(msg.AuthorRole == AuthorRole.User)
                    {
                        hFMessages.Add(new HFMessage { role = "user", content = content });
                    }
                    else if(msg.AuthorRole == AuthorRole.Assistant)
                    {
                        hFMessages.Add(new HFMessage { role = "assistant", content = content });
                    }
                    else if(msg.AuthorRole == AuthorRole.System)
                    {
                        hFMessages.Add(new HFMessage { role = "system", content = content });
                    }
                }
                messages = hFMessages.ToArray();
            }

            /// <summary>
            /// Tool-calling path: build the request straight from a structured message list (which can
            /// include assistant-with-tool_calls and role:"tool" result turns that LLama's ChatHistory
            /// cannot represent). Coalesces any null content to "" to satisfy strict providers.
            /// </summary>
            public void BuildMessagesFromList(IEnumerable<HFMessage> structuredMessages)
            {
                var list = new List<HFMessage>();
                foreach (var m in structuredMessages)
                {
                    list.Add(new HFMessage
                    {
                        role = m.role,
                        content = m.content ?? string.Empty,
                        tool_calls = m.tool_calls,
                        tool_call_id = m.tool_call_id,
                        name = m.name,
                    });
                }
                messages = list.ToArray();
            }
        }

        public class HFLLMInferenceResponse
        {
            [JsonProperty("id")]
            public string id { get; set; }

            [JsonProperty("choices")]
            public List<Choice> choices { get; set; }

            [JsonProperty("created")]
            public long created { get; set; }

            [JsonProperty("model")]
            public string model { get; set; }

            [JsonProperty("system_fingerprint")]
            public string system_fingerprint { get; set; }

            [JsonProperty("object")]
            public string @object { get; set; }

            [JsonProperty("usage")]
            public UsageDetails usage { get; set; }

            [JsonProperty("time_info")]
            public TimeInfo time_info { get; set; }

            public class Choice
            {
                [JsonProperty("finish_reason")]
                public string finish_reason { get; set; }

                [JsonProperty("index")]
                public int index { get; set; }

                // HFMessage carries tool_calls, so a finish_reason == "tool_calls" response
                // deserializes its requested calls straight into message.tool_calls.
                [JsonProperty("message")]
                public HFMessage message { get; set; }
            }

            public class UsageDetails
            {
                // OpenRouter usage accounting: the actual amount charged for this request, in credits
                // (1 credit == 1 USD). Null when the provider doesn't report a cost (HuggingFace, local,
                // or OpenRouter with accounting disabled). Present in the final streamed usage chunk too,
                // so the streaming path surfaces it with no extra work.
                [JsonProperty("cost")]
                public double? cost { get; set; }

                [JsonProperty("total_tokens")]
                public int total_tokens { get; set; }

                [JsonProperty("completion_tokens")]
                public int completion_tokens { get; set; }

                [JsonProperty("completion_tokens_details")]
                public CompletionTokensDetails completion_tokens_details { get; set; }

                [JsonProperty("prompt_tokens")]
                public int prompt_tokens { get; set; }

                [JsonProperty("prompt_tokens_details")]
                public PromptTokensDetails prompt_tokens_details { get; set; }
            }

            public class CompletionTokensDetails
            {
                [JsonProperty("accepted_prediction_tokens")]
                public int accepted_prediction_tokens { get; set; }

                [JsonProperty("rejected_prediction_tokens")]
                public int rejected_prediction_tokens { get; set; }

                [JsonProperty("reasoning_tokens")]
                public int reasoning_tokens { get; set; }
            }

            public class PromptTokensDetails
            {
                [JsonProperty("cached_tokens")]
                public int cached_tokens { get; set; }
            }

            public class TimeInfo
            {
                [JsonProperty("queue_time")]
                public double queue_time { get; set; }

                [JsonProperty("prompt_time")]
                public double prompt_time { get; set; }

                [JsonProperty("completion_time")]
                public double completion_time { get; set; }

                [JsonProperty("total_time")]
                public double total_time { get; set; }

                [JsonProperty("created")]
                public double created { get; set; }
            }
        }

        // ── Streaming (stream=true) chunk shapes ──
        // OpenAI-compatible SSE: each `data: {json}` line is one chunk carrying incremental "delta"
        // fields. Content deltas append to the answer; tool_call deltas are merged by index (id/name
        // arrive once, arguments stream across many chunks). A final chunk may carry usage totals.
        public class HFLLMStreamChunk
        {
            [JsonProperty("choices")]
            public List<StreamChoice> choices { get; set; }

            [JsonProperty("usage")]
            public HFLLMInferenceResponse.UsageDetails usage { get; set; }

            public class StreamChoice
            {
                [JsonProperty("index")]
                public int index { get; set; }

                [JsonProperty("finish_reason")]
                public string finish_reason { get; set; }

                [JsonProperty("delta")]
                public Delta delta { get; set; }
            }

            public class Delta
            {
                [JsonProperty("role")]
                public string role { get; set; }

                [JsonProperty("content")]
                public string content { get; set; }

                [JsonProperty("tool_calls")]
                public List<StreamToolCallDelta> tool_calls { get; set; }
            }

            public class StreamToolCallDelta
            {
                [JsonProperty("index")]
                public int index { get; set; }

                [JsonProperty("id")]
                public string id { get; set; }

                [JsonProperty("type")]
                public string type { get; set; }

                [JsonProperty("function")]
                public StreamFunctionDelta function { get; set; }
            }

            public class StreamFunctionDelta
            {
                [JsonProperty("name")]
                public string name { get; set; }

                [JsonProperty("arguments")]
                public string arguments { get; set; }
            }
        }
    }
}
