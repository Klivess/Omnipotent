namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveChatPerms
    {
        private static readonly PermissionGroup G = new("klivechat", "KliveChat");

        public static readonly PermissionDef RoomsManage = G.Define("rooms.manage", "Rooms", PermissionTier.Act,
            "Create and delete rooms", "Create chat rooms, and rename, keep, lock, moderate or delete the rooms you created.", ProfileRank.Guest,
            refines: new[] { "/klivechat/ws", "/klivechat/update", "/klivechat/delete" });

        public static readonly PermissionDef RoomsModerate = G.Define("rooms.moderate", "Rooms", PermissionTier.Act,
            "Moderate rooms", "Mute, remove and ban people ranked below you, and lock rooms, in any chat room.", ProfileRank.Associate,
            refines: new[] { "/klivechat/ws" });

        public static readonly PermissionDef RoomsDeleteAny = G.Define("rooms.delete-any", "Rooms", PermissionTier.Manage,
            "Delete any room", "Rename, keep or delete chat rooms other people created.", ProfileRank.Admin,
            implies: new[] { "rooms.manage" }, refines: new[] { "/klivechat/delete", "/klivechat/update", "/klivechat/rooms", "/klivechat/lobby", "/klivechat/ws" });
    }
}
