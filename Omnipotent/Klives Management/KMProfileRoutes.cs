using Newtonsoft.Json;
using Omnipotent.Profiles;
using static Omnipotent.Profiles.KMProfileManager;
using System.Net;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Xml.Linq;

namespace Omnipotent.Klives_Management
{
    public class KMProfileRoutes
    {
        private KMProfileManager p;
        public KMProfileRoutes(KMProfileManager parent)
        {
            p = parent;
        }


    }
}
