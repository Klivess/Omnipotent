using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.Omniscience.Replica
{
#pragma warning disable CS4014
    /// <summary>
    /// Replica fidelity benchmark: hold out the person's most recent real
    /// (stimulus → reply) pairs, have the replica predict each reply (with the real
    /// reply excluded from its own retrieval pool), and score predictions on embedding
    /// similarity + stylometric match. One fidelity number per run makes accuracy
    /// changes measurable — every trainer/prompt tweak is benchmarkable, regressions
    /// visible. Worst misses are kept in details_json for error-driven refinement.
    /// </summary>
    public class ReplicaFidelity
    {
        private const int DefaultPairCount = 30;

        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly SemaphoreSlim runLock = new(1, 1);

        private static readonly Regex LaughToken = new(@"\b(lo+l+|lmao+|rofl|(?:ha){2,}|(?:he){2,}|ke+k+|x+d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ReplicaFidelity(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<JObject?> RunBenchmarkAsync(string personId, int pairCount, CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct)) return null;
            try
            {
                // Hold-out set: the most recent pairs with substantive replies.
                var pairs = new List<(string Stimulus, string Reply, string? ReplyMessageId)>();
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
                    cmd.CommandText = $@"SELECT stimulus_text, reply_text, reply_message_id
                        FROM stimulus_reply_pairs
                        WHERE replier_identity_id IN ({inC}) AND length(reply_text) >= 8
                        ORDER BY occurred_at DESC LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", Math.Clamp(pairCount, 5, 100));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        pairs.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
                }
                if (pairs.Count < 5) return null;

                double sumEmb = 0, sumStyle = 0;
                int scored = 0;
                var details = new JArray();
                foreach (var (stimulus, realReply, replyMessageId) in pairs)
                {
                    ct.ThrowIfCancellationRequested();
                    string? predicted;
                    try { predicted = await service.ReplicaChat.GenerateOnceAsync(personId, stimulus, replyMessageId, ct); }
                    catch { predicted = null; }
                    if (string.IsNullOrWhiteSpace(predicted)) continue;

                    double embSim;
                    try
                    {
                        var a = await service.SearchIndex.EmbedQueryAsync(realReply, ct);
                        var b = await service.SearchIndex.EmbedQueryAsync(predicted, ct);
                        embSim = ReplicaEmbedder.CosineSimilarity(a, b);
                    }
                    catch { continue; }
                    double styleScore = StyleMatchScore(realReply, predicted);

                    sumEmb += embSim;
                    sumStyle += styleScore;
                    scored++;
                    details.Add(new JObject(
                        new JProperty("stimulus", Truncate(stimulus, 200)),
                        new JProperty("real", Truncate(realReply, 250)),
                        new JProperty("predicted", Truncate(predicted, 250)),
                        new JProperty("embedding_similarity", Math.Round(embSim, 3)),
                        new JProperty("style_score", Math.Round(styleScore, 3))));
                }
                if (scored == 0) return null;

                double avgEmb = sumEmb / scored;
                double avgStyle = sumStyle / scored;
                double fidelity = 0.6 * avgEmb + 0.4 * avgStyle;

                // Keep the worst misses first in the stored details for refinement review.
                var sortedDetails = new JArray(details.OrderBy(d =>
                    0.6 * d.Value<double>("embedding_similarity") + 0.4 * d.Value<double>("style_score")));

                var run = new JObject(
                    new JProperty("run_id", Guid.NewGuid().ToString("N")),
                    new JProperty("person_id", personId),
                    new JProperty("replica_version", replicaVersion),
                    new JProperty("ran_at", DateTime.UtcNow.ToString("o")),
                    new JProperty("pairs_tested", scored),
                    new JProperty("avg_embedding_similarity", Math.Round(avgEmb, 3)),
                    new JProperty("avg_style_score", Math.Round(avgStyle, 3)),
                    new JProperty("overall_fidelity", Math.Round(fidelity, 3)));

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"INSERT INTO replica_fidelity_runs
                        (run_id, person_id, replica_version, ran_at, pairs_tested, avg_embedding_similarity, avg_style_score, overall_fidelity, details_json)
                        VALUES($id,$p,$v,$t,$n,$e,$s,$f,$d)";
                    cmd.Parameters.AddWithValue("$id", run.Value<string>("run_id"));
                    cmd.Parameters.AddWithValue("$p", personId);
                    cmd.Parameters.AddWithValue("$v", replicaVersion);
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$n", scored);
                    cmd.Parameters.AddWithValue("$e", avgEmb);
                    cmd.Parameters.AddWithValue("$s", avgStyle);
                    cmd.Parameters.AddWithValue("$f", fidelity);
                    cmd.Parameters.AddWithValue("$d", sortedDetails.ToString(Formatting.None));
                    cmd.ExecuteNonQuery();
                }
                finally { db.WriteLock.Release(); }

                await service.ServiceLog($"[Omniscience] Replica fidelity for {personId}: {fidelity:0.000} ({scored} pairs, emb {avgEmb:0.000}, style {avgStyle:0.000})");
                return run;
            }
            finally { runLock.Release(); }
        }

        // Stylometric match: length ratio, casing, terminal punctuation, emoji + laughter habits.
        internal static double StyleMatchScore(string real, string predicted)
        {
            double score = 0;
            // Length: ratio of shorter to longer (1.0 = same length).
            double lenRatio = Math.Min(real.Length, predicted.Length) / (double)Math.Max(1, Math.Max(real.Length, predicted.Length));
            score += 0.35 * lenRatio;
            // Casing of first letter.
            bool realLower = real.Length > 0 && char.IsLetter(real[0]) && char.IsLower(real[0]);
            bool predLower = predicted.Length > 0 && char.IsLetter(predicted[0]) && char.IsLower(predicted[0]);
            score += 0.2 * (realLower == predLower ? 1 : 0);
            // Terminal period habit.
            bool realPeriod = real.TrimEnd().EndsWith('.');
            bool predPeriod = predicted.TrimEnd().EndsWith('.');
            score += 0.15 * (realPeriod == predPeriod ? 1 : 0);
            // Emoji presence.
            bool realEmoji = real.EnumerateRunes().Any(r => r.Value is >= 0x1F300 and <= 0x1FAFF);
            bool predEmoji = predicted.EnumerateRunes().Any(r => r.Value is >= 0x1F300 and <= 0x1FAFF);
            score += 0.15 * (realEmoji == predEmoji ? 1 : 0);
            // Laughter presence.
            bool realLaugh = LaughToken.IsMatch(real);
            bool predLaugh = LaughToken.IsMatch(predicted);
            score += 0.15 * (realLaugh == predLaugh ? 1 : 0);
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
                    cmd.CommandText = @"SELECT run_id, replica_version, ran_at, pairs_tested, avg_embedding_similarity,
                               avg_style_score, overall_fidelity, details_json
                        FROM replica_fidelity_runs WHERE person_id=$p ORDER BY ran_at DESC LIMIT 20";
                    cmd.Parameters.AddWithValue("$p", personId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        JArray worst = new();
                        try
                        {
                            var all = JArray.Parse(r.IsDBNull(7) ? "[]" : r.GetString(7));
                            worst = new JArray(all.Take(5)); // stored worst-first
                        }
                        catch { }
                        arr.Add(new JObject(
                            new JProperty("run_id", r.GetString(0)),
                            new JProperty("replica_version", r.GetInt32(1)),
                            new JProperty("ran_at", DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).UtcDateTime.ToString("o")),
                            new JProperty("pairs_tested", r.GetInt32(3)),
                            new JProperty("avg_embedding_similarity", r.IsDBNull(4) ? 0 : Math.Round(r.GetDouble(4), 3)),
                            new JProperty("avg_style_score", r.IsDBNull(5) ? 0 : Math.Round(r.GetDouble(5), 3)),
                            new JProperty("overall_fidelity", r.IsDBNull(6) ? 0 : Math.Round(r.GetDouble(6), 3)),
                            new JProperty("worst_misses", worst)));
                    }
                    await req.ReturnResponse(new JObject(new JProperty("runs", arr)).ToString(Formatting.None));
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/omniscience/replica/fidelity/run", async req =>
            {
                try
                {
                    var body = JsonConvert.DeserializeObject<JObject>(req.userMessageContent ?? "{}") ?? new JObject();
                    string personId = body.Value<string>("personId") ?? "";
                    int pairs = body.Value<int?>("pairs") ?? DefaultPairCount;
                    if (personId.Length == 0) { await req.ReturnResponse("personId required", code: HttpStatusCode.BadRequest); return; }
                    _ = Task.Run(async () =>
                    {
                        try { await RunBenchmarkAsync(personId, pairs, CancellationToken.None); }
                        catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Fidelity benchmark failed"); }
                    });
                    await req.ReturnResponse("{\"ok\":true,\"message\":\"benchmark queued; poll /omniscience/replica/fidelity\"}");
                }
                catch (Exception ex) { await Err(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives);
        }

        private static async Task Err(KliveAPI.KliveAPI.UserRequest req, Exception ex)
        {
            try { await req.ReturnResponse(new JObject(new JProperty("error", ex.Message)).ToString(Formatting.None), code: HttpStatusCode.InternalServerError); }
            catch { }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
