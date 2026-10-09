namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class AccountsPerms
    {
        private static readonly PermissionGroup G = new("accounts", "Account Registry");

        public static readonly PermissionDef RegistryRead = G.Define("registry.read", "Accounts", PermissionTier.Read,
            "View agent accounts", "Accounts agents created on external sites (secrets stay masked).", ProfileRank.Klives,
            sensitive: true);

        public static readonly PermissionDef SecretsReveal = G.Define("secrets.reveal", "Accounts", PermissionTier.Critical,
            "Reveal account secrets", "Show the decrypted passwords and tokens of agent accounts.", ProfileRank.Klives,
            implies: new[] { "registry.read" }, refines: new[] { "/accounts/list" }, sensitive: true);

        public static readonly PermissionDef RegistryManage = G.Define("registry.manage", "Accounts", PermissionTier.Critical,
            "Edit agent accounts", "Update and delete accounts in the registry.", ProfileRank.Klives,
            implies: new[] { "registry.read" }, sensitive: true);
    }
}
