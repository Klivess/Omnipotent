using Omnipotent.Profiles;
using System;

namespace Omnipotent.Services.KliveCloud
{
    public class FolderPermissions
    {
        public string FolderPath { get; set; } = string.Empty;
        public KMProfileManager.KMPermissions RequiredPermission { get; set; } = KMProfileManager.KMPermissions.Guest;
        public string SetBy { get; set; } = string.Empty;
        public string SetByUserId { get; set; } = string.Empty;
        public DateTime SetTime { get; set; }
    }
}