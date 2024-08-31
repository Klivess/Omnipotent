using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.IO;
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
            int directoriesCreated = 0;
            int filesCreated = 0;
            try
            {
                FieldInfo[] fi = typeof(OmniPaths.GlobalPaths).GetFields(BindingFlags.Static | BindingFlags.Public);
                //seperate directories
                List<string> directories = new();
                List<string> files = new();
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
                            directories.Add(path);
                        }
                        //If not, probably a file
                        else
                        {
                            files.Add(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, "Couldn't translate prerequisite file: " + path);
                    }
                }
                //Loop over directories first, make directories first
                //Make sure top-level directories are made first, sorta ducttape
                directories = directories.OrderBy(k => k.Length).ToList();
                foreach (string dir in directories)
                {
                    if (Directory.Exists(OmniPaths.GetPath(dir)) == false)
                    {
                        Directory.CreateDirectory(dir);
                        directoriesCreated++;
                    }
                }
                //Now, make prereq files
                foreach (string file in files)
                {
                    if (File.Exists(OmniPaths.GetPath(file)) == false)
                    {
                        File.Create(file);
                        filesCreated++;
                    }
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Couldn't create prerequisites.");
            }

            if (directoriesCreated > 0 || filesCreated > 0)
            {
                ServiceLog($"Prerequisites process complete: {directoriesCreated} directories created, {filesCreated} files created.");
            }

            //When ALL startup tasks are done.
            this.TerminateService();
        }
    }
}
