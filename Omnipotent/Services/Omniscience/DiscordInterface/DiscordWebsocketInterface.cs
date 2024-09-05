using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;

using static Omnipotent.Services.Omniscience.DiscordInterface.DiscordInterface;

namespace Omnipotent.Services.Omniscience.DiscordInterface
{
    public class DiscordWebsocketInterface
    {
        DiscordCrawl parentService;
        OmniDiscordUser parentUser;
        private string gatewayURL;
        private KliveLocalLLM.KliveLocalLLM.KliveLLMSession chatbotSession;
        private Websocket.Client.WebsocketClient WS;
        private CancellationTokenSource CTS;
        ManualResetEvent exitEvent = new ManualResetEvent(false);



        private string lastHeartbeatAck = null;
        private float heartbeatInterval = 0;
        public DiscordWebsocketInterface(DiscordCrawl parentServ, OmniDiscordUser user)
        {
            parentUser = user;
            parentService = parentServ;
        }

        public int ReceiveBufferSize { get; set; } = 8192;

        public async Task BeginInitialisation()
        {
            //Acquire websocket URL
            var client = DiscordHttpClient(parentUser);
            string endpoint = $"{DiscordInterface.discordURI}gateway";
            var response = await client.GetAsync(endpoint);
            var responseString = await response.Content.ReadAsStringAsync();
            dynamic responseJson = JsonConvert.DeserializeObject(responseString);
            gatewayURL = responseJson.url;

            //Create chatbot session
            chatbotSession = parentService.serviceManager.GetKliveLocalLLMService().CreateSession();

            //Connect to websocket
            await ConnectAsync(gatewayURL);
        }

        private async Task ConnectAsync(string url)
        {
            WS = new WebsocketClient(new Uri(url));
            WS.ReconnectTimeout = TimeSpan.FromSeconds(30);
            WS.ReconnectionHappened.Subscribe(info =>
            {
                parentService.ServiceLog($"Reconnection for user {parentUser.GlobalName} happened, type: {info.Type}");
            });
            WS.MessageReceived.Subscribe(async msg =>
            {
                dynamic json = JsonConvert.DeserializeObject(msg.Text);
                if (json.op == 10)
                {
                    heartbeatInterval = json.d.heartbeat_interval;
                }
                else if (json.op == 11)
                {
                    lastHeartbeatAck = json.d;
                }
                else if (json.t == "MESSAGE_CREATE")
                {
                    //If the message is from a DM
                    if (json.d.guild_id == null)
                    {
                        OmniDiscordMessage message = await parentService.discordInterface.ChatInterface.ProcessMessageJSONObjectToOmniDiscordMessage(json.d.ToString(), true);
                        parentService.SaveDiscordMessage(parentUser, message);
                        if ((message.AuthorID.ToString() != parentUser.UserID) && (message.AuthorID.ToString() == "976648966944989204"))
                        {
                            Console.WriteLine("Received chatbot request: Responding.");
                            string response = await parentService.serviceManager.GetKliveLocalLLMService().SendMessageToSession(chatbotSession, message.MessageContent);
                            await parentService.discordInterface.ChatInterface.DirectMessageUser(parentUser, message.AuthorID, response);
                            Console.WriteLine("Responded.");
                        }
                    }
                }

            });
            WS.Start();
            AuthenticateWebsocketConnection();
        }

        private async Task AuthenticateWebsocketConnection()
        {
            //Send Identify payload
            string payload = "{\r\n  \"op\": 2,\r\n  \"d\": {\r\n    \"token\": \"" + parentUser.Token + "\",\r\n    \"properties\": {\r\n      \"os\": \"linux\",\r\n      \"browser\": \"disco\",\r\n      \"device\": \"disco\"\r\n    },\r\n    \"compress\": false,\r\n    \"presence\": {\r\n      \"activities\": [],\r\n      \"status\": \"unknown\",\r\n      \"since\": 0,\r\n      \"afk\": false\r\n    },\r\n    \"capabilities\": 16381,\r\n    \"client_state\": {\r\n      \"api_code_version\": 0,\r\n      \"guild_versions\": {}\r\n    }\r\n  }\r\n}";
            WS.Send(payload);
            //Send heartbeat payload
            StartHeartbeatLoop();
        }

        private async Task StartHeartbeatLoop()
        {
            string payload = "{\r\n  \"op\": 1,\r\n  \"d\": " + lastHeartbeatAck + "\r\n}";
            if (lastHeartbeatAck == null)
            {
                payload = "{\r\n  \"op\": 1,\r\n  \"d\": null\r\n}";
                WS.Send(payload);
            }
            else
            {
                payload = "{\r\n  \"op\": 1,\r\n  \"d\": " + lastHeartbeatAck + "\r\n}";
                WS.Send(payload);
                await Task.Delay(TimeSpan.FromMilliseconds(heartbeatInterval));
            }
            while (lastHeartbeatAck == null) { await Task.Delay(100); }
            StartHeartbeatLoop();
        }

        public async Task JoinDiscordVoiceChannel(string guildID, string channelID)
        {
            //Send voice state update payload
            string payload = "{\r\n  \"op\": 4,\r\n  \"d\": {\r\n    \"guild_id\": \"" + guildID + "\",\r\n    \"channel_id\": \"" + channelID + "\",\r\n    \"self_mute\": false,\r\n    \"self_deaf\": false,\r\n    \"self_video\": false\r\n  }\r\n}";
            WS.Send(payload);
        }

        public async Task LeaveDiscordVoiceChannel()
        {
            //Send voice state update payload
            string payload = "{\r\n  \"op\": 4,\r\n  \"d\": {\r\n    \"guild_id\": null,\r\n    \"channel_id\": \"null\",\r\n    \"self_mute\": false,\r\n    \"self_deaf\": false,\r\n    \"self_video\": false\r\n  }\r\n}";
            WS.Send(payload);
        }
    }
}
