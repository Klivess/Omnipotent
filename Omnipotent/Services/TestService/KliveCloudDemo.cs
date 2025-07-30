using System;
using System.Threading.Tasks;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveCloud;

namespace Omnipotent.Services.TestService
{
    public class KliveCloudDemo : OmniService
    {
        public KliveCloudDemo()
        {
            name = "KliveCloudDemo";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override async void ServiceMain()
        {
            await ServiceLog("KliveCloudDemo starting...");
            
            // Wait a bit for other services to start
            await Task.Delay(5000);
            
            try
            {
                await ServiceLog("KliveCloud File Transfer Interface Demo");
                await ServiceLog("=====================================");
                await ServiceLog("");
                
                await ServiceLog("The following API routes have been created for file transfer:");
                await ServiceLog("1. GET  /klivecloud/allfiles - List all uploaded files");
                await ServiceLog("2. POST /klivecloud/makefolder - Create a new folder");
                await ServiceLog("3. POST /klivecloud/uploadfiles - Upload files with metadata tracking");
                await ServiceLog("4. GET  /klivecloud/downloadfile - Download files by ID");
                await ServiceLog("");
                
                await ServiceLog("Example usage:");
                await ServiceLog("- Upload: POST to /klivecloud/uploadfiles with JSON:");
                await ServiceLog("  {\"fileName\": \"test.txt\", \"fileContent\": \"<base64>\", \"path\": \"documents\"}");
                await ServiceLog("- Download: GET /klivecloud/downloadfile?fileId=<fileId>");
                await ServiceLog("- List files: GET /klivecloud/allfiles");
                await ServiceLog("- Create folder: POST /klivecloud/makefolder with path parameter");
                await ServiceLog("");
                
                await ServiceLog("Features implemented:");
                await ServiceLog("✓ File upload with Base64 encoding");
                await ServiceLog("✓ File download with metadata");
                await ServiceLog("✓ Directory creation");
                await ServiceLog("✓ File listing with metadata");
                await ServiceLog("✓ User authentication via KliveAPI");
                await ServiceLog("✓ File metadata tracking (uploader, time, hash, etc.)");
                await ServiceLog("✓ Path sanitization for security");
                await ServiceLog("✓ Error handling and logging");
                await ServiceLog("✓ Content type detection");
                await ServiceLog("✓ File hashing for integrity");
                await ServiceLog("");
                
                await ServiceLog("Storage locations:");
                await ServiceLog($"- Files: SavedData/KliveCloud/Files/");
                await ServiceLog($"- Metadata: SavedData/KliveCloud/Metadata/");
                await ServiceLog("");
                
                await ServiceLog("Authentication: Associate level or higher required for all operations");
                await ServiceLog("");
                await ServiceLog("KliveCloud File Transfer Interface is ready for use!");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error in KliveCloudDemo");
            }
            
            // Keep the demo service running
            while (true)
            {
                await Task.Delay(60000); // Demo heartbeat every minute
            }
        }
    }
}