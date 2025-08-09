using DSharpPlus;
using Microsoft.Extensions.AI;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using Markdig.Extensions.TaskLists;
using LangChain.Providers.HuggingFace;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Net;
using System.Reflection;
using System.Security.Permissions;
using LangChain.Providers.HuggingFace.Predefined;
using LangChain.Providers;
using System.Drawing;
using System.Text;
using Newtonsoft.Json;

namespace Omnipotent.Services.KliveLocalLLM
{
    public class KliveLLM : OmniService
    {
        private string huggingFaceToken = "";
        private HttpClient client;
        public KliveLLM()
        {
            name = "KliveLLM";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            huggingFaceToken = await GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMTokenText));
            if (string.IsNullOrEmpty(huggingFaceToken))
            {
                string apparentToken = await (await serviceManager.GetNotificationsService()).SendTextPromptToKlivesDiscord("KliveLLM requires a hugging face token to function.", "Produce one and set it.", TimeSpan.FromDays(7), "Token", "right here please");
                huggingFaceToken = apparentToken.Trim();
            }
            client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + huggingFaceToken);

        }

        public async Task<string> QueryLLM(string content)
        {
            string payload = ProducePayloadString("user", content, "openai/gpt-oss-120b:cerebras");
            HttpContent httpcontent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://router.huggingface.co/v1/chat/completions", httpcontent);
            string llmResp = await response.Content.ReadAsStringAsync();
            dynamic json = JsonConvert.DeserializeObject(llmResp);
            return json.choices[0].message.content;
        }

        public string ProducePayloadString(string role, string content, string model)
        {
            string payload = $"{{\"messages\":[{{\"role\":\"{role}\",\"content\":\"{content}\"}}],\"model\":\"{model}\",\"stream\":false}}";
            return payload;
        }
        public class KliveLLMSession
        {
            private KliveLLM parentService;

            public string sessionId;

            public KliveLLMSession(KliveLLM parentService)
            {
                this.parentService = parentService;
            }
        }

        public class KliveLLMMessage
        {
            public string content;
            public string role;
        }
    }
}
