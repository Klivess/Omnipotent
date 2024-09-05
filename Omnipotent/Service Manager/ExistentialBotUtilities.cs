using System.Diagnostics;

namespace Omnipotent.Service_Manager
{
    public class ExistentialBotUtilities
    {
        public static void RestartBot()
        {
            Process.Start("Omnipotent.exe");
            Environment.Exit(-1);
        }
        public static void QuitBot()
        {
            Process.Start("Omnipotent.exe");
            Environment.Exit(0);
        }
    }
}
