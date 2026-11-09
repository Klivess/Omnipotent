using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The replica API under <c>/omniscience/v3/replica/</c>. Reads are indexed lookups of maintained state (the replica card
    /// is a read model); commands validate synchronously and return what they created (F013/F014: the version and its job
    /// id, never "ok, job 0"); chat replies and group turns stream as NDJSON (fetch + ReadableStream keeps the Bearer
    /// header) with a heartbeat every few seconds, and a client that goes away cancels the reply it was waiting for (F285).
    /// Permissions: replica.read for reads, replica.chat for chats, blind tests and simulations, replica.train for training.
    /// </summary>
    public sealed class ReplicaRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public ReplicaRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        private ReplicaRuntime R => (host.Require().Host.Modules.OfType<ReplicaModule>().FirstOrDefault()
                                     ?? throw ApiException.Unavailable(ApiErrors.Unavailable, "The replica module is not installed in this build.")).Runtime
                                    ?? throw ApiException.Unavailable(ApiErrors.Starting, "The replica module is starting.");

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("replica/status", OmnisciencePerms.ReplicaRead, req => ApiResponses.OkAsync(req, Status()), ct).ConfigureAwait(false);

            await Get("replica/persons", OmnisciencePerms.ReplicaRead, req =>
            {
                var q = ApiQuery.Of(req);
                string? status = q.Str("status", 16);
                int limit = q.Int("limit", 50, 1, 200);
                var rows = host.Require().Host.ReadModels.List(ReplicaCards.ReadModelId, null, q.Str("cursor", 256), limit);
                var items = rows.Select(r => JsonSerializer.Deserialize<ReplicaPersonDto>(r.BodyJson, ApiJson.Options)!).Where(p => status == null || p.Status == status).Select(Guard).ToList();
                return ApiResponses.OkAsync(req, new ReplicaPersonPageDto(items, rows.Count < limit ? null : rows[^1].Key));
            }, ct).ConfigureAwait(false);

            await Get("replica/person", OmnisciencePerms.ReplicaRead, req =>
            {
                string id = ApiQuery.Of(req).Required("id", 256);
                var rt = host.Require();
                string canonical = rt.Host.Policy.Canonical(id);
                var row = rt.Host.ReadModels.Get(ReplicaCards.ReadModelId, canonical)
                          ?? throw new ApiException(HttpStatusCode.NotFound, "not_built", "No replica card for this person yet: request readiness (POST replica/readiness) to compute it.");
                return ApiResponses.OkAsync(req, Guard(JsonSerializer.Deserialize<ReplicaPersonDto>(row.BodyJson, ApiJson.Options)!));
            }, ct).ConfigureAwait(false);

            await Post("replica/readiness", OmnisciencePerms.ReplicaRead, async req =>
            {
                var body = ApiBody.Read<ReplicaPersonRequest>(req);
                long job = await Refusals(() => R.Trainer.RequestReadinessAsync(body.PersonId, host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ReplicaJobStartedDto(host.Require().Host.Policy.Canonical(body.PersonId), job), HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/train", OmnisciencePerms.ReplicaTrain, async req =>
            {
                var body = ApiBody.Read<ReplicaTrainRequest>(req);
                var result = await Refusals(() => R.Trainer.RequestAsync(body.PersonId, body.Reason ?? "requested", ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, result, HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/train/cancel", OmnisciencePerms.ReplicaTrain, async req =>
            {
                var body = ApiBody.Read<ReplicaPersonRequest>(req);
                bool changed = await R.Trainer.CancelAsync(host.Require().Host.Policy.Canonical(body.PersonId), ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ReplicaActionResultDto(changed, changed ? "The build was cancelled; the serving version is unchanged." : "No build was running.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/autoretrain", OmnisciencePerms.ReplicaTrain, async req =>
            {
                var body = ApiBody.Read<ReplicaAutoRetrainRequest>(req);
                string canonical = host.Require().Host.Policy.Canonical(body.PersonId);
                if (R.Services.Versions.Person(canonical) == null) throw ApiException.NotFound("This person has no replica.");
                await R.Services.Versions.SetAutoRetrainAsync(canonical, body.Enabled, host.Stopping).ConfigureAwait(false);
                await ReplicaCards.RefreshAsync(R.Services, canonical, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ReplicaActionResultDto(true, body.Enabled ? "Retrains automatically when stale." : "Retrains only on request.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("replica/versions", OmnisciencePerms.ReplicaRead, req =>
            {
                string canonical = host.Require().Host.Policy.Canonical(ApiQuery.Of(req).Required("personId", 256));
                return ApiResponses.OkAsync(req, new ReplicaVersionListDto(R.Services.Versions.Versions(canonical).Select(ReplicaCards.Summary).ToList()));
            }, ct).ConfigureAwait(false);

            await Get("replica/version", OmnisciencePerms.ReplicaRead, req =>
            {
                var v = R.Services.Versions.Version(ApiQuery.Of(req).Required("id", 128)) ?? throw ApiException.NotFound("No such version.");
                return ApiResponses.OkAsync(req, VersionDto(v));
            }, ct).ConfigureAwait(false);

            await Get("replica/evals", OmnisciencePerms.ReplicaRead, req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, Evals(q.Required("versionId", 128), q.Str("split", 32), q.Str("cursor", 256), q.Int("limit", 50, 1, 200), req.Can(OmnisciencePerms.MessagesRead)));
            }, ct).ConfigureAwait(false);

            // ── chats ──
            await Get("replica/chats", OmnisciencePerms.ReplicaRead, req =>
            {
                var q = ApiQuery.Of(req);
                string? person = q.Str("personId", 256) is { } p ? host.Require().Host.Policy.Canonical(p) : null;
                try
                {
                    var (items, next) = R.Chat.Chats.List(person, q.Str("kind", 32), q.Bool("archived") ?? false, q.Str("cursor", 256), q.Int("limit", 50, 1, 200));
                    return ApiResponses.OkAsync(req, new ReplicaChatPageDto(items.Select(ChatStore.Dto).ToList(), next));
                }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("replica/chats/messages", OmnisciencePerms.ReplicaRead, req =>
            {
                var q = ApiQuery.Of(req);
                string chatId = q.Required("chatId", 128);
                if (R.Chat.Chats.Get(chatId) == null) throw ApiException.NotFound("No such chat.");
                int after = (int)(q.Long("cursor") ?? 0);
                int limit = q.Int("limit", 200, 1, 1000);
                var lines = R.Chat.Chats.Lines(chatId, after, limit + 1);
                var page = lines.Take(limit).Select(l => ChatStore.Dto(chatId, l)).ToList();
                return ApiResponses.OkAsync(req, new ReplicaChatMessagePageDto(page, lines.Count > limit ? page[^1].Seq : null));
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/create", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaChatCreateRequest>(req);
                var chat = await Refusals(() => R.Chat.CreateAsync(body, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, chat, HttpStatusCode.Created).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/update", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaChatUpdateRequest>(req);
                if (R.Chat.Chats.Get(body.ChatId) == null) throw ApiException.NotFound("No such chat.");
                string? title = body.Title?.Trim();
                if (title is { Length: > 120 }) title = title[..120];
                await R.Chat.Chats.UpdateAsync(body.ChatId, title, body.Archived, R.Services.Clock.NowMs, host.Stopping).ConfigureAwait(false);
                await Kernel.OperatorAudit.WriteAsync(R.Services.Stores.Operator, R.Services.Clock, ApiBody.Actor(req), "replica.chat.update", body.ChatId, new { title, body.Archived }, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, ChatStore.Dto(R.Chat.Chats.Get(body.ChatId)!)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/delete", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaChatActionRequest>(req);
                bool deleted = await R.Chat.DeleteAsync(body.ChatId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ReplicaActionResultDto(deleted, deleted ? "Deleted." : "No such chat.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/send", OmnisciencePerms.ReplicaChat, req =>
            {
                var body = ApiBody.Read<ReplicaChatSendRequest>(req);
                return StreamTurnAsync(req, body.ChatId, async () => await R.Chat.SendAsync(body.ChatId, body.Content, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/regenerate", OmnisciencePerms.ReplicaChat, req =>
            {
                var body = ApiBody.Read<ReplicaChatActionRequest>(req);
                return StreamTurnAsync(req, body.ChatId, async () => (await R.Chat.RegenerateAsync(body.ChatId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false), (int?)null));
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/cancel", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaChatActionRequest>(req);
                bool changed = await R.Chat.CancelAsync(body.ChatId, "cancelled by " + ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new ReplicaActionResultDto(changed, changed ? "Stopped." : "No reply was being written.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/chats/rate", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaChatRateRequest>(req);
                try { await R.Chat.RateAsync(body, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false); }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
                await ApiResponses.OkAsync(req, new ReplicaActionResultDto(true, "Recorded as a gold label.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // ── group simulation ──
            await Post("replica/groups/create", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaGroupCreateRequest>(req);
                var (chat, _) = await Refusals(() => R.Groups.CreateAsync(body.PersonIds ?? Array.Empty<string>(), body.Topic, body.ConversationId, ApiBody.Actor(req), null, host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, chat, HttpStatusCode.Created).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/groups/advance", OmnisciencePerms.ReplicaChat, req =>
            {
                var body = ApiBody.Read<ReplicaGroupAdvanceRequest>(req);
                return StreamGroupAsync(req, body.ChatId, () => Refusals(() => R.Groups.AdvanceAsync(body.ChatId, body.Turns, host.Stopping)));
            }, ct).ConfigureAwait(false);

            await Post("replica/groups/send", OmnisciencePerms.ReplicaChat, req =>
            {
                var body = ApiBody.Read<ReplicaGroupSendRequest>(req);
                return StreamGroupAsync(req, body.ChatId, async () =>
                {
                    await Refusals(() => R.Groups.SendAsync(body.ChatId, body.Content, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                    return await Refusals(() => R.Groups.AdvanceAsync(body.ChatId, body.Turns, host.Stopping)).ConfigureAwait(false);
                });
            }, ct).ConfigureAwait(false);

            // ── blind test ──
            await Post("replica/blind/start", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaBlindStartRequest>(req);
                string canonical = host.Require().Host.Policy.Canonical(body.PersonId);
                var session = await Refusals(() => R.Blind.StartAsync(canonical, body.Count, ApiBody.Actor(req), host.Stopping)).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, session, HttpStatusCode.Created).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("replica/blind/answer", OmnisciencePerms.ReplicaChat, async req =>
            {
                var body = ApiBody.Read<ReplicaBlindAnswerRequest>(req);
                try { await ApiResponses.OkAsync(req, await R.Blind.AnswerAsync(body.SessionId, body.Index, body.Choice, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false)).ConfigureAwait(false); }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("replica/blind/session", OmnisciencePerms.ReplicaChat, req =>
                ApiResponses.OkAsync(req, R.Blind.Get(ApiQuery.Of(req).Required("id", 128)) ?? throw ApiException.NotFound("No such blind test.")), ct).ConfigureAwait(false);

            await Get("replica/blind/results", OmnisciencePerms.ReplicaRead, req =>
                ApiResponses.OkAsync(req, R.Blind.Results(host.Require().Host.Policy.Canonical(ApiQuery.Of(req).Required("personId", 256)))), ct).ConfigureAwait(false);
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static async Task<T> Refusals<T>(Func<Task<T>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (ReplicaRefusedException ex) when (ex.NotFound) { throw ApiException.NotFound(ex.Message); }
            catch (ReplicaRefusedException ex) { throw new ApiException(HttpStatusCode.UnprocessableEntity, "refused", ex.Message); }
            catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
        }

        /// <summary>MinorGuard at serialisation (defence in depth): a blocked card carries its status and nothing else.</summary>
        private static ReplicaPersonDto Guard(ReplicaPersonDto p) => p.Status == PersonStatus.Blocked
            ? p with { Readiness = p.Readiness == null ? null : p.Readiness with { ByContext = new Dictionary<string, int>(), FirstMs = null, LastMs = null }, Serving = null, Building = null, FidelityTrend = Array.Empty<ReplicaFidelityPointDto>() }
            : p;

        private ReplicaModuleStatusDto Status()
        {
            var s = R.Services;
            var persons = s.Versions.Persons();
            ReplicaHouseModelDto? model;
            using (var c = s.Stores.Intel.OpenRead())
                model = c.Query(null, "SELECT version, state, trained_at_ms, metrics_json, detail FROM replica_models WHERE model_id=$m",
                    r => new ReplicaHouseModelDto(AssistantVoiceModel.ModelId, r.GetInt32(0), r.GetString(1), r.GetInt64OrNull(2), RJson.Read<AssistantVoiceMetrics>(r.GetStringOrNull(3)), r.GetStringOrNull(4)),
                    ("$m", AssistantVoiceModel.ModelId)).FirstOrDefault();
            return new ReplicaModuleStatusDto(s.EncoderModel, s.Encoder() != null, model, persons.Count, persons.Count(p => p.Status == PersonStatus.Ready),
                persons.Count(p => p.BuildingVersionId != null), persons.Count(p => p.Status == PersonStatus.Failed), persons.Count(p => p.Status == PersonStatus.Blocked),
                persons.Count(p => p.Stale), s.Clock.NowMs);
        }

        private ReplicaVersionDto VersionDto(VersionRecord v)
        {
            List<ReplicaEraDto> eras;
            using (var c = R.Services.Stores.Intel.OpenRead())
                eras = c.Query(null, "SELECT era_index, from_ms, to_ms, pairs, summary_json FROM replica_eras WHERE version_id=$v ORDER BY era_index",
                    r => new ReplicaEraDto(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), RJson.Read<Dictionary<string, double>>(r.GetString(4)) ?? new Dictionary<string, double>()), ("$v", v.VersionId));
            return new ReplicaVersionDto(ReplicaCards.Summary(v), v.PersonId, v.ModuleVersion, v.RequestedBy, eras, RJson.Read<ReplicaStyleDto>(v.StyleJson), RJson.Read<GenerationConfig>(v.ConfigJson));
        }

        private ReplicaEvalPageDto Evals(string versionId, string? split, string? cursor, int limit, bool texts)
        {
            using var c = R.Services.Stores.Intel.OpenRead();
            var rows = c.Query(null, """
                SELECT e.pair_id, e.config_key, e.split, e.context_kind, e.occurred_ms, e.as_of_ms, e.generated_text, e.outcome, e.judge_correct, e.judge_confidence,
                       e.embedding_sim, e.style_distance, e.length_error, e.model, p.stimulus_plain, p.reply_text
                FROM replica_eval_items e LEFT JOIN replica_pairs p ON p.version_id=e.version_id AND p.pair_id=e.pair_id
                WHERE e.version_id=$v AND ($s IS NULL OR e.split=$s) AND ($c IS NULL OR e.pair_id || '|' || e.config_key > $c)
                ORDER BY e.pair_id, e.config_key LIMIT $l
                """, r => new ReplicaEvalItemDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
                    texts ? r.GetStringOrNull(14) : null, texts ? r.GetStringOrNull(15) : null, r.GetStringOrNull(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetInt32(8) == 1,
                    r.GetDoubleOrNull(9), r.GetDoubleOrNull(10), r.GetDoubleOrNull(11), r.GetDoubleOrNull(12), r.GetStringOrNull(13)),
                ("$v", versionId), ("$s", split), ("$c", cursor), ("$l", limit + 1));
            var page = rows.Take(limit).ToList();
            return new ReplicaEvalPageDto(page, rows.Count > limit ? page[^1].PairId + "|" + page[^1].ConfigKey : null, texts);
        }

        /// <summary>
        /// Streams one chat turn: <c>accepted</c>, stages, each reply line, then <c>done</c> (or <c>failed</c>/<c>cancelled</c>), with a
        /// heartbeat every 5 s that also re-reads the turn (an event that raced the subscription is never lost). The subscription
        /// starts before the turn is queued. If the client leaves before the end, the turn is cancelled (F285).
        /// </summary>
        private async Task StreamTurnAsync(UserRequest req, string chatId, Func<Task<(string TurnId, int? Seq)>> start)
        {
            var rt = host.Require();
            var replica = R;
            var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            using var subscription = rt.Host.Events.Subscribe(ev =>
            {
                if (ev.Topic == ReplicaTopics.Chat && ev.PayloadJson.Contains(chatId, StringComparison.Ordinal)) channel.Writer.TryWrite(ev);
                return ValueTask.CompletedTask;
            });
            var (turnId, seq) = await Refusals(start).ConfigureAwait(false);
            await NdjsonStream.WriteAsync(req, Events(), host.Stopping).ConfigureAwait(false);

            async IAsyncEnumerable<object> Events()
            {
                bool finished = false;
                try
                {
                    yield return new ReplicaStreamEventDto("accepted", chatId, turnId, Index: seq);
                    using var overall = CancellationTokenSource.CreateLinkedTokenSource(host.Stopping);
                    overall.CancelAfter(TimeSpan.FromMinutes(10));
                    var sentLines = new HashSet<int>();
                    while (!overall.IsCancellationRequested)
                    {
                        StreamEvent? ev = null;
                        using (var tick = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
                        {
                            tick.CancelAfter(TimeSpan.FromSeconds(5));
                            try { ev = await channel.Reader.ReadAsync(tick.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!overall.IsCancellationRequested) { ev = null; }
                        }
                        if (ev == null)
                        {
                            var state = replica.Chat.Turn(chatId, turnId);
                            if (state.Status != TurnStatuses.Pending)
                            {
                                foreach (var line in state.Replies.Where(l => sentLines.Add(l.Seq)))
                                    yield return new ReplicaStreamEventDto("message", chatId, turnId, Message: ChatStore.Dto(chatId, line));
                                finished = true;
                                yield return Final(chatId, turnId, state);
                                yield break;
                            }
                            yield return new ReplicaStreamEventDto("heartbeat", chatId, turnId);
                            continue;
                        }
                        using var doc = JsonDocument.Parse(ev.PayloadJson);
                        var p = doc.RootElement;
                        if (!p.TryGetProperty("turnId", out var t) || t.GetString() != turnId) continue;
                        string type = p.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                        switch (type)
                        {
                            case "stage":
                                yield return new ReplicaStreamEventDto("stage", chatId, turnId, p.GetProperty("stage").GetString(), p.GetProperty("index").GetInt32(), p.GetProperty("of").GetInt32());
                                break;
                            case "message":
                                int lineSeq = p.GetProperty("seq").GetInt32();
                                var lineRow = replica.Chat.Chats.Lines(chatId, lineSeq - 1, 1).FirstOrDefault();
                                if (lineRow != null && sentLines.Add(lineSeq)) yield return new ReplicaStreamEventDto("message", chatId, turnId, Message: ChatStore.Dto(chatId, lineRow));
                                break;
                            case "done" or "failed" or "cancelled":
                                var final = replica.Chat.Turn(chatId, turnId);
                                foreach (var line in final.Replies.Where(l => sentLines.Add(l.Seq)))
                                    yield return new ReplicaStreamEventDto("message", chatId, turnId, Message: ChatStore.Dto(chatId, line));
                                finished = true;
                                yield return type == "done" ? new ReplicaStreamEventDto("done", chatId, turnId) : Final(chatId, turnId, final);
                                yield break;
                        }
                    }
                    finished = true; // timed out or stopping: the reply job keeps going and lands in the chat
                    yield return new ReplicaStreamEventDto("timeout", chatId, turnId, Text: "Still writing; the reply will appear in the chat.");
                }
                finally
                {
                    if (!finished) await replica.Chat.CancelAsync(chatId, "the client disconnected", CancellationToken.None, turnId).ConfigureAwait(false);
                }
            }
        }

        private static ReplicaStreamEventDto Final(string chatId, string turnId, TurnState state) => state.Status switch
        {
            TurnStatuses.Answered => new ReplicaStreamEventDto("done", chatId, turnId),
            TurnStatuses.Failed => new ReplicaStreamEventDto("failed", chatId, turnId, Code: "failed", Text: state.Error),
            TurnStatuses.Superseded => new ReplicaStreamEventDto("cancelled", chatId, turnId, Code: "superseded", Text: "A newer message replaced this turn."),
            _ => new ReplicaStreamEventDto("cancelled", chatId, turnId, Code: "cancelled", Text: state.Error),
        };

        /// <summary>Streams group-simulation turns until the advance job reports done/failed (cancelled if the client leaves).</summary>
        private async Task StreamGroupAsync(UserRequest req, string chatId, Func<Task<string>> start)
        {
            var rt = host.Require();
            var replica = R;
            var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            using var subscription = rt.Host.Events.Subscribe(ev =>
            {
                if (ev.Topic == ReplicaTopics.Group && ev.PayloadJson.Contains(chatId, StringComparison.Ordinal)) channel.Writer.TryWrite(ev);
                return ValueTask.CompletedTask;
            });
            int before = replica.Chat.Chats.Lines(chatId).Count;
            string token = await start().ConfigureAwait(false);
            await NdjsonStream.WriteAsync(req, Events(), host.Stopping).ConfigureAwait(false);

            async IAsyncEnumerable<object> Events()
            {
                bool finished = false;
                int sent = before;
                try
                {
                    yield return new ReplicaStreamEventDto("accepted", chatId, token);
                    using var overall = CancellationTokenSource.CreateLinkedTokenSource(host.Stopping);
                    overall.CancelAfter(TimeSpan.FromMinutes(20));
                    while (!overall.IsCancellationRequested)
                    {
                        StreamEvent? ev = null;
                        using (var tick = CancellationTokenSource.CreateLinkedTokenSource(overall.Token))
                        {
                            tick.CancelAfter(TimeSpan.FromSeconds(5));
                            try { ev = await channel.Reader.ReadAsync(tick.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!overall.IsCancellationRequested) { ev = null; }
                        }
                        foreach (var line in replica.Chat.Chats.Lines(chatId, sent))
                        {
                            sent = Math.Max(sent, line.Seq);
                            yield return new ReplicaStreamEventDto("message", chatId, token, Message: ChatStore.Dto(chatId, line));
                        }
                        if (ev == null)
                        {
                            var job = replica.Services.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = token, Limit = 1 }).Items.FirstOrDefault();
                            if (job != null && JobStates.IsTerminal(job.State))
                            {
                                finished = true;
                                yield return job.State == JobStates.Succeeded ? new ReplicaStreamEventDto("done", chatId, token)
                                    : new ReplicaStreamEventDto("failed", chatId, token, Code: job.State, Text: job.ErrorMessage);
                                yield break;
                            }
                            yield return new ReplicaStreamEventDto("heartbeat", chatId, token);
                            continue;
                        }
                        using var doc = JsonDocument.Parse(ev.PayloadJson);
                        string type = doc.RootElement.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                        if (type is "done" or "failed" && doc.RootElement.TryGetProperty("runToken", out var rtk) && rtk.GetString() == token)
                        {
                            foreach (var line in replica.Chat.Chats.Lines(chatId, sent))
                            {
                                sent = Math.Max(sent, line.Seq);
                                yield return new ReplicaStreamEventDto("message", chatId, token, Message: ChatStore.Dto(chatId, line));
                            }
                            finished = true;
                            yield return new ReplicaStreamEventDto(type, chatId, token, Text: doc.RootElement.TryGetProperty("error", out var er) ? er.GetString() : null);
                            yield break;
                        }
                    }
                    finished = true;
                    yield return new ReplicaStreamEventDto("timeout", chatId, token, Text: "Still simulating; new lines will appear in the chat.");
                }
                finally
                {
                    if (!finished)
                        foreach (var state in new[] { JobStates.Queued, JobStates.Leased })
                            foreach (var job in replica.Services.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = token, State = state, Limit = 5 }).Items)
                                await replica.Services.Kernel.Jobs.CancelAsync(job.JobId, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
