using System;

namespace Omnipotent.Services.KliveCloud
{
    public class FileMetadata
    {
        public string FileId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public string UploadedBy { get; set; } = string.Empty;
        public string UploaderUserId { get; set; } = string.Empty;
        public DateTime UploadTime { get; set; }
        public string FileHash { get; set; } = string.Empty;
    }
}