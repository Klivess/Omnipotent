using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using System.Net;
using System.Security.Cryptography;

namespace Omnipotent.Services.KliveCloud
{
    public class KliveCloud : OmniService
    {
        private DataUtil dataHandler;
        private KliveAPI.KliveAPI kliveAPI;

        public KliveCloud()
        {
            name = "KliveCloud";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                await ServiceLog("KliveCloud service starting...");
                
                // Get required services
                dataHandler = GetDataHandler();
                kliveAPI = await serviceManager.GetKliveAPIService();
                
                // Initialize storage directories
                await InitializeStorageDirectories();
                
                // Setup API routes
                await SetupFileTransferRoutes();
                
                await ServiceLog("KliveCloud service started successfully");
                
                // Main service loop - keep the service running
                while (true)
                {
                    await Task.Delay(1000); // Basic heartbeat delay
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Failed to start KliveCloud service");
            }
        }

        private async Task InitializeStorageDirectories()
        {
            try
            {
                string filesDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFilesDirectory);
                string metadataDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudMetadataDirectory);
                
                await dataHandler.CreateDirectory(filesDir);
                await dataHandler.CreateDirectory(metadataDir);
                
                await ServiceLog("Storage directories initialized");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Failed to initialize storage directories");
                throw;
            }
        }

        private async Task SetupFileTransferRoutes()
        {
            try
            {
                // Route to list all files
                await kliveAPI.CreateRoute("klivecloud/allfiles", async (req) =>
                {
                    await HandleGetAllFiles(req);
                }, HttpMethod.Get, KMProfileManager.KMPermissions.Associate);

                // Route to create folder
                await kliveAPI.CreateRoute("klivecloud/makefolder", async (req) =>
                {
                    await HandleMakeFolder(req);
                }, HttpMethod.Post, KMProfileManager.KMPermissions.Associate);

                // Route to upload files
                await kliveAPI.CreateRoute("klivecloud/uploadfiles", async (req) =>
                {
                    await HandleUploadFiles(req);
                }, HttpMethod.Post, KMProfileManager.KMPermissions.Associate);

                // Route to download file
                await kliveAPI.CreateRoute("klivecloud/downloadfile", async (req) =>
                {
                    await HandleDownloadFile(req);
                }, HttpMethod.Get, KMProfileManager.KMPermissions.Associate);

                await ServiceLog("File transfer routes created successfully");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Failed to setup file transfer routes");
                throw;
            }
        }

        private async Task HandleGetAllFiles(KliveAPI.KliveAPI.UserRequest req)
        {
            try
            {
                string metadataDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudMetadataDirectory);
                var allFiles = new List<FileMetadata>();

                if (Directory.Exists(metadataDir))
                {
                    string[] metadataFiles = Directory.GetFiles(metadataDir, "*.json");
                    
                    foreach (string metadataFile in metadataFiles)
                    {
                        try
                        {
                            var metadata = await dataHandler.ReadAndDeserialiseDataFromFile<FileMetadata>(metadataFile);
                            allFiles.Add(metadata);
                        }
                        catch (Exception ex)
                        {
                            await ServiceLog($"Failed to read metadata file {metadataFile}: {ex.Message}");
                        }
                    }
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(allFiles), "application/json");
                await ServiceLog($"Listed {allFiles.Count} files for user {req.user?.Name ?? "unknown"}");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error handling get all files request");
                await req.ReturnResponse("Error retrieving files", "text/plain", null, HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleMakeFolder(KliveAPI.KliveAPI.UserRequest req)
        {
            try
            {
                string folderPath = req.userParameters.Get("path") ?? "";
                
                if (string.IsNullOrEmpty(folderPath))
                {
                    await req.ReturnResponse("Path parameter is required", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }

                // Sanitize path to prevent directory traversal
                folderPath = SanitizePath(folderPath);
                string fullPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFilesDirectory), folderPath);
                
                await dataHandler.CreateDirectory(fullPath);
                
                await req.ReturnResponse($"Folder created: {folderPath}", "text/plain");
                await ServiceLog($"Folder created: {folderPath} by user {req.user?.Name ?? "unknown"}");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error handling make folder request");
                await req.ReturnResponse("Error creating folder", "text/plain", null, HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleUploadFiles(KliveAPI.KliveAPI.UserRequest req)
        {
            try
            {
                // For now, we'll handle base64 encoded file content in the request body
                // This is a simplified implementation - in production you might want multipart/form-data handling
                
                if (string.IsNullOrEmpty(req.userMessageContent))
                {
                    await req.ReturnResponse("No file data provided", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }

                dynamic uploadData = JsonConvert.DeserializeObject(req.userMessageContent);
                string fileName = uploadData.fileName?.ToString() ?? "";
                string fileContent = uploadData.fileContent?.ToString() ?? "";
                string relativePath = uploadData.path?.ToString() ?? "";

                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(fileContent))
                {
                    await req.ReturnResponse("fileName and fileContent are required", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }

                // Sanitize paths
                fileName = SanitizeFileName(fileName);
                relativePath = SanitizePath(relativePath);

                // Create file metadata
                string fileId = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                byte[] fileBytes = Convert.FromBase64String(fileContent);
                string fileHash = CalculateFileHash(fileBytes);
                
                var metadata = new FileMetadata
                {
                    FileId = fileId,
                    FileName = fileName,
                    RelativePath = relativePath,
                    FileSize = fileBytes.Length,
                    ContentType = GetContentType(fileName),
                    UploadedBy = req.user?.Name ?? "unknown",
                    UploaderUserId = req.user?.UserID ?? "unknown",
                    UploadTime = DateTime.Now,
                    FileHash = fileHash
                };

                // Save file
                string fullFilePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFilesDirectory), relativePath, fileName);
                metadata.FilePath = fullFilePath;
                
                // Ensure directory exists
                string directory = Path.GetDirectoryName(fullFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    await dataHandler.CreateDirectory(directory);
                }

                await dataHandler.WriteBytesToFile(fullFilePath, fileBytes);

                // Save metadata
                string metadataPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudMetadataDirectory), $"{fileId}.json");
                await dataHandler.SerialiseObjectToFile(metadataPath, metadata);

                await req.ReturnResponse(JsonConvert.SerializeObject(new { fileId = fileId, message = "File uploaded successfully" }), "application/json");
                await ServiceLog($"File uploaded: {fileName} by user {req.user?.Name ?? "unknown"}");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error handling upload files request");
                await req.ReturnResponse("Error uploading file", "text/plain", null, HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleDownloadFile(KliveAPI.KliveAPI.UserRequest req)
        {
            try
            {
                string fileId = req.userParameters.Get("fileId") ?? "";
                
                if (string.IsNullOrEmpty(fileId))
                {
                    await req.ReturnResponse("fileId parameter is required", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }

                // Get metadata
                string metadataPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudMetadataDirectory), $"{fileId}.json");
                
                if (!File.Exists(metadataPath))
                {
                    await req.ReturnResponse("File not found", "text/plain", null, HttpStatusCode.NotFound);
                    return;
                }

                var metadata = await dataHandler.ReadAndDeserialiseDataFromFile<FileMetadata>(metadataPath);
                
                if (!File.Exists(metadata.FilePath))
                {
                    await req.ReturnResponse("File data not found", "text/plain", null, HttpStatusCode.NotFound);
                    return;
                }

                // Read and return file
                byte[] fileBytes = await dataHandler.ReadBytesFromFile(metadata.FilePath);
                string base64Content = Convert.ToBase64String(fileBytes);

                var downloadResponse = new
                {
                    fileName = metadata.FileName,
                    contentType = metadata.ContentType,
                    fileSize = metadata.FileSize,
                    fileContent = base64Content
                };

                await req.ReturnResponse(JsonConvert.SerializeObject(downloadResponse), "application/json");
                await ServiceLog($"File downloaded: {metadata.FileName} by user {req.user?.Name ?? "unknown"}");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error handling download file request");
                await req.ReturnResponse("Error downloading file", "text/plain", null, HttpStatusCode.InternalServerError);
            }
        }

        private string SanitizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            
            // Remove any attempts at directory traversal
            path = path.Replace("..", "").Replace("\\", "/");
            
            // Remove leading slash
            path = path.TrimStart('/');
            
            return path;
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            
            // Remove invalid filename characters
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            
            return fileName;
        }

        private string CalculateFileHash(byte[] fileBytes)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(fileBytes);
                return Convert.ToBase64String(hashBytes);
            }
        }

        private string GetContentType(string fileName)
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".pdf" => "application/pdf",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".zip" => "application/zip",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                _ => "application/octet-stream"
            };
        }
    }
}