using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Services.OmniStartupManager
{
    public class OmniStartupManager : OmniService
    {
        public OmniStartupManager()
        {
            name = "Omnipotent Startup Manager";
            threadAnteriority = ThreadAnteriority.Critical;
        }

        protected override void ServiceMain()
        {
            try
            {
                FieldInfo[] fi = typeof(OmniPaths.GlobalPaths).GetFields(BindingFlags.Static | BindingFlags.Public);
                //Get each prerequisite path
                foreach (FieldInfo info in fi)
                {
                    string path = "";
                    try
                    {
                        //path
                        path = OmniPaths.GetPath(info.GetValue(null) as string);
                        //if empty, must be a directory
                        if (Path.GetExtension(path) == "")
                        {
                            if (Directory.Exists(path) == false)
                            {
                                Directory.CreateDirectory(path);
                            }
                        }
                        else
                        {
                            File.Create(path);
                        }
                    }
                    catch(Exception ex)
                    {
                        LogError(ex, "Couldn't create prerequisite file: " + path);
                    }
                }
            }
            catch(Exception ex)
            {
                LogError(ex, "Couldn't create prerequisites.");
            }

            //When ALL startup tasks are done.
            this.TerminateService();
        }
    }
}
