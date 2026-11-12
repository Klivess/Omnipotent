using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// Readiness from data sufficiency and MinorGuard, with the numbers (plan "Readiness", F015/F286): it never depends on
    /// tiers or a profile-target allow-list. ≥ ready-threshold replies is Ready, ≥ experimental-threshold is Experimental,
    /// fewer is Not possible; a person with few replies but many initiations (monologue) falls back to counting
    /// initiations. A (likely) minor is Blocked, a bot account Not possible. Klives's own replica is allowed (plan R5).
    /// </summary>
    public static class Readiness
    {
        public static ReplicaReadinessDto From(PersonScope person, IndexStats stats, AgeBand band, long? adultFromMs, int experimental, int ready, long nowMs, long sourceSeq)
        {
            int replies = stats.Replies, initiations = stats.Initiations;
            bool monologue = replies < experimental && replies + initiations >= experimental;
            int effective = monologue ? replies + initiations : replies;
            ReplicaTier tier;
            string reason;
            if (MinorGuard.IsRestricted(band))
            {
                tier = ReplicaTier.Blocked;
                reason = $"Not available: the evidence says {person.DisplayName} is, or may be, under 18 ({band}). Omniscience never builds a replica of a minor.";
            }
            else if (stats.IsBot)
            {
                tier = ReplicaTier.NotPossible;
                reason = "Not possible: this is a bot account.";
            }
            else if (effective >= ready)
            {
                tier = ReplicaTier.Ready;
                reason = monologue
                    ? $"Ready (monologue mode): {replies} replies and {initiations} conversation starts; replies to messages will be less certain."
                    : $"Ready: {replies} replies to others (at least {ready} needed).";
            }
            else if (effective >= experimental)
            {
                tier = ReplicaTier.Experimental;
                reason = $"Experimental: {effective} usable {(monologue ? "turns" : "replies")} ({experimental}–{ready - 1} is experimental; {ready} or more is ready).";
            }
            else
            {
                tier = ReplicaTier.NotPossible;
                reason = $"Not possible yet: {replies} replies and {initiations} conversation starts; at least {experimental} are needed.";
            }
            if (stats.MinorPeriodExcluded > 0 && tier is not ReplicaTier.Blocked)
                reason += $" {stats.MinorPeriodExcluded} messages from when they were a minor are never used.";
            return new ReplicaReadinessDto(person.CanonicalId, person.DisplayName, tier, monologue ? "initiations" : "replies", reason, replies, initiations, effective,
                stats.ExplicitReplies, stats.RepliesToSelf, stats.AuthoredMessages, stats.NonAuthored, stats.Deleted, stats.MinorPeriodExcluded, stats.ByContext,
                stats.FirstMs, stats.LastMs, stats.Conversations, band.ToString(), adultFromMs, experimental, ready, person.IsSelf, stats.IsBot, stats.Truncated, nowMs, sourceSeq);
        }

        public static bool CanTrain(ReplicaReadinessDto r) => r.Tier is ReplicaTier.Ready or ReplicaTier.Experimental;
    }
}
