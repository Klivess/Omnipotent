﻿using DSharpPlus;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using LLama.Batched;
using Markdig.Parsers;
using Markdig.Syntax;
using Microsoft.Extensions.ObjectPool;
using Microsoft.PowerShell.Commands;
using Microsoft.WSMan.Management;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web.Helpers;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Omnipotent.Services.KliveTechHub
{
    public class KliveTechHub : OmniService
    {
        private const string startCommand = "{startComm}";
        private const string endCommand = "{endComm}";

        public KliveTechHub()
        {
            name = "KliveTech Hub";
            threadAnteriority = ThreadAnteriority.High;
        }
        BluetoothClient client = new BluetoothClient();
        KliveTechRoutes KliveTechRoutes;
        private SynchronizedCollection<DataQueue> dataSendQueue = new();
        private List<DataQueue> awaitingResponse = new();
        public List<KliveTechGadget> connectedGadgets = new();
        public class KliveTechGadget
        {
            public string name;
            public string IPAddress;
            public long IPAddressLong;
            public string gadgetID;
            public List<KliveTechActions.KliveTechAction> actions;
            public DateTime timeConnected;
            public bool isOnline;
            public DateTime lastMessageReceived;
            [Newtonsoft.Json.JsonIgnore]
            public BluetoothDeviceInfo deviceInfo;
            [Newtonsoft.Json.JsonIgnore]
            public Thread ReceiveLoop;
            [Newtonsoft.Json.JsonIgnore]
            public BluetoothClient connectedClient;

            public KliveTechGadget()
            {
                actions = new();
                gadgetID = RandomGeneration.GenerateRandomLengthOfNumbers(15);
            }
            public void DisconnectDevice()
            {
                //connectedClient.Close();
                if (ReceiveLoop.IsAlive)
                {
                    ReceiveLoop.Interrupt();
                }
            }
        }
        private enum DataQueueType
        {
            Send,
            Receive
        }
        private struct DataQueue
        {
            //Expected message: {ID{string} DATA{string} RESPEXPECT{true/false}}
            public DataQueueType type;
            public KliveTechGadget gadget;
            public string? dataToSend;
            public string ID;
            [Newtonsoft.Json.JsonIgnore]
            public TaskCompletionSource<KliveTechGadgetResponse> response;
            public bool isResponseExpected;
            public KliveTechActions.OperationNumber operation;

            public KliveTechGadgetMessage CreateMessage()
            {
                var message = new KliveTechGadgetMessage();
                message.DATA = dataToSend;
                message.ID = int.Parse(ID);
                message.OP = operation;
                message.RESPEXPECT = isResponseExpected;
                return message;
            }
        }
        private struct KliveTechGadgetResponse
        {
            //Expected Response: {ID{string} RESP{string} RESPEXPECT{true/false} STATUS{string}}
            public string ID;
            public dynamic response;
            public bool isResponseExpected;
            public HttpStatusCode status;

            public KliveTechGadgetResponse(string response)
            {
                dynamic data = JsonConvert.DeserializeObject(response);
                this.ID = data.ID;
                this.response = data.DATA;
                this.isResponseExpected = data.RESPEXPECT;
                int status = Convert.ToInt32(data.STATUS);
                HttpStatusCode code = (HttpStatusCode)status;
                this.status = code;
            }
        }
        private struct KliveTechGadgetMessage
        {
            //Expected Response: {ID{string} RESP{string} RESPEXPECT{true/false} STATUS{string}}
            public int ID;
            public string DATA;
            public KliveTechActions.OperationNumber OP;
            public bool RESPEXPECT;
        }
        public static bool CheckIfBluetoothProtocolExistsOnDevice()
        {
            try
            {
                return BluetoothRadio.IsSupported;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
        protected async override void ServiceMain()
        {
            if (!CheckIfBluetoothProtocolExistsOnDevice())
            {
                ServiceLog("No Bluetooth on this device, so terminating service.");
                TerminateService();
                return;
            }

            //Routes class broken
            //KliveTechRoutes = new(this);
            //await KliveTechRoutes.RegisterRoutes();

            setuproutestemp();

            await ReconnectToRememberedDevices();
            Task sendDataLoop = new Task(async () => { SendDataLoop(); });
            Task discoverNewGadgets = new Task(async () => { DiscoverNewKliveTechGadgets(); });
            sendDataLoop.Start();
            discoverNewGadgets.Start();
            //SendData(connectedGadgets.Last(), "Hello from KliveTech Hub!");
        }

        private async void setuproutestemp()
        {
            Action<UserRequest> getAllGadgetsHandler = async (req) =>
            {
                try
                {
                    ServiceLog($"Request from {req.user.Name} to get all gadgets");
                    await req.ReturnResponse(JsonConvert.SerializeObject(connectedGadgets));
                }
                catch (Exception ex)
                {
                    ErrorInformation er = new ErrorInformation(ex);
                    await req.ReturnResponse(JsonConvert.SerializeObject(er), code: System.Net.HttpStatusCode.InternalServerError);
                }
            };

            Action<UserRequest> executeGadgetActionHandler = async (req) =>
            {
                //params: gadgetid, actionid, actionparams  
                string id = req.userParameters["gadgetID"];
                string gadgetName = req.userParameters["gadgetName"];
                string actionName = req.userParameters["actionName"];
                string actionParams = req.userParameters["actionParam"];
                ServiceLog($"Request from {req.user.Name} to execute gadget '{gadgetName}' action '{actionName}' with param '{actionParams}'");
                KliveTechHub.KliveTechGadget g;
                if (string.IsNullOrEmpty(gadgetName))
                {
                    g = GetKliveTechGadgetByID(id);
                }
                else
                {
                    g = GetKliveTechGadgetByName(gadgetName);
                }
                ExecuteActionByName(g, actionName, actionParams);
                await req.ReturnResponse("Action executed successfully!");
            };

            Action<UserRequest> getGadgetByIdHandler = async (req) =>
                {
                    try
                    {
                        await req.ReturnResponse(JsonConvert.SerializeObject(connectedGadgets.Find(k => k.gadgetID == req.userParameters["gadgetID"])));
                    }
                    catch (Exception ex)
                    {
                        ErrorInformation er = new ErrorInformation(ex);
                        await req.ReturnResponse(JsonConvert.SerializeObject(er), code: System.Net.HttpStatusCode.InternalServerError);
                    }
                };

            (await serviceManager.GetKliveAPIService()).CreateRoute("/klivetech/GetAllGadgets", getAllGadgetsHandler, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);
            (await serviceManager.GetKliveAPIService()).CreateRoute("/klivetech/executegadgetaction", executeGadgetActionHandler, HttpMethod.Post, Profiles.KMProfileManager.KMPermissions.Guest);
            (await serviceManager.GetKliveAPIService()).CreateRoute("/klivetech/GetGadgetByID", getGadgetByIdHandler, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Guest);
        }

        public async Task RememberKliveTechDevice(KliveTechGadget gadget)
        {
            string path = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechHubGadgetsDirectory), $"{gadget.name}.kliveTechGadget");
            await GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(gadget));
        }
        public async Task ReconnectToRememberedDevices()
        {
            ServiceLog("Reconnecting to remembered KliveTech gadgets.");
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechHubGadgetsDirectory));
            foreach (var file in files)
            {
                if (file.EndsWith(".kliveTechGadget"))
                {
                    KliveTechGadget kliveTechGadget = JsonConvert.DeserializeObject<KliveTechGadget>(await GetDataHandler().ReadDataFromFile(file));
                    await TryConnectToDevice(new BluetoothDeviceInfo(new BluetoothAddress(kliveTechGadget.IPAddressLong)));
                }
            }
            ServiceLog("Finished reconnecting to remembered KliveTech gadgets. " + connectedGadgets.Count + " gadgets in memory.");
        }
        public async Task CheckConnectionStatusOfGadget(KliveTechGadget gadget, int attempts = 0)
        {
            try
            {
                if (connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline == false)
                {
                    ServiceLog($"Device {gadget.name} disconnected!");
                    return;
                }
                if (await IsDeviceConnected(gadget) == false)
                {
                    ServiceLog($"Device {gadget.name} disconnected!");
                    gadget.DisconnectDevice();
                    connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline = false;
                    return;
                }
                else
                {
                    Convert.ToString("hey");
                }
                await Task.Delay(5000);
                CheckConnectionStatusOfGadget(gadget);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
                if (attempts > 10)
                {
                    return;
                }
                CheckConnectionStatusOfGadget(gadget, attempts++);
            }
        }
        private async Task GetGadgetActions(KliveTechGadget gadget, int attempts = 0)
        {
            int maxAttempts = 10;

            KliveTechGadgetResponse res;
            try
            {
                res = await SendData(gadget, KliveTechActions.OperationNumber.GetActions, "GetActions", true).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Get Gadget Actions failed, retrying.");
                await GetGadgetActions(gadget, attempts + 1);
                return;
            }
            if (res.status == HttpStatusCode.OK)
            {
                foreach (var item in res.response.Actions)
                {
                    string test = JsonConvert.SerializeObject(item);
                    KliveTechActions.KliveTechAction action = new();
                    action.name = item.Name;
                    action.paramDescription = item.ParamDescription;
                    action.parameters = (KliveTechActions.ActionParameterType)Convert.ToInt32(item.Type);
                    gadget.actions.Add(action);
                }
            }
            else
            {
                if (attempts >= maxAttempts)
                {
                    await ServiceLogError(new Exception("Failed to get gadget actions"), "Get Gadget Actions failed, max attempts reached.");
                    connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline = false;
                    return;
                }
                await ServiceLogError(new Exception("Failed to get gadget actions"), "Get Gadget Actions failed, retrying.");
                await GetGadgetActions(gadget, attempts + 1);
                return;
            }
            ServiceLog($"{gadget.actions.Count} actions added from {gadget.name}");
        }
        private async Task DiscoverNewKliveTechGadgets()
        {
            try
            {
                BluetoothDeviceInfo[] devicesInRadius = client.DiscoverDevices();
                foreach (BluetoothDeviceInfo device in devicesInRadius)
                {
                    try
                    {
                        //if name starts with klivetech and it's not already connected
                        if (device.DeviceName.ToLower().StartsWith("klivetech") && connectedGadgets.Where(x => x.IPAddress == device.DeviceAddress.ToString()).Count() == 0)
                        {
                            try
                            {
                                TryConnectToDevice(device);
                            }
                            catch (Exception ex)
                            {
                                ServiceLogError(ex);
                                DiscoverNewKliveTechGadgets();
                                (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Connecting to KliveTech gadget failed!: " + new ErrorInformation(ex).FullFormattedMessage);
                                return;
                            }
                        }
                        else if (connectedGadgets.Where(x => x.IPAddress == device.DeviceAddress.ToString()).Count() > 0)
                        {
                            var gadget = connectedGadgets.Where(x => x.IPAddress == device.DeviceAddress.ToString()).First();
                            if (connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline == false)
                            {
                                if (gadget.ReceiveLoop != null)
                                {
                                    gadget.ReceiveLoop.Interrupt();
                                }
                                connectedGadgets.Remove(gadget);
                                TryConnectToDevice(device);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex);
                        (await serviceManager.GetKliveBotDiscordService()).SendMessageToKlives("Connecting to gadget failed!: " + new ErrorInformation(ex).FullFormattedMessage);
                    }
                }
                //Prevent stack overflow
                await Task.Delay(5000);
                DiscoverNewKliveTechGadgets();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Discover klivetech gadgets failed!");
                if (ex.Message.ToLower().Contains("method is not implemented by this class"))
                {
                    client = new BluetoothClient();
                    DiscoverNewKliveTechGadgets();
                }
                else
                {
                    var result = await (await serviceManager.GetNotificationsService()).SendButtonsPromptToKlivesDiscord("Discover klivetech gadgets failed!", $"Error: {new ErrorInformation(ex).FullFormattedMessage}", new Dictionary<string, ButtonStyle>() { { "Retry", ButtonStyle.Primary } }, TimeSpan.FromDays(3));
                    if (result == "Retry")
                    {
                        DiscoverNewKliveTechGadgets();
                    }
                }
            }
        }
        public async Task TryConnectToDevice(BluetoothDeviceInfo device, int attempts = 0)
        {
            KliveTechGadget gadget = new KliveTechGadget();
            try
            {
                gadget.IPAddress = device.DeviceAddress.ToString();
                gadget.deviceInfo = device;
                gadget.connectedClient = new BluetoothClient();
                gadget.IPAddressLong = device.DeviceAddress.ToInt64();
                gadget.connectedClient.Connect(device.DeviceAddress, BluetoothService.SerialPort);
                gadget.isOnline = true;
                gadget.timeConnected = DateTime.Now;


                device.Refresh();
                //Need to wait 100ms for the device name to be received.
                await Task.Delay(1000);
                gadget.name = device.DeviceName;
                ServiceLog("Found KliveTech gadget: " + gadget.name);
                try
                {
                    ((await serviceManager.GetKliveBotDiscordService())).SendMessageToKlives("Found KliveTech gadget: " + gadget.name);
                }
                catch (Exception) { }
                RememberKliveTechDevice(gadget);
                gadget.ReceiveLoop = new Thread(async () => { ReadDataLoop(gadget); });
                gadget.ReceiveLoop.Start();
                Thread GetGadgActions = new(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            await GetGadgetActions(gadget);
                            break;
                        }
                        catch (Exception ex)
                        {
                            ServiceLogError(ex);
                        }
                    }
                });
                connectedGadgets.Add(gadget);
                CheckConnectionStatusOfGadget(gadget);
                GetGadgActions.Start();
            }
            catch (Exception ex)
            {
                ServiceLogError("Couldn't connect to klivetech device: " + device.DeviceName + " Reason: " + new ErrorInformation(ex).FullFormattedMessage);
                if (attempts >= 5)
                {
                    return;
                }
                if (ex.Message.Contains("the connected party did not properly respond"))
                {
                    TryConnectToDevice(device, attempts + 1);
                }
            }
        }
        private async Task<bool> ExecuteGadgetAction(KliveTechGadget gadget, KliveTechActions.KliveTechAction action, string? data)
        {
            if (gadget.isOnline)
            {
                string serial = "";
                if (action.parameters == KliveTechActions.ActionParameterType.Integer)
                {
                    serial = $"{{\"ActionName\":\"{action.name}\",\"Param\":{int.Parse(data)}}}";
                }
                else if (action.parameters == KliveTechActions.ActionParameterType.String)
                {
                    serial = $"{{\"ActionName\":\"{action.name}\",\"Param\":\"{data}\"}}";
                }
                else if (action.parameters == KliveTechActions.ActionParameterType.Bool)
                {
                    string dat = data;
                    serial = $"{{\"ActionName\":\"{action.name}\",\"Param\":{(data == "true").ToString().ToLower()}}}";
                }
                else if (action.parameters == KliveTechActions.ActionParameterType.None)
                {
                    serial = $"{{\"ActionName\":\"{action.name}\",\"Param\":\"\"}}";
                }
                var result = SendData(gadget, KliveTechActions.OperationNumber.ExecuteAction, serial, true);
                return (await result).status == HttpStatusCode.OK;
            }
            else
            {
                return false;
            }
        }
        private async Task ReadDataLoop(KliveTechGadget gadget)
        {
            string result = "";
            try
            {
                if (gadget.isOnline == false)
                {
                    return;
                }
                // Receiving data
                byte[] receiveBuffer = new byte[1024];
                NetworkStream stream = null;
                try
                {
                    stream = gadget.connectedClient.GetStream();
                }
                catch (Exception ex)
                {
                    connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline = false;
                    return;
                }
                int bytesRead = 0;
                string receivedData = "";
                while (receivedData.EndsWith(endCommand) == false)
                {
                    try
                    {
                        bytesRead = await stream.ReadAsync(receiveBuffer, 0, receiveBuffer.Length);
                        receivedData += Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead);
                    }
                    catch (Exception ex)
                    {
                        connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline = false;
                        return;
                    }
                }
                //Get index of start and end command
                int startIndex = receivedData.IndexOf(startCommand);
                result = receivedData;
                int endIndex = receivedData.IndexOf(endCommand);
                result = result.Substring(startIndex);
                result = result.Substring(0, endIndex - startIndex);
                result = result.Replace(startCommand, "").Replace(endCommand, "");
                receivedData = "";
                if (!string.IsNullOrEmpty(result.Trim()) && OmniPaths.IsValidJson(result.Trim()))
                {
                    ServiceLog($"Received data from device {gadget.name}: " + result, false);
                    connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).lastMessageReceived = DateTime.Now;
                    KliveTechGadgetResponse Response = new KliveTechGadgetResponse(result.Trim());
                    if (awaitingResponse.Select(k => k.ID).ToList().Contains(Convert.ToString(Response.ID)) == true)
                    {
                        var wife = awaitingResponse.Find(k => k.ID == Convert.ToString(Response.ID));
                        if (wife.response.Task.IsCompleted == false)
                        {
                            awaitingResponse.Find(k => k.ID == Convert.ToString(Response.ID)).response.SetResult(Response);
                        }
                    }
                    Task.Delay(100).Wait();
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.ToLower().Contains("aborted"))
                {
                    connectedGadgets.Find(k => k.IPAddress == gadget.IPAddress).isOnline = false;
                }
                else
                {
                    ServiceLogError(ex);
                }
            }
            ReadDataLoop(gadget);
        }
        private async Task<KliveTechGadgetResponse> SendData(KliveTechGadget gadget, KliveTechActions.OperationNumber operation, string data, bool expectResponse = false)
        {
            DataQueue dataQueue = new DataQueue();
            dataQueue.gadget = gadget;
            dataQueue.dataToSend = data;
            dataQueue.operation = operation;
            dataQueue.type = DataQueueType.Send;
            dataQueue.ID = RandomGeneration.GenerateRandomLengthOfNumbers(5);
            dataQueue.response = new TaskCompletionSource<KliveTechGadgetResponse>();
            dataQueue.isResponseExpected = expectResponse;
            dataSendQueue.Add(dataQueue);
            if (expectResponse)
            {
                return await dataQueue.response.Task;
            }
            else
            {
                return new();
            }
        }
        private async Task SendDataLoop()
        {
            if (dataSendQueue.Any())
            {
                DataQueue item = dataSendQueue.First();
                dataSendQueue.Remove(item);
                try
                {
                    Task task = new Task(async () =>
                    {
                        if (item.gadget.isOnline)
                        {
                            if (item.type == DataQueueType.Send)
                            {
                                NetworkStream stream = null;
                                Task task = new Task(async () =>
                                {
                                    try
                                    {
                                        stream = item.gadget.connectedClient.GetStream();
                                    }
                                    catch (Exception ex)
                                    {
                                        connectedGadgets.Find(k => k.IPAddress == item.gadget.IPAddress).isOnline = false;
                                        return;
                                    }
                                });
                                task.Start();
                                task.Wait(TimeSpan.FromSeconds(5));
                                try
                                {
                                    if (stream != null & item.gadget.isOnline)
                                    {
                                        string dataToSend = startCommand + JsonConvert.SerializeObject(item.CreateMessage()) + endCommand;
                                        var firstChar = Encoding.UTF8.GetBytes(startCommand);
                                        byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSend);
                                        await stream.WriteAsync(dataBytes, 0, dataBytes.Length);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    connectedGadgets.Find(k => k.IPAddress == item.gadget.IPAddress).isOnline = false;
                                    ServiceLogError(ex);
                                }
                                if (item.isResponseExpected)
                                {
                                    awaitingResponse.Add(item);
                                }
                                //ServiceLog($"Sent data to device {item.gadget.name}: " + dataToSend);
                            }
                        }
                    });
                    task.Start();
                    task.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    dataSendQueue.Add(item);
                    SendDataLoop();
                }
            }
            await Task.Delay(10);
            SendDataLoop();
        }
        public KliveTechGadget GetKliveTechGadgetByID(string id)
        {
            return connectedGadgets.Where(x => x.gadgetID == id).FirstOrDefault();
        }
        public KliveTechGadget GetKliveTechGadgetByName(string name)
        {
            return connectedGadgets.Where(x => x.name.ToLower() == name.Trim().ToLower()).FirstOrDefault();
        }
        public async Task<bool> ExecuteActionByName(KliveTechGadget gadget, string name, string data)
        {
            try
            {
                var action = gadget.actions.Where(x => x.name == name).FirstOrDefault();
                if (action != null)
                {
                    if (action.parameters == KliveTechActions.ActionParameterType.None)
                    {
                        data = null;
                    }
                    return await ExecuteGadgetAction(gadget, action, data);
                }
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }
        public async Task<bool> IsDeviceConnected(KliveTechGadget gadget, int attempts = 0)
        {
            int maxAttempts = 3;
            if (gadget.lastMessageReceived.AddSeconds(10) > DateTime.Now)
            {
                return true;
            }
            try
            {
                try
                {
                    var response = await SendData(gadget, KliveTechActions.OperationNumber.GetActions, "GetActions", true).WaitAsync(TimeSpan.FromSeconds(5));
                    if (response.status == HttpStatusCode.OK)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (TimeoutException ex)
                {
                    if (attempts >= maxAttempts)
                    {
                        return false;
                    }
                    else
                    {
                        return await IsDeviceConnected(gadget, attempts++);
                    }
                }
            }
            catch (Exception ex)
            {
                if (attempts >= maxAttempts)
                {
                    return false;
                }
                else
                {
                    return await IsDeviceConnected(gadget, attempts++);
                }
            }
        }
    }
}
