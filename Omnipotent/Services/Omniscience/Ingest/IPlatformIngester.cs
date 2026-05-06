using System;
using System.Threading;
using System.Threading.Tasks;
using Omnipotent.Services.Omniscience.Domain;

namespace Omnipotent.Services.Omniscience.Ingest
{
    /// <summary>
    /// Abstract interface implemented by per-platform ingesters (Discord, future: Instagram, etc.).
    /// Ingesters are responsible for connecting to a platform via stored harvest-source credentials,
    /// normalising raw events into <see cref="HarvestedMessage"/>, and forwarding them to a callback.
    /// </summary>
    public interface IPlatformIngester
    {
        string Platform { get; }

        Task StartAsync(CancellationToken ct);

        Task StopAsync();

        /// <summary>Fired whenever a normalised message has been produced.</summary>
        event Func<HarvestedMessage, Task>? OnNormalisedMessage;

        /// <summary>Fired whenever the ingester learns about a person identity outside a message context.</summary>
        event Func<HarvestedIdentity, Task>? OnIdentityObserved;

        /// <summary>Trigger an explicit, full backfill across every conversation reachable by a given source.</summary>
        Task RequestBackfillAsync(string sourceId, CancellationToken ct);
    }
}
