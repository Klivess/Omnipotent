using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using LLama.Batched;
using Markdig.Parsers;
using Markdig.Syntax;
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
        public struct KliveTechGadget
        {
            public string name;
            public string IPAddress;
            public string gadgetID;
            public BluetoothDeviceInfo deviceInfo;
            public BluetoothClient connectedClient;
            public List<KliveTechActions.KliveTechAction> actions;
            public DateTime timeConnected;

            public Thread ReceiveLoop;
            public KliveTechGadget()
            {
                actions = new();
                gadgetID = RandomGeneration.GenerateRandomLengthOfNumbers(15);
            }
            public void DisconnectDevice()
            {
                connectedClient.Close();
                connectedClient.Dispose();
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

            public string SerialiseToKliveTechReadable()
            {
                //Don't send any curly brackets
                return $"{{ID{{{ID}}} DATA{{{dataToSend}}} RESPEXPECT{{{isResponseExpected.ToString().ToLower()}}}}}";
            }
        }
        private struct KliveTechGadgetResponse
        {
            //Expected Response: {ID{string} RESP{string} RESPEXPECT{true/false} STATUS{string}}
            public string ID;
            public string response;
            public bool isResponseExpected;
            public HttpStatusCode status;

            public KliveTechGadgetResponse DeserialiseKliveTechResponse(string input)
            {
                //Don't receive any curly brackets
                // Expected Response: {ID{string} RESP{string} RESPEXPECT{true/false} STATUS{string}}
                // Regular expressions to extract values from the input string
                string idPattern = @"ID{([^}]*)}";
                string respPattern = @"RESP{([^}]*)}";
                string respExpectPattern = @"RESPEXPECT{([^}]*)}";
                string statusPattern = @"STATUS{([^}]*)}";

                // Extract ID (string)
                string id = Regex.Match(input, idPattern).Groups[1].Value;

                // Extract RESP (string)
                string resp = Regex.Match(input, respPattern).Groups[1].Value;

                // Extract RESPEXPECT (bool)
                bool respExpect = Regex.Match(input, respExpectPattern).Groups[1].Value == "true";

                // Extract STATUS (int)
                int status = int.Parse(Regex.Match(input, statusPattern).Groups[1].Value);

                //Return
                ID = id;
                response = resp;
                isResponseExpected = respExpect;
                this.status = (HttpStatusCode)status;
                return this;
            }
        }
        private bool CheckIfBluetoothProtocolExistsOnDevice()
        {
            return BluetoothRadio.IsSupported;
        }
        protected async override void ServiceMain()
        {
            if (!CheckIfBluetoothProtocolExistsOnDevice())
            {
                ServiceLog("No Bluetooth on this device, so terminating service.");
                TerminateService();
                return;
            }
            KliveTechRoutes = new(this);
            KliveTechRoutes.RegisterRoutes();
            Task sendDataLoop = new Task(async () => { SendDataLoop(); });
            Task checkConnectionStatusOfGadgets = new Task(async () => { CheckConnectionStatusOfGadgets(); });
            Task discoverNewGadgets = new Task(async () => { DiscoverNewKliveTechGadgets(); });
            sendDataLoop.Start();
            checkConnectionStatusOfGadgets.Start();
            discoverNewGadgets.Start();
            //SendData(connectedGadgets.Last(), "Hello from KliveTech Hub!");
        }
        public async void CheckConnectionStatusOfGadgets()
        {
            try
            {
                foreach (var item in connectedGadgets)
                {
                    if (await IsDeviceConnected(item) == false)
                    {
                        ServiceLog($"Device {item.name} disconnected!");
                        item.DisconnectDevice();
                        connectedGadgets.Remove(item);
                    }
                }
                await Task.Delay(5000);
                CheckConnectionStatusOfGadgets();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex);
                CheckConnectionStatusOfGadgets();
            }
        }
        private async Task GetGadgetActions(KliveTechGadget gadget)
        {
            KliveTechGadgetResponse res;
            try
            {
                res = await SendData(gadget, "GetActions", true).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Get Gadget Actions failed, retrying.");
                await GetGadgetActions(gadget);
                return;
            }
            if (res.status == HttpStatusCode.OK)
            {
                try
                {
                    // Regular expression pattern to match items in the format ITEMX(name, type, paramDescription)(ENDITEM)
                    string pattern = @"ITEM\d+\(([^,]+),\s*(\d+),\s*([^)]*)\)\(ENDITEM\)";

                    // Loop through each match in the input string
                    foreach (Match match in Regex.Matches(res.response, pattern))
                    {
                        // Extract name (string)
                        string name = match.Groups[1].Value;

                        // Extract type (int)
                        var type = (KliveTechActions.ActionParameterType)int.Parse(match.Groups[2].Value);

                        // Extract paramDescription (string)
                        string paramDescription = match.Groups[3].Value;

                        //Add the action to the list
                        var action = new KliveTechActions.KliveTechAction() { name = name, parameters = (KliveTechActions.ActionParameterType)type, paramDescription = paramDescription };
                        if (gadget.actions.Select(k => k.name).Contains(action.name) == false)
                        {
                            gadget.actions.Add(action);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "Get Gadget Actions failed, retrying.");
                    await GetGadgetActions(gadget);
                    return;
                }
            }
            else
            {
                await ServiceLogError(new Exception("Failed to get gadget actions"), "Get Gadget Actions failed, retrying.");
                await GetGadgetActions(gadget);
                return;
            }
        }
        private async Task DiscoverNewKliveTechGadgets()
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
                            KliveTechGadget gadget = new KliveTechGadget();
                            gadget.name = device.DeviceName;
                            gadget.IPAddress = device.DeviceAddress.ToString();
                            gadget.deviceInfo = device;
                            gadget.connectedClient = new BluetoothClient();
                            gadget.connectedClient.Connect(device.DeviceAddress, BluetoothService.SerialPort);
                            gadget.timeConnected = DateTime.Now;
                            ServiceLog("Found KliveTech gadget: " + gadget.name);
                            gadget.ReceiveLoop = new Thread(async () => { ReadDataLoop(gadget); });
                            gadget.ReceiveLoop.Start();
                            connectedGadgets.Add(gadget);
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
                            GetGadgActions.Start();
                        }
                        catch (Exception ex)
                        {
                            ServiceLogError(ex);
                            DiscoverNewKliveTechGadgets();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex);
                }
            }
            GC.Collect();
            DiscoverNewKliveTechGadgets();
        }
        private async Task<bool> ExecuteGadgetAction(KliveTechGadget gadget, KliveTechActions.KliveTechAction action, string data)
        {
            var result = SendData(gadget, $"{action.name}({data})", true);
            return (await result).status == HttpStatusCode.OK;
        }
        private async Task ReadDataLoop(KliveTechGadget gadget)
        {
            Thread thread = new Thread(async () =>
            {
                if (!await IsDeviceConnected(gadget))
                {
                    AnnounceGadgetDisconnect(gadget);
                    return;
                }
                else
                {
                    try
                    {
                        // Receiving data
                        byte[] receiveBuffer = new byte[1024];
                        NetworkStream stream = gadget.connectedClient.GetStream();
                        int bytesRead = 0;
                        string receivedData = "";
                        while (receivedData.EndsWith(endCommand) == false)
                        {
                            if (receivedData.StartsWith(startCommand))
                            {
                                receivedData = "";
                            }
                            bytesRead = await stream.ReadAsync(receiveBuffer, 0, receiveBuffer.Length);
                            receivedData += Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead);
                        }
                        string result = receivedData.Replace(startCommand, "").Replace(endCommand, "");
                        receivedData = "";
                        if (!string.IsNullOrEmpty(result.Trim()))
                        {
                            ServiceLog($"Received data from device {gadget.name}: " + result);
                            KliveTechGadgetResponse Response = new KliveTechGadgetResponse().DeserialiseKliveTechResponse(result);
                            if (awaitingResponse.Select(k => k.ID).ToList().Contains(Convert.ToString(Response.ID)) == true)
                            {
                                awaitingResponse.Find(k => k.ID == Convert.ToString(Response.ID)).response.SetResult(Response);
                            }
                            Task.Delay(100).Wait();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.ToLower().Contains("aborted"))
                        {
                            AnnounceGadgetDisconnect(gadget);
                        }
                        else
                        {
                            ServiceLogError(ex);
                        }
                    }
                    ReadDataLoop(gadget);
                }
            });
            thread.Start();
            return;
        }
        private async Task<KliveTechGadgetResponse> SendData(KliveTechGadget gadget, string data, bool expectResponse = false)
        {
            DataQueue dataQueue = new DataQueue();
            dataQueue.gadget = gadget;
            dataQueue.dataToSend = data;
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
                        if (item.type == DataQueueType.Send)
                        {
                            if (await IsDeviceConnected(item.gadget) == false)
                            {
                                AnnounceGadgetDisconnect(item.gadget);
                                return;
                            }
                            NetworkStream stream = null;
                            Task task = new Task(async () =>
                            {
                                stream = item.gadget.connectedClient.GetStream();
                            });
                            task.Start();
                            task.Wait(TimeSpan.FromSeconds(5));
                            if (stream == null)
                            {
                                AnnounceGadgetDisconnect(item.gadget);
                                return;
                            }
                            try
                            {
                                string dataToSend = startCommand + item.SerialiseToKliveTechReadable() + endCommand;
                                var firstChar = Encoding.UTF8.GetBytes(startCommand);
                                byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSend);
                                await stream.WriteAsync(dataBytes, 0, dataBytes.Length);
                            }
                            catch (Exception ex)
                            {
                                ServiceLogError(ex);
                                AnnounceGadgetDisconnect(item.gadget);
                            }
                            if (item.isResponseExpected)
                            {
                                awaitingResponse.Add(item);
                            }
                            //ServiceLog($"Sent data to device {item.gadget.name}: " + dataToSend);
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
        public async Task<bool> ExecuteActionByName(KliveTechGadget gadget, string name, string data)
        {
            var action = gadget.actions.Where(x => x.name == name).FirstOrDefault();
            if (action != null)
            {
                return await ExecuteGadgetAction(gadget, action, data);
            }
            return false;
        }
        public async Task<bool> IsDeviceConnected(KliveTechGadget gadget)
        {
            return gadget.connectedClient.Connected;
        }
        private void AnnounceGadgetDisconnect(KliveTechGadget g)
        {
            ServiceLog($"Device {g.name} disconnected!");
            g.DisconnectDevice();
            try
            {
                connectedGadgets.Remove(g);
            }
            catch (Exception ex) { }
        }
    }
}
