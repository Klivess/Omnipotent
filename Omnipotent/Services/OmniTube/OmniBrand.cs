using Omnipotent.Data_Handling;

namespace Omnipotent.Services.OmniTube
{
    public class OmniBrand
    {
        public string Name;
        public string Description;

        public OmniTube parent;

        public string DirectoryPath;

        public OmniBrand(OmniTube parent)
        {
            this.parent = parent;

            DirectoryPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTubeBrandsDirectory), $"{Name}Brand");
            Directory.CreateDirectory(DirectoryPath);
        }

        public string ProduceVideo()
        {
            string pathOfVideo = "";
            return pathOfVideo;
        }
    }
}
