namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveMailPerms
    {
        private static readonly PermissionGroup G = new("klivemail", "KliveMail");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View mail statistics", "Message and mailbox counts.", ProfileRank.Klives);

        public static readonly PermissionDef MailboxesRead = G.Define("mailboxes.read", "Mailboxes", PermissionTier.Read,
            "View mailboxes", "Every mailbox address.", ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef MessagesRead = G.Define("messages.read", "Messages", PermissionTier.Read,
            "Read mail", "Messages, search and attachments.", ProfileRank.Klives, implies: new[] { "mailboxes.read" },
            sensitive: true);

        public static readonly PermissionDef MessagesAct = G.Define("messages.act", "Messages", PermissionTier.Act,
            "Mark mail read or unread", "Change a message's read state.", ProfileRank.Klives, implies: new[] { "messages.read" });

        public static readonly PermissionDef MessagesDelete = G.Define("messages.delete", "Messages", PermissionTier.Manage,
            "Delete mail", "Delete messages.", ProfileRank.Klives, implies: new[] { "messages.read" });

        public static readonly PermissionDef MailboxesManage = G.Define("mailboxes.manage", "Mailboxes", PermissionTier.Manage,
            "Manage mailboxes", "Create and delete mailboxes.", ProfileRank.Klives, implies: new[] { "mailboxes.read" });
    }
}
