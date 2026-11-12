using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Conversation context kinds the replica distinguishes (style differs between DMs, group chats and servers).</summary>
    public static class ContextKinds
    {
        public const string Dm = "dm";
        public const string GroupDm = "group_dm";
        public const string Guild = "guild_channel";
        public const string Other = "other";
        public const string Auto = "auto";

        public static readonly IReadOnlyList<string> All = new[] { Dm, GroupDm, Guild, Other };

        /// <summary>Maps a corpus conversation kind (platform-specific) onto a replica context kind.</summary>
        public static string FromConversationKind(string? kind)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k is "dm" or "direct" or "private" or "personal_chat" or "one_to_one") return Dm;
            if (k.Contains("group", StringComparison.Ordinal)) return GroupDm;
            if (k.StartsWith("guild", StringComparison.Ordinal) || k.Contains("thread", StringComparison.Ordinal) || k.Contains("forum", StringComparison.Ordinal)
                || k.Contains("channel", StringComparison.Ordinal) || k == "server") return Guild;
            if (k.EndsWith("dm", StringComparison.Ordinal)) return Dm;
            return Other;
        }

        /// <summary>A requested context (v2 also used "server"/"guild" and "auto").</summary>
        public static string Normalize(string? requested)
        {
            string v = (requested ?? Auto).Trim().ToLowerInvariant();
            return v switch
            {
                Dm or GroupDm or Guild or Other => v,
                "server" or "guild" => Guild,
                "group" => GroupDm,
                _ => Auto,
            };
        }
    }

    /// <summary>Which purpose a pair serves in a version. Holdout pairs (and their shadows) are invisible to every
    /// prompt-visible source: exemplars, style, persona, reflexes (F321/F322).</summary>
    public static class PairSplits
    {
        public const string Train = "train";
        public const string Tune = "holdout_tune";
        public const string Test = "holdout_test";
        /// <summary>Not held out, but excluded with the holdout: a near-duplicate of a held-out reply, or a pair whose
        /// context shows a held-out reply (it would leak it).</summary>
        public const string Shadow = "shadow";

        public static bool IsHoldout(string split) => split is Tune or Test;
        public static bool IsVisible(string split) => split == Train;
    }

    [Flags]
    public enum PairFlags
    {
        None = 0,
        Link = 1,
        Attachment = 2,
        Code = 4,
        Edited = 8,
        Sticker = 16,
        LinkOnly = 32,
        MediaOnly = 64,
        DayPrecision = 128,
        CustomEmoji = 256,
    }

    /// <summary>One stimulus → reply pair (or a standalone initiation) of a replica version (plan "Index").</summary>
    public sealed record ReplicaPair
    {
        public required string PairId { get; init; }
        public required string PersonId { get; init; }
        public required string ConversationId { get; init; }
        public string? GuildId { get; init; }
        public required string Platform { get; init; }
        public required string ContextKind { get; init; }
        public string? PartnerPersonId { get; init; }
        public bool PartnerIsSelf { get; init; }
        /// <summary>How the addressee was determined: dm, reply, mention, adjacency, none.</summary>
        public required string AddresseeSource { get; init; }
        public bool IsInitiation { get; init; }
        public bool ExplicitReply { get; init; }
        public long ReplyAtMs { get; init; }
        public long ReplyEndMs { get; init; }
        public long? StimulusAtMs { get; init; }
        public long? LatencyMs { get; init; }
        public required IReadOnlyList<string> ReplyIds { get; init; }
        public required IReadOnlyList<string> StimulusIds { get; init; }
        public required IReadOnlyList<string> ContextIds { get; init; }
        /// <summary>The person's burst as sent (version 1 of each message), one message per line.</summary>
        public required string ReplyText { get; init; }
        /// <summary>What others said that the person answered (no names), for similarity.</summary>
        public required string StimulusPlain { get; init; }
        /// <summary>The exchange rendered for prompts (names, placeholders, the person's lines last).</summary>
        public required string Rendered { get; init; }
        public required string TextHash { get; init; }
        public required string StimulusHash { get; init; }
        public int TimeQuality { get; init; }
        public PairFlags Flags { get; init; }
        public string Split { get; init; } = PairSplits.Train;
        public int EraIndex { get; init; }
        public int SliceIndex { get; init; }
        public string? ReflexClass { get; init; }
        public long SourceSeq { get; init; }

        public int ReplyCount => ReplyIds.Count;
        public IReadOnlyList<string> ReplyLines => ReplyText.Split('\n');
    }

    /// <summary>JSON used for replica rows (kernel conventions: camelCase, enums by name).</summary>
    internal static class RJson
    {
        public static string Write<T>(T value) => JsonSerializer.Serialize(value, KernelJson.Options);

        public static T? Read<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            try { return JsonSerializer.Deserialize<T>(json, KernelJson.Options); }
            catch (JsonException) { return default; }
        }

        public static IReadOnlyList<string> Ids(string? json) => Read<List<string>>(json) ?? new List<string>();
    }

    /// <summary>Prompt versions of every replica LLM purpose (recorded on every call and every derived row).</summary>
    public static class ReplicaPrompts
    {
        public const string Compose = "replica.compose/v1";
        public const string Judge = "replica.judge/v1";
        public const string Discriminate = "replica.discriminate/v1";
        public const string Persona = "replica.persona/v1";
        public const string Reflex = "replica.reflex/v1";
        public const string AssistantTeacher = "replica.assistant-teacher/v1";
        public const string ChatSummary = "replica.chat-summary/v1";
        public const string Rehearsal = "replica.rehearsal/v1";
        public const string Social = "replica.social/v1";
    }
}
