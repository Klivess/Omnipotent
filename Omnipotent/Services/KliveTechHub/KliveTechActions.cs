﻿namespace Omnipotent.Services.KliveTechHub
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
            Ping
        }

        public struct KliveTechActionRequest
        {
            public string ActionName;
            public string Param;
        }

        public class KliveTechAction
        {
            public string name;
            public ActionParameterType parameters;
            public string paramDescription;
        }
    }
}
