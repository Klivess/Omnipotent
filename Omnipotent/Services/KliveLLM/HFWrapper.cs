using System.Collections.Generic;
using LLama.Common;
using SteamKit2.GC.Dota.Internal;
using Newtonsoft.Json;

namespace Omnipotent.Services.KliveLLM
{
    public class HFWrapper
    {
        public struct HFMessage
        {
            public string role;
            public string content;
        }

        public struct HFLLMInferenceRequest
        {
            public HFMessage[] messages;
            public string model;
            public bool stream;

            [JsonProperty("max_tokens", NullValueHandling = NullValueHandling.Ignore)]
            public int? max_tokens;

            public void BuildMessagesFromChatHistory(ChatHistory history)
            {
                List<HFMessage> hFMessages = new();
                foreach(var msg in history.Messages)
                {
                    if(msg.AuthorRole == AuthorRole.User)
                    {
                        hFMessages.Add(new HFMessage { role = "user", content = msg.Content });
                    }
                    else if(msg.AuthorRole == AuthorRole.Assistant)
                    {
                        hFMessages.Add(new HFMessage { role = "assistant", content = msg.Content });
                    }
                    else if(msg.AuthorRole == AuthorRole.System)
                    {
                        hFMessages.Add(new HFMessage { role = "system", content = msg.Content });
                    }
                }
                messages = hFMessages.ToArray();
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
