using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;

namespace Omnipotent.Services.KliveTechHub
{
    public sealed class KliveTechRoutes
    {
        private readonly KliveTechHub parent;

        public KliveTechRoutes(KliveTechHub parentService)
        {
            parent = parentService;
        }

        public async Task RegisterRoutes()
        {
            await parent.CreateAPIRoute(
                "/klivetech/GetAllGadgets",
                GetAllGadgets,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/executegadgetaction",
                ExecuteGadgetAction,
                HttpMethod.Post,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/GetGadgetByID",
                GetGadgetById,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/firmware/config",
                GetFirmwareConfiguration,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/firmware/projects",
                GetFirmwareProjects,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/firmware/compile",
                CompileFirmware,
                HttpMethod.Post,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/firmware/update",
                UpdateFirmware,
                HttpMethod.Post,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/firmware/jobs",
                GetFirmwareJobs,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/firmware/jobs/cancel",
                CancelFirmwareJob,
                HttpMethod.Post,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/streamables",
                GetStreamables,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/streamables/history",
                GetStreamableHistory,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/streamables/latest",
                GetLatestStreamableBinary,
                HttpMethod.Get,
                KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute(
                "/klivetech/streamables/control",
                ConfigureStreamable,
                HttpMethod.Post,
                KMProfileManager.KMPermissions.Klives);

            await parent.RegisterRelayRouteAsync();
            await parent.RegisterStreamableLiveRouteAsync();
        }

        private async Task GetStreamables(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                string? gadgetId = request.userParameters.Get("gadgetID");
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(parent.GetStreamableCatalog(gadgetId)),
                    "application/json");
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task GetStreamableHistory(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                string gadgetId = request.userParameters.Get("gadgetID") ?? string.Empty;
                string streamId = request.userParameters.Get("streamID") ?? string.Empty;
                int limit = 100;
                string? suppliedLimit = request.userParameters.Get("limit");
                if (!string.IsNullOrWhiteSpace(suppliedLimit) &&
                    !int.TryParse(suppliedLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
                {
                    await ReturnJsonError(request, "limit must be an integer.", HttpStatusCode.BadRequest);
                    return;
                }
                ulong? afterSequence = null;
                string? suppliedSequence = request.userParameters.Get("afterSequence");
                if (!string.IsNullOrWhiteSpace(suppliedSequence))
                {
                    if (!ulong.TryParse(
                            suppliedSequence,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ulong parsedSequence))
                    {
                        await ReturnJsonError(
                            request,
                            "afterSequence must be an unsigned integer.",
                            HttpStatusCode.BadRequest);
                        return;
                    }
                    afterSequence = parsedSequence;
                }

                await request.ReturnResponse(
                    JsonConvert.SerializeObject(parent.GetStreamableHistory(
                        gadgetId,
                        streamId,
                        limit,
                        afterSequence)),
                    "application/json");
            }
            catch (KeyNotFoundException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.NotFound);
            }
            catch (InvalidDataException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task GetLatestStreamableBinary(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                string gadgetId = request.userParameters.Get("gadgetID") ?? string.Empty;
                string streamId = request.userParameters.Get("streamID") ?? string.Empty;
                KliveTechHub.StreamableBinaryData? binary =
                    parent.GetLatestStreamableBinary(gadgetId, streamId);
                if (binary == null)
                {
                    await ReturnJsonError(
                        request,
                        "The Streamable has no completed binary frame.",
                        HttpStatusCode.NotFound);
                    return;
                }
                NameValueCollection headers = new()
                {
                    ["Cache-Control"] = "private, no-store",
                    ["X-KliveTech-Sequence"] = binary.Sequence.ToString(CultureInfo.InvariantCulture),
                    ["X-KliveTech-Session"] = binary.SessionID,
                    ["X-KliveTech-SHA256"] = binary.Sha256,
                    ["X-KliveTech-Received-UTC"] = binary.ReceivedUtc.ToString("O", CultureInfo.InvariantCulture)
                };
                await request.ReturnBinaryResponse(
                    binary.Data,
                    binary.MimeType,
                    HttpStatusCode.OK,
                    headers);
            }
            catch (KeyNotFoundException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.NotFound);
            }
            catch (InvalidDataException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task ConfigureStreamable(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                JObject body = ParseJsonBody(request);
                string gadgetId = body.Value<string>("gadgetID") ?? string.Empty;
                string streamId = body.Value<string>("streamID") ?? string.Empty;
                if (body["enabled"]?.Type != JTokenType.Boolean)
                {
                    await ReturnJsonError(request, "enabled must be a boolean.", HttpStatusCode.BadRequest);
                    return;
                }
                uint? intervalMs = null;
                if (body["intervalMs"] != null)
                {
                    if (body["intervalMs"]!.Type != JTokenType.Integer)
                    {
                        await ReturnJsonError(
                            request,
                            "intervalMs must be an unsigned integer.",
                            HttpStatusCode.BadRequest);
                        return;
                    }
                    try
                    {
                        intervalMs = body["intervalMs"]!.Value<uint>();
                    }
                    catch (Exception ex) when (ex is OverflowException or FormatException)
                    {
                        await ReturnJsonError(
                            request,
                            "intervalMs must be an unsigned 32-bit integer.",
                            HttpStatusCode.BadRequest);
                        return;
                    }
                }

                KliveTechHub.StreamableCatalogEntry result = await parent.ConfigureStreamableAsync(
                    gadgetId,
                    streamId,
                    body.Value<bool>("enabled"),
                    intervalMs,
                    CancellationToken.None);
                await request.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
            }
            catch (JsonException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (InvalidDataException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (KeyNotFoundException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.NotFound);
            }
            catch (TimeoutException ex)
            {
                await parent.ServiceLogError(ex, "KliveTech Streamables control timed out.");
                await ReturnJsonError(request, "The gadget timed out.", HttpStatusCode.GatewayTimeout);
            }
            catch (IOException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.ServiceUnavailable);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task GetFirmwareConfiguration(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(parent.GetFirmwareConfiguration()),
                    "application/json");
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task GetFirmwareProjects(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(parent.GetFirmwareProjects()),
                    "application/json");
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task CompileFirmware(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                JObject body = ParseJsonBody(request);
                string project = body.Value<string>("project") ?? string.Empty;
                KliveTechHub.FirmwareJob job = parent.StartFirmwareCompile(
                    project,
                    body.Value<string>("fqbn"),
                    body.Value<string>("partitionScheme"));
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(job),
                    "application/json",
                    new NameValueCollection(),
                    HttpStatusCode.Accepted);
            }
            catch (Exception ex) when (IsFirmwareRequestError(ex))
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task UpdateFirmware(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                JObject body = ParseJsonBody(request);
                string gadgetId = body.Value<string>("gadgetID") ?? string.Empty;
                string project = body.Value<string>("project") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(gadgetId))
                {
                    await ReturnJsonError(request, "gadgetID is required.", HttpStatusCode.BadRequest);
                    return;
                }

                KliveTechHub.FirmwareJob job = parent.StartFirmwareUpdate(
                    gadgetId,
                    project,
                    body.Value<string>("fqbn"),
                    body.Value<string>("partitionScheme"));
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(job),
                    "application/json",
                    new NameValueCollection(),
                    HttpStatusCode.Accepted);
            }
            catch (KeyNotFoundException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.NotFound);
            }
            catch (Exception ex) when (IsFirmwareRequestError(ex))
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (InvalidOperationException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.Conflict);
            }
            catch (IOException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.ServiceUnavailable);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task GetFirmwareJobs(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                string? jobId = request.userParameters.Get("jobID");
                IReadOnlyList<KliveTechHub.FirmwareJob> jobs = parent.GetFirmwareJobs(jobId);
                if (!string.IsNullOrWhiteSpace(jobId) && jobs.Count == 0)
                {
                    await ReturnJsonError(request, "Firmware job not found.", HttpStatusCode.NotFound);
                    return;
                }
                await request.ReturnResponse(JsonConvert.SerializeObject(jobs), "application/json");
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task CancelFirmwareJob(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                JObject body = ParseJsonBody(request);
                string jobId = body.Value<string>("jobID")
                    ?? request.userParameters.Get("jobID")
                    ?? string.Empty;
                if (!parent.CancelFirmwareJob(jobId))
                {
                    await ReturnJsonError(
                        request,
                        "Active firmware job not found.",
                        HttpStatusCode.NotFound);
                    return;
                }
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = true, jobID = jobId }),
                    "application/json");
            }
            catch (JsonException ex)
            {
                await ReturnJsonError(request, ex.Message, HttpStatusCode.BadRequest);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private static JObject ParseJsonBody(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.userMessageContent))
            {
                return new JObject();
            }
            return JObject.Parse(request.userMessageContent);
        }

        private static bool IsFirmwareRequestError(Exception exception)
        {
            return exception is JsonException or InvalidDataException or DirectoryNotFoundException or
                UnauthorizedAccessException;
        }

        private async Task GetAllGadgets(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                await parent.ServiceLog(
                    $"Request from {request.user?.Name ?? "unknown"} to get all KliveTech gadgets.");
                await request.ReturnResponse(
                    JsonConvert.SerializeObject(parent.connectedGadgets),
                    "application/json");
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task GetGadgetById(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                string? gadgetId = request.userParameters.Get("gadgetID");
                if (string.IsNullOrWhiteSpace(gadgetId))
                {
                    await ReturnJsonError(request, "gadgetID is required.", HttpStatusCode.BadRequest);
                    return;
                }

                KliveTechHub.KliveTechGadget? gadget = parent.GetKliveTechGadgetByID(gadgetId);
                if (gadget == null)
                {
                    await ReturnJsonError(request, "Gadget not found.", HttpStatusCode.NotFound);
                    return;
                }

                await request.ReturnResponse(JsonConvert.SerializeObject(gadget), "application/json");
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private async Task ExecuteGadgetAction(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request)
        {
            try
            {
                string? gadgetId = request.userParameters.Get("gadgetID");
                string? gadgetName = request.userParameters.Get("gadgetName");
                string? actionName = request.userParameters.Get("actionName");
                string actionParameter = request.userParameters.Get("actionParam") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(actionName))
                {
                    await ReturnJsonError(request, "actionName is required.", HttpStatusCode.BadRequest);
                    return;
                }

                KliveTechHub.KliveTechGadget? gadget = !string.IsNullOrWhiteSpace(gadgetId)
                    ? parent.GetKliveTechGadgetByID(gadgetId)
                    : parent.GetKliveTechGadgetByName(gadgetName);

                if (gadget == null)
                {
                    await ReturnJsonError(request, "Gadget not found.", HttpStatusCode.NotFound);
                    return;
                }

                KliveTechActions.KliveTechAction? action = gadget.actions.FirstOrDefault(
                    candidate => string.Equals(candidate.name, actionName, StringComparison.Ordinal));
                if (action == null)
                {
                    await ReturnJsonError(request, "Action not found.", HttpStatusCode.NotFound);
                    return;
                }

                if (!IsValidParameter(action, actionParameter, out string? validationError))
                {
                    await ReturnJsonError(request, validationError!, HttpStatusCode.BadRequest);
                    return;
                }

                await parent.ServiceLog(
                    $"Request from {request.user?.Name ?? "unknown"} to execute " +
                    $"gadget '{gadget.name}' action '{action.name}'.");

                bool executed = await parent.ExecuteActionByName(gadget, action.name, actionParameter);
                if (!executed)
                {
                    await ReturnJsonError(
                        request,
                        "The gadget did not acknowledge the action.",
                        HttpStatusCode.ServiceUnavailable);
                    return;
                }

                await request.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = true }),
                    "application/json");
            }
            catch (TimeoutException ex)
            {
                await parent.ServiceLogError(ex, "KliveTech action timed out.");
                await ReturnJsonError(request, "The gadget timed out.", HttpStatusCode.GatewayTimeout);
            }
            catch (IOException ex)
            {
                await parent.ServiceLogError(ex, "KliveTech gadget became unavailable.");
                await ReturnJsonError(request, "The gadget is unavailable.", HttpStatusCode.ServiceUnavailable);
            }
            catch (OperationCanceledException ex)
            {
                await parent.ServiceLogError(ex, "KliveTech action was cancelled.");
                await ReturnJsonError(request, "The gadget is unavailable.", HttpStatusCode.ServiceUnavailable);
            }
            catch (Exception ex)
            {
                await ReturnError(request, ex);
            }
        }

        private static bool IsValidParameter(
            KliveTechActions.KliveTechAction action,
            string parameter,
            out string? error)
        {
            error = null;
            switch (action.parameters)
            {
                case KliveTechActions.ActionParameterType.Integer:
                    if (!int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        error = "actionParam must be a valid 32-bit integer.";
                        return false;
                    }
                    return true;

                case KliveTechActions.ActionParameterType.Bool:
                    if (!bool.TryParse(parameter, out _))
                    {
                        error = "actionParam must be true or false.";
                        return false;
                    }
                    return true;

                case KliveTechActions.ActionParameterType.String:
                case KliveTechActions.ActionParameterType.None:
                    return true;

                default:
                    error = "The gadget advertised an unsupported parameter type.";
                    return false;
            }
        }

        private async Task ReturnError(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request,
            Exception exception)
        {
            await parent.ServiceLogError(exception, "KliveTech API request failed.");
            await ReturnJsonError(request, "KliveTech request failed.", HttpStatusCode.InternalServerError);
        }

        private static Task ReturnJsonError(
            Omnipotent.Services.KliveAPI.KliveAPI.UserRequest request,
            string message,
            HttpStatusCode status)
        {
            return request.ReturnResponse(
                JsonConvert.SerializeObject(new { error = message }),
                "application/json",
                new NameValueCollection(),
                status);
        }
    }
}
