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
                ServiceLog("KliveCloud service starting...");
                
                // Get required services
                dataHandler = GetDataHandler();
                kliveAPI = await serviceManager.GetKliveAPIService();
                
                // Initialize storage directories
                await InitializeStorageDirectories();
                
                // Setup API routes
                await SetupFileTransferRoutes();
                
                ServiceLog("KliveCloud service started successfully");
                
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
                string folderPermissionsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFolderPermissionsDirectory);
                
                await dataHandler.CreateDirectory(filesDir);
                await dataHandler.CreateDirectory(metadataDir);
                await dataHandler.CreateDirectory(folderPermissionsDir);
                
                ServiceLog("Storage directories initialized");
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
                }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

                // Route to create folder
                await kliveAPI.CreateRoute("klivecloud/makefolder", async (req) =>
                {
                    await HandleMakeFolder(req);
                }, HttpMethod.Post, KMProfileManager.KMPermissions.Guest);

                // Route to upload files
                await kliveAPI.CreateRoute("klivecloud/uploadfiles", async (req) =>
                {
                    await HandleUploadFiles(req);
                }, HttpMethod.Post, KMProfileManager.KMPermissions.Guest);

                // Route to download file
                await kliveAPI.CreateRoute("klivecloud/downloadfile", async (req) =>
                {
                    await HandleDownloadFile(req);
                }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

                // Route to set folder permissions
                await kliveAPI.CreateRoute("klivecloud/setfolderpermissions", async (req) =>
                {
                    await HandleSetFolderPermissions(req);
                }, HttpMethod.Post, KMProfileManager.KMPermissions.Guest);

                ServiceLog("File transfer routes created successfully");
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
                            
                            // Check if user has access to this file's folder
                            if (await CheckFolderAccess(metadata.RelativePath, req.user))
                            {
                                allFiles.Add(metadata);
                            }
                        }
                        catch (Exception ex)
                        {
                            ServiceLog($"Failed to read metadata file {metadataFile}: {ex.Message}");
                        }
                    }
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(allFiles), "application/json");
                ServiceLog($"Listed {allFiles.Count} accessible files for user {req.user?.Name ?? "unknown"}");
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
                
                // Check parent folder access
                string parentPath = Path.GetDirectoryName(folderPath) ?? "";
                if (!await CheckFolderAccess(parentPath, req.user))
                {
                    await req.ReturnResponse("Access denied to parent folder", "text/plain", null, HttpStatusCode.Forbidden);
                    return;
                }
                
                string fullPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFilesDirectory), folderPath);
                
                await dataHandler.CreateDirectory(fullPath);
                
                await req.ReturnResponse($"Folder created: {folderPath}", "text/plain");
                ServiceLog($"Folder created: {folderPath} by user {req.user?.Name ?? "unknown"}");
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
                // Handle base64 encoded file content in the request body
                // This supports all file types (.zip, .png, etc.) through Base64 encoding
                
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

                // Check folder access permission
                if (!await CheckFolderAccess(relativePath, req.user))
                {
                    await req.ReturnResponse("Access denied to target folder", "text/plain", null, HttpStatusCode.Forbidden);
                    return;
                }

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
                ServiceLog($"File uploaded: {fileName} by user {req.user?.Name ?? "unknown"}");
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
                
                // Check folder access permission
                if (!await CheckFolderAccess(metadata.RelativePath, req.user))
                {
                    await req.ReturnResponse("Access denied to file's folder", "text/plain", null, HttpStatusCode.Forbidden);
                    return;
                }
                
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
                ServiceLog($"File downloaded: {metadata.FileName} by user {req.user?.Name ?? "unknown"}");
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
                // Text files
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".csv" => "text/csv",
                ".log" => "text/plain",
                
                // Data formats
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".yaml" or ".yml" => "application/x-yaml",
                
                // Documents
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".odt" => "application/vnd.oasis.opendocument.text",
                ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
                ".odp" => "application/vnd.oasis.opendocument.presentation",
                
                // Images
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".tiff" or ".tif" => "image/tiff",
                ".webp" => "image/webp",
                ".ico" => "image/x-icon",
                
                // Audio
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                ".aac" => "audio/aac",
                ".ogg" => "audio/ogg",
                
                // Video
                ".mp4" => "video/mp4",
                ".avi" => "video/x-msvideo",
                ".mkv" => "video/x-matroska",
                ".mov" => "video/quicktime",
                ".wmv" => "video/x-ms-wmv",
                ".webm" => "video/webm",
                
                // Archives
                ".zip" => "application/zip",
                ".rar" => "application/vnd.rar",
                ".7z" => "application/x-7z-compressed",
                ".tar" => "application/x-tar",
                ".gz" => "application/gzip",
                ".bz2" => "application/x-bzip2",
                
                // Programming
                ".cs" => "text/x-csharp",
                ".js" => "text/javascript",
                ".ts" => "text/typescript",
                ".html" => "text/html",
                ".css" => "text/css",
                ".py" => "text/x-python",
                ".java" => "text/x-java-source",
                ".cpp" or ".cc" => "text/x-c++src",
                ".c" => "text/x-csrc",
                ".h" => "text/x-chdr",
                ".php" => "text/x-php",
                ".rb" => "text/x-ruby",
                ".go" => "text/x-go",
                ".sql" => "text/x-sql",
                
                // Executables
                ".exe" => "application/vnd.microsoft.portable-executable",
                ".msi" => "application/x-msi",
                ".deb" => "application/vnd.debian.binary-package",
                ".rpm" => "application/x-rpm",
                ".dmg" => "application/x-apple-diskimage",
                
                // Fonts
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                
                // Other
                ".iso" => "application/x-iso9660-image",
                ".torrent" => "application/x-bittorrent",
                
                _ => "application/octet-stream"
            };
        }

        private async Task HandleSetFolderPermissions(KliveAPI.KliveAPI.UserRequest req)
        {
            try
            {
                string folderPath = req.userParameters.Get("path") ?? "";
                string permissionLevel = req.userParameters.Get("permission") ?? "";
                
                if (string.IsNullOrEmpty(folderPath))
                {
                    await req.ReturnResponse("Path parameter is required", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }

                if (string.IsNullOrEmpty(permissionLevel) || !int.TryParse(permissionLevel, out int permLevel))
                {
                    await req.ReturnResponse("Valid permission level (0-5) is required", "text/plain", null, HttpStatusCode.BadRequest);
                    return;
                }

                var requestedPermission = (KMProfileManager.KMPermissions)permLevel;
                
                // Users can only set permissions to their level or below
                if (req.user?.KlivesManagementRank < requestedPermission)
                {
                    await req.ReturnResponse("Cannot set permission level higher than your own", "text/plain", null, HttpStatusCode.Forbidden);
                    return;
                }

                // Sanitize path
                folderPath = SanitizePath(folderPath);
                
                var folderPermissions = new FolderPermissions
                {
                    FolderPath = folderPath,
                    RequiredPermission = requestedPermission,
                    SetBy = req.user?.Name ?? "unknown",
                    SetByUserId = req.user?.UserID ?? "unknown",
                    SetTime = DateTime.Now
                };

                // Save folder permissions
                string permissionsPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFolderPermissionsDirectory), 
                    $"{folderPath.Replace("/", "_").Replace("\\", "_")}.json");
                
                await dataHandler.SerialiseObjectToFile(permissionsPath, folderPermissions);

                await req.ReturnResponse($"Folder permissions set: {folderPath} requires {requestedPermission} level", "text/plain");
                ServiceLog($"Folder permissions set for {folderPath} to {requestedPermission} by user {req.user?.Name ?? "unknown"}");
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error handling set folder permissions request");
                await req.ReturnResponse("Error setting folder permissions", "text/plain", null, HttpStatusCode.InternalServerError);
            }
        }

        private async Task<bool> CheckFolderAccess(string folderPath, KMProfileManager.KMProfile? user)
        {
            try
            {
                folderPath = SanitizePath(folderPath);
                string permissionsPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudFolderPermissionsDirectory), 
                    $"{folderPath.Replace("/", "_").Replace("\\", "_")}.json");

                if (!File.Exists(permissionsPath))
                {
                    // No specific permissions set, default to Guest level
                    return (user?.KlivesManagementRank ?? KMProfileManager.KMPermissions.Anybody) >= KMProfileManager.KMPermissions.Guest;
                }

                var folderPermissions = await dataHandler.ReadAndDeserialiseDataFromFile<FolderPermissions>(permissionsPath);
                return (user?.KlivesManagementRank ?? KMProfileManager.KMPermissions.Anybody) >= folderPermissions.RequiredPermission;
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Error checking folder access for {folderPath}");
                // In case of error, allow access if user has Guest level or higher
                return (user?.KlivesManagementRank ?? KMProfileManager.KMPermissions.Anybody) >= KMProfileManager.KMPermissions.Guest;
            }
        }
    }
}