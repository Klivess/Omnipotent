namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveChatPerms
    {
        private static readonly PermissionGroup G = new("klivechat", "KliveChat");

        public static readonly PermissionDef RoomsManage = G.Define("rooms.manage", "Rooms", PermissionTier.Act,
            "Create and delete rooms", "Create chat rooms and delete rooms you created.", ProfileRank.Guest);

        public static readonly PermissionDef RoomsModerate = G.Define("rooms.moderate", "Rooms", PermissionTier.Act,
            "Moderate rooms", "Mute and remove people ranked below you in chat rooms.", ProfileRank.Associate,
            refines: new[] { "/klivechat/ws" });

        public static readonly PermissionDef RoomsDeleteAny = G.Define("rooms.delete-any", "Rooms", PermissionTier.Manage,
            "Delete any room", "Delete chat rooms other people created.", ProfileRank.Admin,
            implies: new[] { "rooms.manage" }, refines: new[] { "/klivechat/delete" });
    }
}
