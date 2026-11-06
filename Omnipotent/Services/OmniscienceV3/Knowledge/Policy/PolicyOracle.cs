using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    /// <summary>Answers policy questions about people for the kernel (MinorGuard bands, canonical ids).</summary>
    public interface IPolicyOracle
    {
        /// <summary>The age band of <paramref name="personId"/> (resolved through merges) at <paramref name="atMs"/>.</summary>
        AgeBand BandOf(string personId, long atMs);

        /// <summary>The canonical id of a person (merges resolved); the id itself when unknown.</summary>
        string Canonical(string personId);
    }

    /// <summary>
    /// The Phase 1 oracle: Klives's explicit minor marks (operator.db) and the corpus canonical map. The knowledge
    /// workstream wraps it with time-adjusted birth intervals from age evidence (MinorGuard.Evaluate), so the band
    /// gets stricter, never looser, as evidence arrives.
    /// </summary>
    public sealed class OperatorPolicyOracle : IPolicyOracle
    {
        private readonly OmniscienceStores stores;

        public OperatorPolicyOracle(OmniscienceStores stores) => this.stores = stores;

        public string Canonical(string personId)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<string>(null, "SELECT canonical_id FROM person_canonical WHERE person_id=$p", ("$p", personId)) ?? personId;
        }

        public AgeBand BandOf(string personId, long atMs)
        {
            string canonical = Canonical(personId);
            using var c = stores.Operator.OpenRead();
            bool marked = c.Scalar<long?>(null, "SELECT minor_marked FROM person_settings WHERE person_id IN ($p,$c) ORDER BY minor_marked DESC LIMIT 1",
                ("$p", personId), ("$c", canonical)) is 1;
            return MinorGuard.Evaluate(null, marked, atMs);
        }
    }
}
