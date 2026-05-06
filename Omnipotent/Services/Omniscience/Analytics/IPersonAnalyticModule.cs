using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics
{
    /// <summary>
    /// Pluggable analytic that, given a person, returns a JSON payload describing one
    /// dimension of their behaviour. Modules MUST be deterministic, side-effect-free,
    /// and complete in seconds for thousands of messages.
    /// New modules are added by registering them in <see cref="AnalyticsEngine"/>.
    /// </summary>
    public interface IPersonAnalyticModule
    {
        /// <summary>Stable name written to person_statistics.module_name.</summary>
        string Name { get; }

        /// <summary>Bumped when payload schema changes. Triggers recompute.</summary>
        int Version { get; }

        Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct);
    }
}
