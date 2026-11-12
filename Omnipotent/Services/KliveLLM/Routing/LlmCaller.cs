using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// Who is asking for a model call. KliveLLM used to know nothing about its callers beyond a
    /// session id, which is why one global switch had to govern every service: there was nothing to
    /// route on and nothing to attribute usage to.
    ///
    /// <see cref="Service"/> is the Omnipotent service, <see cref="Lane"/> one part of it with its own
    /// traffic shape (a Project's Commander and its one-shot utility calls want different routers),
    /// <see cref="Subject"/> a low-cardinality grouping the caller supplies (a project id, a game id),
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
            public static readonly LlmCaller ProjectsCommander = new("Projects", "commander");
            public static readonly LlmCaller ProjectsSubAgent = new("Projects", "subagent");
            public static readonly LlmCaller ProjectsFrontDesk = new("Projects", "frontdesk");
            public static readonly LlmCaller ProjectsCouncil = new("Projects", "council");
            public static readonly LlmCaller ProjectsUtility = new("Projects", "utility");
            public static readonly LlmCaller OmniBloxAgents = new("OmniBlox", "agents");
            public static readonly LlmCaller OmniBloxLabeller = new("OmniBlox", "labeller");
            public static readonly LlmCaller StratumEngineer = new("Stratum", "engineer");
            public static readonly LlmCaller StratumSummary = new("Stratum", "summary");
            public static readonly LlmCaller OmniTumblrCaptions = new("OmniTumblr", "captions");
            public static readonly LlmCaller OmniGramCaptions = new("OmniGram", "captions");
            public static readonly LlmCaller KliveBotChat = new("KliveBot", "discord-chat");
            public static readonly LlmCaller Internal = new("KliveLLM", "internal");
        }

        private const string ProjectModels = "Each project's own model routes (Project, Settings)";

        /// <summary>Catalogued lanes, in the order the page lists them.</summary>
        public static readonly IReadOnlyList<LlmLaneDescriptor> Lanes = new List<LlmLaneDescriptor>
        {
            new("KliveAgent", "chat", "Chat", "Conversations with KliveAgent, including its tool and computer-use loop.",
                AIRouterWorkClass.Interactive, ModelSource: "KliveAgent_FastModel and KliveAgent_ReasoningModel", ModelSourceLink: "/administration/omnisettings"),
            new("KliveAgent", "talk", "Talk mode", "Spoken conversations: one pinned model on an append-only session.",
                AIRouterWorkClass.Interactive, NeedsToolCalling: true, ModelSource: "KliveAgent_TalkModel", ModelSourceLink: "/administration/omnisettings"),
            new("KliveAgent", "speech", "Speech", "Speech to text and text to speech for talk mode.",
                AIRouterWorkClass.Audio, FixedRouter: "airouter"),

            new("Projects", "commander", "Commander", "Each project's Commander wake loop.",
                AIRouterWorkClass.Agent, NeedsToolCalling: true, ModelSource: ProjectModels, ModelSourceLink: "/projects"),
            new("Projects", "subagent", "Sub-agents", "Worker agents inside a project.",
                AIRouterWorkClass.Agent, NeedsToolCalling: true, ModelSource: ProjectModels, ModelSourceLink: "/projects"),
            new("Projects", "council", "Councils", "Adversarial council turns.",
                AIRouterWorkClass.Burst, ModelSource: ProjectModels, ModelSourceLink: "/projects"),
            new("Projects", "frontdesk", "Front desk", "Instant replies while a wake is busy.",
                AIRouterWorkClass.Interactive, ModelSource: ProjectModels, ModelSourceLink: "/projects"),
            new("Projects", "utility", "Utility", "One-shot digests, triage and summaries.",
                AIRouterWorkClass.OneShot, ModelSource: ProjectModels, ModelSourceLink: "/projects"),

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
            ("/services/projects/projectcommander", Callers.ProjectsCommander),
            ("/services/projects/projectsubagent", Callers.ProjectsSubAgent),
            ("/services/projects/projectfrontdesk", Callers.ProjectsFrontDesk),
            ("/services/projects/", Callers.ProjectsUtility),
            ("/services/omniblox/assets/", Callers.OmniBloxLabeller),
            ("/services/omniblox/", Callers.OmniBloxAgents),
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
