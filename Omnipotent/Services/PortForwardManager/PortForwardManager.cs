using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Service_Manager;
using Open.Nat;

namespace Omnipotent.Services.PortForwardManager
{
    public class PortForwardManager : OmniService
    {
        public PortForwardManager()
        {
            name = "PortForwardManager";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                await SetupRoutes();
                await ServiceLog("PortForwardManager routes successfully registered.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error starting PortForwardManager.");
            }
        }

        private async Task SetupRoutes()
        {
            await CreateAPIRoute("/admin/portforwarding/list", HandleListRequest, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/portforwarding/add", HandleAddRequest, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/portforwarding/delete", HandleDeleteRequest, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/portforwarding/edit", HandleEditRequest, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
        }

        // Returns true if a UPnP-enabled gateway device is present on the network.
        public async Task<bool> IsUpnpAvailable()
        {
            return (await DiscoverDeviceAsync()) != null;
        }

        // Returns the current port mappings on the UPnP device, or null if no device is available.
        public async Task<List<PortMappingModel>?> GetActiveMappings()
        {
            var device = await DiscoverDeviceAsync();
            if (device == null)
            {
                return null;
            }

            var openNatMappings = await device.GetAllMappingsAsync();
            var mappingsList = new List<PortMappingModel>();
            foreach (var mapping in openNatMappings)
            {
                var expirationStr = mapping.Expiration == DateTime.MaxValue || mapping.Lifetime == 0
                    ? "Permanent"
                    : mapping.Expiration.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss UTC");

                mappingsList.Add(new PortMappingModel
                {
                    Protocol = mapping.Protocol == Open.Nat.Protocol.Tcp ? "TCP" : "UDP",
                    PublicPort = mapping.PublicPort,
                    PrivatePort = mapping.PrivatePort,
                    PrivateIp = mapping.PrivateIP?.ToString() ?? string.Empty,
                    Description = mapping.Description ?? string.Empty,
                    LifetimeSeconds = mapping.Lifetime,
                    ExpirationDate = expirationStr
                });
            }
            return mappingsList;
        }

        // Ensures the given public port/protocol is forwarded to the local machine.
        // Returns true if the mapping had to be ADDED, false if it already existed or no UPnP device is available.
        // Uses a permanent lifetime (0).
        public async Task<bool> EnsurePortForwarded(int publicPort, int privatePort, string protocol, string description)
        {
            var device = await DiscoverDeviceAsync();
            if (device == null)
            {
                return false;
            }

            var protocolEnum = protocol.ToUpper() == "UDP" ? Open.Nat.Protocol.Udp : Open.Nat.Protocol.Tcp;

            var existingMappings = await device.GetAllMappingsAsync();
            foreach (var mapping in existingMappings)
            {
                if (mapping.PublicPort == publicPort && mapping.Protocol == protocolEnum)
                {
                    // Already forwarded.
                    return false;
                }
            }

            var privateIpAddr = IPAddress.Parse(GetLocalIPAddress());
            var newMapping = new Open.Nat.Mapping(protocolEnum, privateIpAddr, privatePort, publicPort, 0, description);
            await device.CreatePortMapAsync(newMapping);
            return true;
        }

        private async Task<NatDevice?> DiscoverDeviceAsync()
        {
            try
            {
                var discoverer = new NatDiscoverer();
                using var cts = new CancellationTokenSource(4000); // 4 seconds timeout
                return await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);
            }
            catch (NatDeviceNotFoundException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "UPnP device discovery encountered an error.", false);
                return null;
            }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                try
                {
                    foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        {
                            var ipProps = ni.GetIPProperties();
                            foreach (var addr in ipProps.UnicastAddresses)
                            {
                                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    return addr.Address.ToString();
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return "127.0.0.1";
        }

        private async Task HandleListRequest(UserRequest req)
        {
            try
            {
                var localIp = GetLocalIPAddress();
                var device = await DiscoverDeviceAsync();
                if (device == null)
                {
                    var emptyResult = new
                    {
                        success = false,
                        error = "No UPnP-enabled gateway device was discovered on the network.",
                        localIp = localIp,
                        externalIp = "Unknown",
                        mappings = new List<PortMappingModel>()
                    };
                    await req.ReturnResponse(JsonConvert.SerializeObject(emptyResult), "application/json");
                    return;
                }

                var externalIpTask = device.GetExternalIPAsync();
                var mappingsTask = device.GetAllMappingsAsync();

                await Task.WhenAll(externalIpTask, mappingsTask);

                var externalIp = externalIpTask.Result?.ToString() ?? "Unknown";
                var openNatMappings = mappingsTask.Result;

                var mappingsList = new List<PortMappingModel>();
                foreach (var mapping in openNatMappings)
                {
                    var expirationStr = mapping.Expiration == DateTime.MaxValue || mapping.Lifetime == 0
                        ? "Permanent"
                        : mapping.Expiration.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss UTC");

                    mappingsList.Add(new PortMappingModel
                    {
                        Protocol = mapping.Protocol == Open.Nat.Protocol.Tcp ? "TCP" : "UDP",
                        PublicPort = mapping.PublicPort,
                        PrivatePort = mapping.PrivatePort,
                        PrivateIp = mapping.PrivateIP?.ToString() ?? string.Empty,
                        Description = mapping.Description ?? string.Empty,
                        LifetimeSeconds = mapping.Lifetime,
                        ExpirationDate = expirationStr
                    });
                }

                var result = new
                {
                    success = true,
                    localIp = localIp,
                    externalIp = externalIp,
                    mappings = mappingsList
                };

                await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in HandleListRequest");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = ex.Message }), "application/json", code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleAddRequest(UserRequest req)
        {
            try
            {
                var body = req.userMessageContent;
                var model = JsonConvert.DeserializeObject<PortMappingModel>(body);
                if (model == null)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "Invalid request payload." }), "application/json", code: HttpStatusCode.BadRequest);
                    return;
                }

                var device = await DiscoverDeviceAsync();
                if (device == null)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "No UPnP device found." }), "application/json", code: HttpStatusCode.ServiceUnavailable);
                    return;
                }

                var protocol = model.Protocol.ToUpper() == "UDP" ? Open.Nat.Protocol.Udp : Open.Nat.Protocol.Tcp;
                
                IPAddress privateIpAddr;
                if (!IPAddress.TryParse(model.PrivateIp, out privateIpAddr))
                {
                    privateIpAddr = IPAddress.Parse(GetLocalIPAddress());
                }

                var mapping = new Open.Nat.Mapping(protocol, privateIpAddr, model.PrivatePort, model.PublicPort, model.LifetimeSeconds, model.Description);
                await device.CreatePortMapAsync(mapping);

                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = $"Port {model.PublicPort} successfully forwarded to {privateIpAddr}:{model.PrivatePort} ({model.Protocol})." }), "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in HandleAddRequest");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = ex.Message }), "application/json", code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleDeleteRequest(UserRequest req)
        {
            try
            {
                var body = req.userMessageContent;
                var model = JsonConvert.DeserializeObject<PortMappingModel>(body);
                if (model == null)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "Invalid request payload." }), "application/json", code: HttpStatusCode.BadRequest);
                    return;
                }

                var device = await DiscoverDeviceAsync();
                if (device == null)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "No UPnP device found." }), "application/json", code: HttpStatusCode.ServiceUnavailable);
                    return;
                }

                var protocol = model.Protocol.ToUpper() == "UDP" ? Open.Nat.Protocol.Udp : Open.Nat.Protocol.Tcp;
                var mapping = new Open.Nat.Mapping(protocol, model.PrivatePort, model.PublicPort);
                await device.DeletePortMapAsync(mapping);

                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = $"Port forward mapping for {model.PublicPort} ({model.Protocol}) successfully deleted." }), "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in HandleDeleteRequest");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = ex.Message }), "application/json", code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleEditRequest(UserRequest req)
        {
            try
            {
                var body = req.userMessageContent;
                var editModel = JsonConvert.DeserializeObject<PortEditModel>(body);
                if (editModel == null || editModel.OldMapping == null || editModel.NewMapping == null)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "Invalid request payload." }), "application/json", code: HttpStatusCode.BadRequest);
                    return;
                }

                var device = await DiscoverDeviceAsync();
                if (device == null)
                {
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = "No UPnP device found." }), "application/json", code: HttpStatusCode.ServiceUnavailable);
                    return;
                }

                // 1. Delete the old mapping
                var oldProtocol = editModel.OldMapping.Protocol.ToUpper() == "UDP" ? Open.Nat.Protocol.Udp : Open.Nat.Protocol.Tcp;
                var oldMapping = new Open.Nat.Mapping(oldProtocol, editModel.OldMapping.PrivatePort, editModel.OldMapping.PublicPort);
                try
                {
                    await device.DeletePortMapAsync(oldMapping);
                }
                catch (Exception ex)
                {
                    await ServiceLog($"Warning: failed to delete old mapping during edit: {ex.Message}");
                }

                // 2. Add the new mapping
                var newProtocol = editModel.NewMapping.Protocol.ToUpper() == "UDP" ? Open.Nat.Protocol.Udp : Open.Nat.Protocol.Tcp;
                IPAddress newPrivateIpAddr;
                if (!IPAddress.TryParse(editModel.NewMapping.PrivateIp, out newPrivateIpAddr))
                {
                    newPrivateIpAddr = IPAddress.Parse(GetLocalIPAddress());
                }

                var newMapping = new Open.Nat.Mapping(newProtocol, newPrivateIpAddr, editModel.NewMapping.PrivatePort, editModel.NewMapping.PublicPort, editModel.NewMapping.LifetimeSeconds, editModel.NewMapping.Description);
                await device.CreatePortMapAsync(newMapping);

                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true, message = "Port forward mapping successfully updated." }), "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in HandleEditRequest");
                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = false, error = ex.Message }), "application/json", code: HttpStatusCode.InternalServerError);
            }
        }
    }

    public class PortMappingModel
    {
        public string Protocol { get; set; } = "TCP";
        public int PublicPort { get; set; }
        public int PrivatePort { get; set; }
        public string PrivateIp { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int LifetimeSeconds { get; set; }
        public string ExpirationDate { get; set; } = string.Empty;
    }

    public class PortEditModel
    {
        public PortMappingModel OldMapping { get; set; } = null!;
        public PortMappingModel NewMapping { get; set; } = null!;
    }
}
