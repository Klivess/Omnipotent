using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Who is asking for a model call. KliveLLM used to know nothing about its callers beyond a
    /// session id, which is why one global switch had to govern every service: there was nothing to
    /// route on and nothing to attribute usage to.
    ///
    /// <see cref="Service"/> is the Omnipotent service, <see cref="Lane"/> one part of it with its own
    /// traffic shape (a Steward's Commander and its one-shot utility calls want different routers),
    /// <see cref="Subject"/> a low-cardinality grouping the caller supplies (a Steward id, a game id),
    /// and <see cref="Operation"/> a free label for the call log.
    /// </summary>
    public sealed record LlmCaller(string Service, string Lane = "default", string? Subject = null, string? Operation = null)
    {
        public string Key => Service + "/" + Lane;

        public LlmCaller For(string? subject, string? operation = null)
            => this with { Subject = string.IsNullOrWhiteSpace(subject) ? Subject : subject, Operation = operation ?? Operation };

        public override string ToString() => Subject == null ? Key : Key + "#" + Subject;
    }

    /// <summary>How far a service's traffic may go onto routers that bill per token.</summary>
    public enum LlmSpending
    {
        /// <summary>Free and flat-fee routers only, as the primary and as any fallback.</summary>
        Never = 0,
        /// <summary>The assigned router may bill per token, but an automatic fallback never moves the
        /// service from a free or flat-fee router onto a per-token one.</summary>
        PrimaryOnly = 1,
        /// <summary>Fallback may cross onto per-token routers.</summary>
        Anywhere = 2,
    }

    /// <summary>What the routing layer and the KliveLLM page need to know about one lane.</summary>
    public sealed record LlmLaneDescriptor(
        string Service,
        string Lane,
        string Label,
        string Description,
        AIRouterWorkClass WorkClass,
        bool NeedsToolCalling = false,
        LlmSpending DefaultSpending = LlmSpending.PrimaryOnly,
        // Where this lane's own model choice lives, when it has one.
        string? ModelSource = null,
        // A page on the KM website where that choice is edited.
        string? ModelSourceLink = null,
        // Set when the lane cannot be routed because its endpoint is fixed (AIRouter speech).
        string? FixedRouter = null)
    {
        public string Key => Service + "/" + Lane;
    }

    /// <summary>
    /// Every lane that calls KliveLLM, and the rule that names the caller of a call site that did not
    /// say who it is.
    ///
    /// The fallback is the compiler: <c>[CallerFilePath]</c> on KliveLLM's entry points supplies the
    /// source file of the call, and <see cref="FromCallSite"/> maps its folder to a service and lane.
    /// So a call site added tomorrow is attributed to its own service without anyone remembering to
    /// pass a caller, and services whose files this change must not touch are attributed all the same.
    /// Sites that serve several lanes from one file, or that have a subject worth recording, pass an
    /// explicit <see cref="LlmCaller"/>.
    /// </summary>
    public static class LlmConsumerCatalog
    {
        public static class Callers
        {
            public static readonly LlmCaller KliveAgentChat = new("KliveAgent", "chat");
            public static readonly LlmCaller KliveAgentTalk = new("KliveAgent", "talk");
            public static readonly LlmCaller KliveAgentSpeech = new("KliveAgent", "speech");
            public static readonly LlmCaller DemiurgeCommander = new("Demiurge", "commander");
            public static readonly LlmCaller DemiurgeSubAgent = new("Demiurge", "subagent");
            public static readonly LlmCaller DemiurgeFrontDesk = new("Demiurge", "frontdesk");
            public static readonly LlmCaller DemiurgeCouncil = new("Demiurge", "council");
            public static readonly LlmCaller DemiurgeUtility = new("Demiurge", "utility");
            public static readonly LlmCaller OmniBloxAgents = new("OmniBlox", "agents");
            public static readonly LlmCaller OmniBloxLabeller = new("OmniBlox", "labeller");
            public static readonly LlmCaller StratumEngineer = new("Stratum", "engineer");
            public static readonly LlmCaller StratumSummary = new("Stratum", "summary");
            public static readonly LlmCaller OmniTumblrCaptions = new("OmniTumblr", "captions");
            public static readonly LlmCaller OmniGramCaptions = new("OmniGram", "captions");
            public static readonly LlmCaller KliveBotChat = new("KliveBot", "discord-chat");
            public static readonly LlmCaller OmniscienceV3Background = new("OmniscienceV3", "background");
            public static readonly LlmCaller OmniscienceV3Interactive = new("OmniscienceV3", "interactive");
            public static readonly LlmCaller Internal = new("KliveLLM", "internal");
        }

        /// <summary>
        /// Services that were renamed, old → new. The ledger moves their history across once
        /// (LlmTelemetryDb version 2) and the routing document re-keys their policies, budgets and
        /// capture windows as it loads, so a rename never resets a budget part-way through a month.
        /// </summary>
        public static readonly IReadOnlyList<(string Old, string New)> RenamedServices = new[]
        {
            ("Projects", "Demiurge"), // November 2026
        };

        /// <summary>A routing key ("Service" or "Service/lane") under its service's current name.</summary>
        public static string CurrentKey(string key)
        {
            foreach (var (oldName, newName) in RenamedServices)
            {
                if (key.Equals(oldName, StringComparison.OrdinalIgnoreCase)) return newName;
                if (key.StartsWith(oldName + "/", StringComparison.OrdinalIgnoreCase)) return newName + key[oldName.Length..];
            }
            return key;
        }

        private const string StewardModels = "Each Steward's own model routes (Steward, Settings)";

        /// <summary>Catalogued lanes, in the order the page lists them.</summary>
        public static readonly IReadOnlyList<LlmLaneDescriptor> Lanes = new List<LlmLaneDescriptor>
        {
            new("KliveAgent", "chat", "Chat", "Conversations with KliveAgent, including its tool and computer-use loop.",
                AIRouterWorkClass.Interactive, ModelSource: "KliveAgent_FastModel and KliveAgent_ReasoningModel", ModelSourceLink: "/administration/omnisettings"),
            new("KliveAgent", "talk", "Talk mode", "Spoken conversations: one pinned model on an append-only session.",
                AIRouterWorkClass.Interactive, NeedsToolCalling: true, ModelSource: "KliveAgent_TalkModel", ModelSourceLink: "/administration/omnisettings"),
            new("KliveAgent", "speech", "Speech", "Speech to text and text to speech for talk mode.",
                AIRouterWorkClass.Audio, FixedRouter: "airouter"),

            new("Demiurge", "commander", "Commander", "Each Steward's Commander wake loop.",
                AIRouterWorkClass.Agent, NeedsToolCalling: true, ModelSource: StewardModels, ModelSourceLink: "/demiurge"),
            new("Demiurge", "subagent", "Sub-agents", "Worker agents inside a Steward.",
                AIRouterWorkClass.Agent, NeedsToolCalling: true, ModelSource: StewardModels, ModelSourceLink: "/demiurge"),
            new("Demiurge", "council", "Councils", "Adversarial council turns.",
                AIRouterWorkClass.Burst, ModelSource: StewardModels, ModelSourceLink: "/demiurge"),
            new("Demiurge", "frontdesk", "Front desk", "Instant replies while a wake is busy.",
                AIRouterWorkClass.Interactive, ModelSource: StewardModels, ModelSourceLink: "/demiurge"),
            new("Demiurge", "utility", "Utility", "One-shot digests, triage and summaries.",
                AIRouterWorkClass.OneShot, ModelSource: StewardModels, ModelSourceLink: "/demiurge"),

            new("OmniBlox", "agents", "Studio agents", "The Roblox studio's design, build and test agents.",
                AIRouterWorkClass.Agent, NeedsToolCalling: true, DefaultSpending: LlmSpending.Never),
            new("OmniBlox", "labeller", "Asset labeller", "Background labelling of catalogue assets.",
                AIRouterWorkClass.OneShot, DefaultSpending: LlmSpending.Never),

            new("Omniscience", "profiling", "Profiling", "Personality dossiers, biographies and changelogs.",
                AIRouterWorkClass.OneShot, DefaultSpending: LlmSpending.Never),
            new("Omniscience", "extraction", "Extraction", "Fact extraction from message windows.",
                AIRouterWorkClass.OneShot, DefaultSpending: LlmSpending.Never),
            new("Omniscience", "detective", "Detective", "Cross-fact deduction passes.",
                AIRouterWorkClass.OneShot, DefaultSpending: LlmSpending.Never),
            new("Omniscience", "search", "Search answers", "Answers written over search evidence.",
                AIRouterWorkClass.OneShot, DefaultSpending: LlmSpending.Never),
            new("Omniscience", "replica-train", "Replica training", "Building a person's replica.",
                AIRouterWorkClass.OneShot),
            new("Omniscience", "replica-chat", "Replica chat", "Replica replies, their polish pass and chat memory.",
                AIRouterWorkClass.OneShot),
            new("Omniscience", "other", "Other", "Omniscience calls from anywhere else.",
                AIRouterWorkClass.OneShot),

            // The rewrite, which runs beside Omniscience until cutover. Neither lane spends per token unless that is
            // changed on the KliveLLM page: the engine reads a whole corpus on its own, and nobody has priced that yet.
            new("OmniscienceV3", "background", "Background", "What the v3 engine does on its own: extraction, deduction, digests, captions, translation.",
                AIRouterWorkClass.Background, DefaultSpending: LlmSpending.Never),
            new("OmniscienceV3", "interactive", "Interactive", "v3 answers someone is waiting for: questions, briefs on request, replica replies.",
                AIRouterWorkClass.Interactive, DefaultSpending: LlmSpending.Never),

            new("Stratum", "engineer", "Engineer", "The Stratum Engineer's tool loop.",
                AIRouterWorkClass.Agent, NeedsToolCalling: true, ModelSource: "StratumEngineerModelID", ModelSourceLink: "/administration/omnisettings"),
            new("Stratum", "summary", "Summaries", "Turn summaries.",
                AIRouterWorkClass.OneShot, ModelSource: "StratumUtilityModelID", ModelSourceLink: "/administration/omnisettings"),

            new("OmniTumblr", "captions", "Captions", "AI captions and tags for Tumblr posts.",
                AIRouterWorkClass.OneShot, ModelSource: "Each blog's caption model", ModelSourceLink: "/schemery/omnitumblr"),
            new("OmniGram", "captions", "Captions", "AI captions for Instagram posts.",
                AIRouterWorkClass.OneShot, ModelSource: "Each account's caption model", ModelSourceLink: "/schemery/omnigram"),
            new("KliveBot", "discord-chat", "Discord chat", "KliveBot answering in Discord.",
                AIRouterWorkClass.Interactive),
        };

        private static readonly Dictionary<string, LlmLaneDescriptor> byKey =
            Lanes.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);

        /// <summary>Call-site rules, most specific first. Paths are lower-case with forward slashes.</summary>
        private static readonly (string Fragment, LlmCaller Caller)[] PathRules =
        {
            ("/services/kliveagent/talk/", Callers.KliveAgentTalk),
            ("/services/kliveagent/", Callers.KliveAgentChat),
            ("/services/demiurge/stewardcommander", Callers.DemiurgeCommander),
            ("/services/demiurge/stewardsubagent", Callers.DemiurgeSubAgent),
            ("/services/demiurge/stewardfrontdesk", Callers.DemiurgeFrontDesk),
            ("/services/demiurge/", Callers.DemiurgeUtility),
            ("/services/omniblox/assets/", Callers.OmniBloxLabeller),
            ("/services/omniblox/", Callers.OmniBloxAgents),
            // v3's calls name their own lane (the gateway passes it); this is the fallback for a call that does not.
            ("/services/omnisciencev3/", Callers.OmniscienceV3Background),
            ("/services/omniscience/profiling/", new LlmCaller("Omniscience", "profiling")),
            ("/services/omniscience/deduction/detective", new LlmCaller("Omniscience", "detective")),
            ("/services/omniscience/deduction/", new LlmCaller("Omniscience", "extraction")),
            ("/services/omniscience/search/", new LlmCaller("Omniscience", "search")),
            ("/services/omniscience/replica/replicatrainer", new LlmCaller("Omniscience", "replica-train")),
            ("/services/omniscience/replica/", new LlmCaller("Omniscience", "replica-chat")),
            ("/services/omniscience/", new LlmCaller("Omniscience", "other")),
            ("/services/stratum/", Callers.StratumEngineer),
            ("/services/omnitumblr/", Callers.OmniTumblrCaptions),
            ("/services/omnigram/", Callers.OmniGramCaptions),
            ("/services/klivebot discord/", Callers.KliveBotChat),
            ("/services/klivellm/", Callers.Internal),
        };

        private static readonly ConcurrentDictionary<string, LlmCaller> callSiteCache = new(StringComparer.Ordinal);

        public static LlmLaneDescriptor? Find(string? service, string? lane)
            => byKey.TryGetValue((service ?? "") + "/" + (lane ?? ""), out var found) ? found : null;

        public static LlmLaneDescriptor? Find(LlmCaller? caller) => caller == null ? null : Find(caller.Service, caller.Lane);

        public static IEnumerable<string> Services => Lanes.Select(l => l.Service).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The caller to use: the explicit one, else the one the call site implies.</summary>
        public static LlmCaller Resolve(LlmCaller? explicitCaller, string? callerFile)
            => explicitCaller != null && !string.IsNullOrWhiteSpace(explicitCaller.Service)
                ? explicitCaller
                : FromCallSite(callerFile);

        /// <summary>
        /// Names the caller from the source file of the call. An unlisted folder under Services becomes
        /// its own service with a "default" lane, so a new service shows up on the page by name rather
        /// than as "unknown".
        /// </summary>
        public static LlmCaller FromCallSite(string? callerFile)
        {
            if (string.IsNullOrWhiteSpace(callerFile)) return new LlmCaller("Other", "default");
            return callSiteCache.GetOrAdd(callerFile, static file =>
            {
                string path = file.Replace('\\', '/').ToLowerInvariant();
                foreach (var (fragment, caller) in PathRules)
                    if (path.Contains(fragment, StringComparison.Ordinal)) return caller;

                if (path.Contains("omnipotent.tests/", StringComparison.Ordinal)) return new LlmCaller("Tests", "default");

                const string marker = "/services/";
                int at = path.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    // Keep the folder's own casing for the name shown on the page.
                    string original = file.Replace('\\', '/');
                    int start = at + marker.Length;
                    int end = original.IndexOf('/', start);
                    if (end > start) return new LlmCaller(original[start..end].Replace(" ", ""), "default");
                }
                return new LlmCaller("Other", "default");
            });
        }

        /// <summary>The work class a lane's calls compete with when the caller did not state one.</summary>
        public static AIRouterWorkClass? DefaultWorkClass(LlmCaller? caller) => Find(caller)?.WorkClass;
    }
}
