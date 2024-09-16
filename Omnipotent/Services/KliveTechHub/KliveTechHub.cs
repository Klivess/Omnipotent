using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using Omnipotent.Service_Manager;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    public class KliveTechHub : OmniService
    {
        public KliveTechHub()
        {
            name = "KliveTech Hub";
            threadAnteriority = ThreadAnteriority.High;
        }
        BluetoothClient client = new BluetoothClient();
        List<KliveTechGadget> connectedGadgets = new();
        public struct KliveTechGadget
        {
            public string name;
            public string IPAddress;
            public BluetoothDeviceInfo deviceInfo;
            public BluetoothClient connectedClient;
        }
        protected async override void ServiceMain()
        {
            DiscoverNewKliveTechGadgets();
            Task.Delay(TimeSpan.FromSeconds(5)).Wait();
            SendData(connectedGadgets.Last(), "Hello from KliveTech Hub!");
        }
        private async Task DiscoverNewKliveTechGadgets()
        {
            BluetoothDeviceInfo[] devicesInRadius = client.DiscoverDevices();
            foreach (BluetoothDeviceInfo device in devicesInRadius)
            {
                if (device.DeviceName.ToLower().StartsWith("klivetech"))
                {
                    KliveTechGadget gadget = new KliveTechGadget();
                    gadget.name = device.DeviceName;
                    gadget.IPAddress = device.DeviceAddress.ToString();
                    gadget.deviceInfo = device;
                    gadget.connectedClient = new BluetoothClient();
                    gadget.connectedClient.Connect(device.DeviceAddress, BluetoothService.SerialPort);
                    connectedGadgets.Add(gadget);
                    ServiceLog("Found KliveTech gadget: " + gadget.name);
                    ReadData(connectedGadgets.Last());
                    SendData(connectedGadgets.Last(), "Hello from KliveTech Hub!");
                }
            }
            Task.Delay(5000).Wait();
            GC.Collect();
            DiscoverNewKliveTechGadgets();
        }
        private async Task ReadData(KliveTechGadget gadget)
        {
            Thread thread = new Thread(() =>
            {
                // Receiving data
                byte[] receiveBuffer = new byte[1024];
                NetworkStream stream = gadget.connectedClient.GetStream();
                int bytesRead = stream.Read(receiveBuffer, 0, receiveBuffer.Length);
                string receivedData = Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead);
                ServiceLog($"Received data from device {gadget.name}:" + receivedData);
                Task.Delay(100).Wait();
                ReadData(gadget);
            });
            thread.Start();
        }

        private async Task SendData(KliveTechGadget gadget, string data)
        {
            NetworkStream stream = gadget.connectedClient.GetStream();
            string dataToSend = data;
            byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSend);
            await stream.WriteAsync(dataBytes, 0, dataBytes.Length);
            ServiceLog($"Sent data to device {gadget.name}: " + dataToSend);
        }
    }
}