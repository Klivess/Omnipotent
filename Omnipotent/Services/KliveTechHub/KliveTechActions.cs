namespace Omnipotent.Services.KliveTechHub
{
    public class KliveTechActions
    {
        public enum ActionParameterType
        {
            Integer,
            String,
            Bool,
            None
        }

        public enum OperationNumber
        {
            ExecuteAction,
            GetActions,
            Ping,
            BeginFirmwareUpdate,
            FirmwareUpdateChunk,
            CompleteFirmwareUpdate,
            AbortFirmwareUpdate,
            GetStreamables,
            ConfigureStreamable
        }

        public struct KliveTechActionRequest
        {
            public string ActionName;
            public object? Param;
        }

        public class KliveTechAction
        {
            public string name = string.Empty;
            public ActionParameterType parameters;
            public string paramDescription = string.Empty;
        }
    }
}
