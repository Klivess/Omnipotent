using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text.RegularExpressions;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Temporal holdout benchmark. Every held-out message is excluded from every
    /// retrieval/style source, and generation uses the production model path.
    /// </summary>
    public class ReplicaFidelity
    {
        private const int DefaultPairCount = 30;
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly SemaphoreSlim runLock = new(1, 1);
        private static readonly Regex LaughToken = new(@"\b(lo+l+|lmao+|rofl|(?:ha){2,}|(?:he){2,}|ke+k+|x+d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ReplicaFidelity(Omniscience service, OmniscienceDb db) { this.service = service; this.db = db; }

        private sealed record EvaluationPair(string Stimulus, string Reply, string? ReplyMessageId,
            string? StimulusMessageId, long OccurredAt);

        public async Task<JObject?> RunBenchmarkAsync(string personId, int pairCount, CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct)) return null;
            try
            {
                var pairs = new List<EvaluationPair>();
                int replicaVersion = 0;
                using (var conn = db.Open())
                {
                    using (var ver = conn.CreateCommand())
                    {
                        ver.CommandText = "SELECT version FROM replicas WHERE person_id=$p";
                        ver.Parameters.AddWithValue("$p", personId);
                        replicaVersion = Convert.ToInt32(ver.ExecuteScalar() ?? 0);
                    }
                    var idents = Analytics.AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                    if (idents.Count == 0) return null;
                    using var cmd = conn.CreateCommand();
                    string inC = Analytics.AnalyticHelpers.BindInClause(cmd, "i", idents);
                    // Short replies are deliberately included: they are often the most
                    // distinctive part of a person's chat style.
                    cmd.CommandText = $@"SELECT stimulus_text, reply_text, reply_message_id, stimulus_message_id, COALESCE(occurred_at,0)
                        FROM stimulus_reply_pairs
                        WHERE replier_identity_id IN ({inC}) AND length(trim(reply_text)) > 0
                        ORDER BY occurred_at DESC LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", Math.Clamp(pairCount, 5, 100));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        pairs.Add(new EvaluationPair(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4)));
                }
                if (pairs.Count < 5) return null;

                var exclusions = new HashSet<string>(pairs.SelectMany(p => new[] { p.ReplyMessageId, p.StimulusMessageId })
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!), StringComparer.Ordinal);
                double sumEmb = 0, sumStyle = 0;
                int scored = 0, failed = 0;
                var details = new JArray();
                var models = new HashSet<string>(StringComparer.Ordinal);
                var providers = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in pairs)
                {
                    ct.ThrowIfCancellationRequested();
                    ReplicaChatOrchestrator.ReplicaChatResult? generated = null;
                    try
                    {
                        generated = await service.ReplicaChat.GenerateDetailedOnceAsync(personId, pair.Stimulus,
                            new ReplicaChatOptions { AsOfAt = pair.OccurredAt > 0 ? pair.OccurredAt : null },
                            exclusions, useSelfCritique: false, ct);
                    }
                    catch { }
                    string? predicted = generated == null ? null : generated.Polished ?? generated.Draft;
                    if (generated != null)
                    {
                        if (!string.IsNullOrWhiteSpace(generated.ModelUsed)) models.Add(generated.ModelUsed);
                        if (!string.IsNullOrWhiteSpace(generated.Provider)) providers.Add(generated.Provider);
                    }
                    if (string.IsNullOrWhiteSpace(predicted))
                    {
                        failed++;
                        details.Add(FailureDetail(pair, "generation_failed"));
                        continue;
                    }

                    double embSim;
                    try
                    {
                        var a = await service.SearchIndex.EmbedQueryAsync(pair.Reply, ct);
                        var b = await service.SearchIndex.EmbedQueryAsync(predicted, ct);
                        embSim = ReplicaEmbedder.CosineSimilarity(a, b);
                    }
                    catch
                    {
                        failed++;
                        details.Add(FailureDetail(pair, "scoring_failed", predicted));
                        continue;
                    }
                    double styleScore = StyleMatchScore(pair.Reply, predicted);
                    sumEmb += embSim;
                    sumStyle += styleScore;
                    scored++;
                    details.Add(new JObject(
                        new JProperty("status", "ok"),
                        new JProperty("occurred_at", pair.OccurredAt),
                        new JProperty("era", pair.OccurredAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(pair.OccurredAt).UtcDateTime.Year : 0),
                        new JProperty("stimulus", Truncate(pair.Stimulus, 200)),
                        new JProperty("real", Truncate(pair.Reply, 250)),
                        new JProperty("predicted", Truncate(predicted, 250)),
                        new JProperty("embedding_similarity", Math.Round(embSim, 3)),
                        new JProperty("style_score", Math.Round(styleScore, 3))));
                }

                double? avgEmb = scored > 0 ? sumEmb / scored : null;
                double? avgStyle = scored > 0 ? sumStyle / scored : null;
                double? fidelity = scored > 0 ? 0.6 * avgEmb!.Value + 0.4 * avgStyle!.Value : null;
                var sortedDetails = new JArray(details.OrderBy(d => d.Value<string>("status") != "ok" ? -1.0 :
                    0.6 * (d.Value<double?>("embedding_similarity") ?? 0) + 0.4 * (d.Value<double?>("style_score") ?? 0)));
                string runId = Guid.NewGuid().ToString("N");
                long ranAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var metadata = new JObject
                {
                    ["pairs_attempted"] = pairs.Count,
                    ["pairs_failed"] = failed,
                    ["pipeline_version"] = "temporal-v2",
                    ["production_model_path"] = true,
                    ["self_critique"] = false,
                    ["models"] = new JArray(models),
                    ["providers"] = new JArray(providers),
                    ["leakage_controls"] = "All holdout reply/stimulus IDs excluded from topical, stimulus, stylistic, reflex, fact and exact style inputs; generation is bounded to each pair timestamp.",
                    ["interpretation"] = "Experimental retrieval/style comparison, not a calibrated accuracy percentage.",
                };
                var run = new JObject(
                    new JProperty("run_id", runId), new JProperty("person_id", personId),
                    new JProperty("replica_version", replicaVersion), new JProperty("ran_at", DateTimeOffset.FromUnixTimeMilliseconds(ranAt).UtcDateTime.ToString("o")),
                    new JProperty("pairs_tested", scored), new JProperty("pairs_attempted", pairs.Count), new JProperty("pairs_failed", failed),
                    new JProperty("avg_embedding_similarity", avgEmb == null ? null : Math.Round(avgEmb.Value, 3)),
                    new JProperty("avg_style_score", avgStyle == null ? null : Math.Round(avgStyle.Value, 3)),
                    new JProperty("overall_fidelity", fidelity == null ? null : Math.Round(fidelity.Value, 3)),
                    new JProperty("method", "temporal-holdout-v2"), new JProperty("metadata", metadata));

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"INSERT INTO replica_fidelity_runs
                        (run_id,person_id,replica_version,ran_at,pairs_tested,avg_embedding_similarity,avg_style_score,overall_fidelity,details_json,method,metadata_json)
                        VALUES($id,$p,$v,$t,$n,$e,$s,$f,$d,$method,$metadata)";
                    cmd.Parameters.AddWithValue("$id", runId);
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.Parameters.AddWithValue("$v", replicaVersion);
                    cmd.Parameters.AddWithValue("$t", ranAt);
                    cmd.Parameters.AddWithValue("$n", scored);
                    cmd.Parameters.AddWithValue("$e", avgEmb == null ? (object)DBNull.Value : avgEmb.Value);
                    cmd.Parameters.AddWithValue("$s", avgStyle == null ? (object)DBNull.Value : avgStyle.Value);
                    cmd.Parameters.AddWithValue("$f", fidelity == null ? (object)DBNull.Value : fidelity.Value);
                    cmd.Parameters.AddWithValue("$d", sortedDetails.ToString(Formatting.None));
                    cmd.Parameters.AddWithValue("$method", "temporal-holdout-v2");
                    cmd.Parameters.AddWithValue("$metadata", metadata.ToString(Formatting.None));
                    cmd.ExecuteNonQuery();
                }
                finally { db.WriteLock.Release(); }
                await service.ServiceLog($"[Omniscience] Replica temporal fidelity for {personId}: {scored}/{pairs.Count} scored, {failed} failed" +
                    (fidelity == null ? "." : $", experimental score {fidelity:0.000}."));
                return run;
            }
            finally { runLock.Release(); }
        }

        private static JObject FailureDetail(EvaluationPair pair, string reason, string? predicted = null) => new(
            new JProperty("status", reason), new JProperty("occurred_at", pair.OccurredAt),
            new JProperty("stimulus", Truncate(pair.Stimulus, 200)), new JProperty("real", Truncate(pair.Reply, 250)),
            new JProperty("predicted", predicted == null ? null : Truncate(predicted, 250)));

        internal static double StyleMatchScore(string real, string predicted)
        {
            double score = 0;
            double lenRatio = Math.Min(real.Length, predicted.Length) / (double)Math.Max(1, Math.Max(real.Length, predicted.Length));
            score += 0.35 * lenRatio;
            bool realLower = real.Length > 0 && char.IsLetter(real[0]) && char.IsLower(real[0]);
            bool predLower = predicted.Length > 0 && char.IsLetter(predicted[0]) && char.IsLower(predicted[0]);
            score += 0.2 * (realLower == predLower ? 1 : 0);
            bool realPeriod = real.TrimEnd().EndsWith('.');
            bool predPeriod = predicted.TrimEnd().EndsWith('.');
            score += 0.15 * (realPeriod == predPeriod ? 1 : 0);
            bool realEmoji = real.EnumerateRunes().Any(r => r.Value is >= 0x1F300 and <= 0x1FAFF);
            bool predEmoji = predicted.EnumerateRunes().Any(r => r.Value is >= 0x1F300 and <= 0x1FAFF);
            score += 0.15 * (realEmoji == predEmoji ? 1 : 0);
            score += 0.15 * (LaughToken.IsMatch(real) == LaughToken.IsMatch(predicted) ? 1 : 0);
            return score;
        }

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/omniscience/replica/fidelity", async req =>
            {
                try
                {
                    string personId = req.userParameters?["personId"] ?? "";
                    var arr = new JArray();
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT run_id,replica_version,ran_at,pairs_tested,avg_embedding_similarity,
                        avg_style_score,overall_fidelity,details_json,method,metadata_json
                        FROM replica_fidelity_runs WHERE person_id=$p ORDER BY ran_at DESC LIMIT 20";
                    cmd.Parameters.AddWithValue("$p", personId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        JArray worst = new();
                        try { worst = new JArray(JArray.Parse(r.IsDBNull(7) ? "[]" : r.GetString(7)).Take(5)); } catch { }
                        JObject metadata = new();
                        try { metadata = JObject.Parse(r.IsDBNull(9) ? "{}" : r.GetString(9)); } catch { }
                        arr.Add(new JObject(
                            new JProperty("run_id", r.GetString(0)), new JProperty("replica_version", r.GetInt32(1)),
                            new JProperty("ran_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).UtcDateTime.ToString("o")),
                            new JProperty("pairs_tested", r.GetInt32(3)),
                            new JProperty("pairs_attempted", metadata.Value<int?>("pairs_attempted") ?? r.GetInt32(3)),
                            new JProperty("pairs_failed", metadata.Value<int?>("pairs_failed") ?? 0),
                            new JProperty("avg_embedding_similarity", r.IsDBNull(4) ? null : Math.Round(r.GetDouble(4), 3)),
                            new JProperty("avg_style_score", r.IsDBNull(5) ? null : Math.Round(r.GetDouble(5), 3)),
                            new JProperty("overall_fidelity", r.IsDBNull(6) ? null : Math.Round(r.GetDouble(6), 3)),
                            new JProperty("method", r.IsDBNull(8) ? "legacy" : r.GetString(8)),
                            new JProperty("metadata", metadata), new JProperty("worst_misses", worst)));
                    }
                    await req.ReturnResponse(new JObject(new JProperty("runs", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, OmnisciencePerms.ReplicaRead);

            await service.CreateAPIRoute("/omniscience/replica/fidelity/run", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    int pairs = body.Value<int?>("pairs") ?? DefaultPairCount;
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    if (CountEvaluationPairs(personId) < 5)
                    {
                        await req.ReturnResponse(new JObject(new JProperty("error", "At least five harvested stimulus/reply pairs are required.")).ToString(Formatting.None),
                            code: HttpStatusCode.BadRequest);
                        return;
                    }
                    _ = Task.Run(async () =>
                    {
                        try { await RunBenchmarkAsync(personId, pairs, CancellationToken.None); }
                        catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Fidelity benchmark failed"); }
                    });
                    await req.ReturnResponse("{\"ok\":true,\"method\":\"temporal-holdout-v2\",\"message\":\"benchmark queued; poll /omniscience/replica/fidelity\"}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, OmnisciencePerms.ReplicaTrain);
        }

        private int CountEvaluationPairs(string personId)
        {
            using var conn = db.Open();
            var idents = Analytics.AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return 0;
            using var cmd = conn.CreateCommand();
            string inC = Analytics.AnalyticHelpers.BindInClause(cmd, "i", idents);
            cmd.CommandText = $"SELECT COUNT(*) FROM stimulus_reply_pairs WHERE replier_identity_id IN ({inC}) AND length(trim(reply_text))>0";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        private static async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            try { await req.ReturnResponse(new JObject(new JProperty("error", ex.Message)).ToString(Formatting.None), code: HttpStatusCode.InternalServerError); }
            catch { }
        }
        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
