namespace Omnipotent.Services.KliveTechHub
{
    public class KliveTechActions
    {
        public enum ActionParameterType
        {
            Integer,
            String,
            Bool
        }

        public class KliveTechAction
        {
            public string name;
            public ActionParameterType parameters;
            public string paramDescription;
        }
    }
}
