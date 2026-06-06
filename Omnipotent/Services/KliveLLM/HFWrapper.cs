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
            [JsonProperty("content")]
            public string content;

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
    }
}
