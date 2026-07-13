using System.Text;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveMail.Persistence;

namespace Omnipotent.Services.AccountRegistry
{
    /// <summary>
    /// A single GLOBAL account registry shared by KliveAgent and every Project. When an agent
    /// creates an account on an external service (GitHub, a mail relay, an API provider…) it records
    /// it here — service, username, email, description, owners, timestamps, and the encrypted
    /// secrets — so agents on other projects reuse it instead of creating redundant duplicates.
    ///
    /// Cross-system access mirrors KliveRAG: consumers resolve this service at call time via
    /// GetActiveServices().OfType&lt;AccountRegistry&gt;() and fail soft if it isn't running.
    ///
    /// Secrets are never returned to a model. Agents reference them as {account:service/field}
    /// placeholders that are decrypted host-side only at typing time.
    /// </summary>
    public class AccountRegistry : OmniService
    {
        public AccountRegistryStore Store { get; private set; } = null!;

        public AccountRegistry()
        {
            name = "AccountRegistry";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                Store = new AccountRegistryStore(msg => _ = ServiceLog(msg));
                await new AccountRegistryRoutes(this).RegisterRoutes();
                await ServiceLog("AccountRegistry service started (global shared account registry).");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "AccountRegistry startup failed");
            }
        }

        // ── façade: registration + dedup ──

        public sealed record AccountRegistrationOutcome(
            bool Created,
            bool Failed,
            RegisteredAccount? Account,
            string Message,
            string? ErrorCode = null);

        /// <summary>
        /// Registers an account (warn-but-allow dedup). Returns an agent-readable result string. When
        /// the service already has account(s) and <paramref name="allowDuplicate"/> is false, creates
        /// nothing and returns the existing account(s) plus the instruction to re-call with
        /// allowDuplicate + a reason. Ensures a KliveMail inbox when the email is @klive.dev.
        /// </summary>
        public async Task<string> RegisterAccountAsync(string service, string username, string? email,
            Dictionary<string, string>? secrets, string? description, string createdBy, string? owner,
            bool allowDuplicate, string? reason)
            => (await RegisterAccountDetailedAsync(service, username, email, secrets, description,
                createdBy, owner, allowDuplicate, reason)).Message;

        /// <summary>Typed registration result so callers never infer a mutation from a prose string.</summary>
        public async Task<AccountRegistrationOutcome> RegisterAccountDetailedAsync(string service, string username, string? email,
            Dictionary<string, string>? secrets, string? description, string createdBy, string? owner,
            bool allowDuplicate, string? reason)
        {
            AccountRegistryStore.RegisterResult result;
            try
            {
                result = Store.Register(service, username, email, secrets, description, createdBy, owner, allowDuplicate, reason);
            }
            catch (Exception ex)
            {
                return new AccountRegistrationOutcome(false, true, null,
                    $"Could not register account: {ex.Message}", ex.GetType().Name);
            }

            if (!result.Created)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"An account for '{AccountRegistryStore.NormalizeService(service)}' already exists — reuse it instead of creating a duplicate:");
                foreach (var a in result.Existing) sb.AppendLine("  " + FormatAccountLine(a));
                sb.Append("If you genuinely need a SEPARATE account, call account_register again with allowDuplicate=true and a reason.");
                return new AccountRegistrationOutcome(false, false, null, sb.ToString(), "DuplicatePrevented");
            }

            string? mailboxWarning = null;
            try { await EnsureMailboxForAccountAsync(result.Account!); }
            catch (Exception ex) { mailboxWarning = ex.Message; }
            var confirm = new StringBuilder();
            confirm.AppendLine($"Registered account: {FormatAccountLine(result.Account!)}");
            if (IsKliveMailAddress(result.Account!.Email))
                confirm.AppendLine(mailboxWarning == null
                    ? $"KliveMail inbox ensured for {result.Account!.Email} — verification/reset mail will arrive there."
                    : $"Account was registered, but its KliveMail inbox could not be ensured: {mailboxWarning}");
            confirm.Append("Reference its secrets when typing as shown above; they are never shown back to you.");
            return new AccountRegistrationOutcome(true, false, result.Account, confirm.ToString(),
                mailboxWarning == null ? null : "MailboxEnsureFailed");
        }

        // ── façade: listing / description ──

        /// <summary>Full metadata listing (never secret values), optionally filtered by owner/service,
        /// including a trailing section of KliveMail inboxes not linked to any registered account.</summary>
        public async Task<string> DescribeAccountsAsync(string? owner, string? service)
        {
            var accounts = Store.List(service);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                // Owner's accounts first, then the rest (shared registry — all are visible).
                accounts = accounts
                    .OrderByDescending(a => a.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                    .ThenByDescending(a => a.UpdatedAt)
                    .ToList();
            }

            var sb = new StringBuilder();
            if (accounts.Count == 0) sb.AppendLine("No accounts registered yet.");
            else foreach (var a in accounts) sb.AppendLine(FormatAccountLine(a));

            var unlinked = await UnlinkedMailboxesAsync(accounts);
            if (unlinked.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("KliveMail inboxes not linked to any registered account (reuse before inventing new addresses):");
                foreach (var m in unlinked) sb.AppendLine($"  {m.Address}{(string.IsNullOrWhiteSpace(m.DisplayName) ? "" : $" — {m.DisplayName}")}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Budget-fitted, fail-soft block for wake seeds / system prompts (accounts only — KliveMail
        /// enrichment is omitted to keep it cheap). Owner's accounts first. Returns "" on any error.
        /// </summary>
        public string DescribeForPrompt(string? owner, int maxTokens)
        {
            try
            {
                var accounts = Store.List();
                if (accounts.Count == 0) return "";
                if (!string.IsNullOrWhiteSpace(owner))
                    accounts = accounts
                        .OrderByDescending(a => a.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase))
                        .ThenByDescending(a => a.UpdatedAt)
                        .ToList();
                var text = string.Join("\n", accounts.Select(FormatAccountLine));
                return TruncateToTokens(text, maxTokens);
            }
            catch { return ""; }
        }

        // ── façade: passthrough mutations ──

        public bool ClaimForOwner(string accountID, string owner) => Store.AddOwner(accountID, owner);
        public bool TouchUsed(string accountID) => Store.TouchUsed(accountID);
        public bool UpdateStatus(string accountID, AccountStatus status) => Store.UpdateStatus(accountID, status);
        public bool UpdateNotes(string accountID, string? notes) => Store.UpdateNotes(accountID, notes);
        public bool AddSecret(string accountID, string name, string value) => Store.SetSecret(accountID, name, value);
        public bool Delete(string accountID) => Store.Delete(accountID);
        public RegisteredAccount? Get(string accountID) => Store.Get(accountID);
        public List<RegisteredAccount> List(string? serviceKey = null) => Store.List(serviceKey);

        // ── façade: placeholder resolution (host-side secret injection) ──

        public string ResolveAccountPlaceholders(string text, string? usingOwner)
            => Store.ResolveAccountPlaceholders(text, usingOwner);

        public AccountRegistryStore.ResolveResult TryResolveForTyping(string text, string? usingOwner)
            => Store.TryResolveForTyping(text, usingOwner);

        // ── KliveMail linkage ──

        private KliveMail.KliveMail? GetKliveMail()
            => GetActiveServices().OfType<KliveMail.KliveMail>().FirstOrDefault(s => s.IsServiceActive());

        private static bool IsKliveMailAddress(string? email)
            => !string.IsNullOrWhiteSpace(email) && email.EndsWith("@" + KliveMailRepository.MailDomain, StringComparison.OrdinalIgnoreCase);

        /// <summary>Creates the KliveMail mailbox for an account's @klive.dev email so it shows named
        /// in the mail client (instead of an anonymous catch-all). Idempotent; no-op if KliveMail is down.</summary>
        public async Task EnsureMailboxForAccountAsync(RegisteredAccount account)
        {
            if (!IsKliveMailAddress(account.Email)) return;
            try
            {
                var mail = GetKliveMail();
                if (mail?.Repo == null) return;
                await mail.Repo.CreateMailboxAsync(account.Email!, $"{account.ServiceDisplay} — {account.Username}");
            }
            catch (Exception ex) { _ = ServiceLog($"AccountRegistry: could not ensure KliveMail mailbox ({ex.Message})."); }
        }

        private async Task<List<Omnipotent.Services.KliveMail.Models.MailboxInfo>> UnlinkedMailboxesAsync(List<RegisteredAccount> accounts)
        {
            try
            {
                var mail = GetKliveMail();
                if (mail?.Repo == null) return new();
                var linked = accounts
                    .Where(a => IsKliveMailAddress(a.Email))
                    .Select(a => a.Email!.ToLowerInvariant())
                    .ToHashSet();
                var boxes = await mail.Repo.ListMailboxesAsync();
                return boxes.Where(b => !linked.Contains(b.Address.ToLowerInvariant())).ToList();
            }
            catch { return new(); }
        }

        // ── formatting ──

        /// <summary>One compact metadata line (never a secret value): the agent-facing view of an account.</summary>
        public static string FormatAccountLine(RegisteredAccount a)
        {
            var sb = new StringBuilder();
            sb.Append(a.ServiceKey).Append(" · ").Append(a.Username);
            if (!string.IsNullOrWhiteSpace(a.Email))
                sb.Append(" (").Append(a.Email).Append(IsKliveMailAddress(a.Email) ? " — KliveMail inbox" : "").Append(')');
            sb.Append(" [").Append(a.Status).Append(']');
            if (!string.IsNullOrWhiteSpace(a.Description)) sb.Append(" — ").Append(a.Description);
            if (a.Secrets.Count > 0)
                sb.Append(" · secrets: ").Append(string.Join(", ", a.Secrets.Select(s => s.Name)))
                  .Append(" → ").Append($"{{account:{a.ServiceKey}/{a.Username}/{a.Secrets[0].Name}}}");
            if (a.Owners.Count > 0) sb.Append(" · owners: ").Append(string.Join(", ", a.Owners));
            if (a.LastUsedAt != null) sb.Append(" · last used ").Append(a.LastUsedAt.Value.ToString("yyyy-MM-dd"));
            return sb.ToString();
        }

        /// <summary>Rough char-budget truncation (~4 chars/token), whole lines only.</summary>
        private static string TruncateToTokens(string text, int maxTokens)
        {
            int maxChars = Math.Max(0, maxTokens) * 4;
            if (text.Length <= maxChars) return text;
            var sb = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (sb.Length + line.Length + 1 > maxChars) { sb.Append("… (more accounts — account_list for all)"); break; }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            return sb.ToString();
        }
    }
}
