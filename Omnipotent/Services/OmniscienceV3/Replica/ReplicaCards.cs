using Omnipotent.Services.OmniscienceV3.Storage;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The replica card read model (<c>replica.person</c>): readiness with numbers, the serving and building versions with
    /// every stage, fidelity per version (the trend), staleness. Written by the module after every transition; routes
    /// read one row (plan "Read models are the only thing routes read").
    /// </summary>
    public static class ReplicaCards
    {
        public const string ReadModelId = "replica.person";
        public const int SchemaVersion = 1;

        public static ReplicaVersionSummaryDto Summary(VersionRecord v) => new(
            v.VersionId, v.Number, v.State, v.Reason, v.CreatedAtMs, v.FinishedAtMs, v.DataFromMs, v.DataToMs, v.EncoderModel,
            Stages.All.Select(name => { var s = v.Stage(name); return new ReplicaStageDto(name, s.State, s.Attempts, s.StartedAtMs, s.FinishedAtMs, s.Detail, s.Error, s.Total, s.Completed, s.Failed); }).ToList(),
            RJson.Read<ReplicaMetricsDto>(v.MetricsJson), v.Degraded, RJson.Read<ReplicaCountsDto>(v.CountsJson), v.Error);

        public static ReplicaPersonDto? Build(ReplicaServices s, string personId)
        {
            var person = s.Versions.Person(personId);
            if (person == null) return null;
            var versions = s.Versions.Versions(personId, 50);
            var serving = person.ServingVersionId == null ? null : versions.FirstOrDefault(v => v.VersionId == person.ServingVersionId) ?? s.Versions.Version(person.ServingVersionId);
            var building = person.BuildingVersionId == null ? null : versions.FirstOrDefault(v => v.VersionId == person.BuildingVersionId) ?? s.Versions.Version(person.BuildingVersionId);
            var trend = versions.Where(v => v.FinishedAtMs != null && v.MetricsJson != null && v.State is VersionStates.Ready or VersionStates.Retired)
                .OrderBy(v => v.Number)
                .Select(v => (v, m: RJson.Read<ReplicaMetricsDto>(v.MetricsJson)))
                .Where(x => x.m != null)
                .Select(x => new ReplicaFidelityPointDto(x.v.VersionId, x.v.Number, x.v.FinishedAtMs!.Value, x.m!.DiscriminationAccuracy, x.m.EmbeddingSimilarity, x.m.StyleDistance, x.m.Evaluated))
                .ToList();
            return new ReplicaPersonDto(person.PersonId, person.DisplayName ?? person.PersonId, person.Status, person.StatusDetail, person.Stale, person.StaleReason, person.StaleSinceMs,
                person.NewAuthored, person.AutoRetrain, RJson.Read<ReplicaReadinessDto>(person.ReadinessJson), serving == null ? null : Summary(serving),
                building == null ? null : Summary(building), trend, person.UpdatedAtMs);
        }

        public static async Task RefreshAsync(ReplicaServices s, string personId, CancellationToken ct)
        {
            var card = Build(s, personId);
            if (card == null) return;
            long seq;
            using (var c = s.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(c);
            await s.Kernel.ReadModels.PutAsync(ReadModelId, personId, SchemaVersion, s.Clock.NowMs, seq, card, ct).ConfigureAwait(false);
        }
    }
}
