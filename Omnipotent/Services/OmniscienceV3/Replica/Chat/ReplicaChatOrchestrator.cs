using System.Collections.Concurrent;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>A chat turn's current state for a stream that may have missed events.</summary>
    public sealed record TurnState(string Status, string? Error, IReadOnlyList<ChatLine> Replies);

    /// <summary>
    /// Replica chat (plan "Composition"; F285–F309). Each reply is a durable job on the interactive LLM lane — never
    /// fire-and-forget, never parkable for 45 minutes (F300): the user line is stored first with status "pending", the job
    /// composes as of the chat's cutoff (the version's data end for live chats, F287–F293), generates N judged candidates
    /// on a cached prefix and stores the reply burst atomically with the user line's new status; a failure marks the user
    /// line failed instead of orphaning it (F306), a crash re-runs the job (idempotent: a turn that already has its reply is
    /// not answered twice). Sending again while a reply is being written supersedes it (both messages get one reply), and
    /// the turn can be cancelled at once from the API or by the client going away (F285). Every replica line is labelled a
    /// simulation and never leaves Omniscience (replicas never post).
    /// </summary>
    public sealed class ReplicaChatOrchestrator
    {
        public const string KindTurn = "chat.turn";

        private sealed record TurnPayload(string TurnId);

        private readonly ReplicaServices s;
        private readonly ChatStore chats;
        private readonly ChatMemory memory;
        private readonly ConcurrentDictionary<string, (string TurnId, CancellationTokenSource Cts)> live = new(StringComparer.Ordinal);

        public ReplicaChatOrchestrator(ReplicaServices services)
        {
            s = services;
            chats = new ChatStore(services.Stores.Operator);
            memory = new ChatMemory(services);
        }

        public ChatStore Chats => chats;

        // ── chats ────────────────────────────────────────────────────────────────

        public async Task<ReplicaChatDto> CreateAsync(ReplicaChatCreateRequest r, string actor, CancellationToken ct)
        {
            string canonical;
            using (var corpus = s.Stores.Corpus.OpenRead())
                canonical = People.Resolve(corpus, r.PersonId)?.CanonicalId ?? throw new ReplicaRefusedException($"No person '{r.PersonId}' in the corpus.", notFound: true);
            var person = s.Versions.Person(canonical);
            string name = person?.DisplayName ?? canonical;
            var serving = person?.ServingVersionId == null ? null : s.Versions.Version(person.ServingVersionId);
            long? asOf = r.AsOfMs;
            if (asOf is long a && a > s.Clock.NowMs) throw new ArgumentException("A chat cannot be set in the future.");
            // Inactive people answer as of their last activity (plan "Time").
            if (asOf == null && serving?.DataToMs is long last && s.Clock.NowMs - last > 90L * 86_400_000L) asOf = last;
            if (ReplicaPrep.MinorRefusal(s, canonical, name, asOf, serving?.AdultFromMs) is { } refusal) throw new ReplicaRefusedException(refusal);
            if (asOf is long t && serving?.DataFromMs is long first && t < first) throw new ReplicaRefusedException($"Nothing of {name} was captured before {ReplicaComposer.Day(first)}.");
            long now = s.Clock.NowMs;
            var chat = new ChatRecord("rc_" + TextFeatures.Hash(canonical + "|" + now + "|" + Guid.NewGuid().ToString("N"), 16), canonical,
                string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim()[..Math.Min(120, r.Title.Trim().Length)], asOf,
                new ChatContext { Kind = ChatKinds.Chat, ContextKind = ContextKinds.Normalize(r.ContextKind), AddresseePersonId = r.AddresseePersonId }, false, now, now);
            await chats.CreateAsync(chat, ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, "replica.chat.create", chat.ChatId, new { canonical, asOf }, ct).ConfigureAwait(false);
            return ChatStore.Dto(chat);
        }

        public async Task<bool> DeleteAsync(string chatId, string actor, CancellationToken ct)
        {
            await CancelAsync(chatId, "chat deleted", ct).ConfigureAwait(false);
            bool deleted = await chats.DeleteAsync(chatId, ct).ConfigureAwait(false);
            await memory.ForgetAsync(chatId, ct).ConfigureAwait(false);
            await s.Stores.Intel.WriteAsync((c, tx) => Storage.Sqlite.SqliteExtensions.Exec(c, tx, "DELETE FROM replica_generations WHERE chat_id=$c", ("$c", chatId)), ct).ConfigureAwait(false);
            if (deleted) await OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, "replica.chat.delete", chatId, null, ct).ConfigureAwait(false);
            return deleted;
        }

        // ── turns ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stores Klives's message and queues the reply. Pending lines of an unanswered turn are folded into this one, and the
        /// in-flight generation is stopped (it would answer without the new message). Returns the turn id and the line's seq.
        /// </summary>
        public async Task<(string TurnId, int Seq)> SendAsync(string chatId, string content, string actor, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("A message is required.");
            if (content.Length > 8_000) throw new ArgumentException("A message is limited to 8000 characters.");
            var chat = RequireChat(chatId, ChatKinds.Chat);
            var (_, serving) = RequireServing(chat);
            string turnId = NewTurnId(chatId);
            long now = s.Clock.NowMs;
            var lines = chats.Lines(chatId);
            var pendingTurns = lines.Where(l => l.Role == "user" && l.Meta.Status == TurnStatuses.Pending && l.Meta.TurnId != null).Select(l => l.Meta.TurnId!).Distinct().ToList();
            var seqs = await chats.AppendAsync(chatId, new[] { ("user", content.Trim(), (object)new { turnId, status = TurnStatuses.Pending }) }, now, ct).ConfigureAwait(false);
            if (pendingTurns.Count > 0)
            {
                await chats.PatchAsync(chatId, l => l.Role == "user" && l.Meta.Status == TurnStatuses.Pending && l.Meta.TurnId != turnId, new { turnId, supersededTurn = true }, ct).ConfigureAwait(false);
                foreach (var old in pendingTurns) await StopAsync(chatId, old, ct).ConfigureAwait(false);
            }
            await EnqueueTurnAsync(chatId, turnId, ct).ConfigureAwait(false);
            await s.PublishAsync(ReplicaTopics.Chat, new { chatId, turnId, type = "accepted", seq = seqs[0], versionId = serving }, ct).ConfigureAwait(false);
            return (turnId, seqs[0]);
        }

        /// <summary>Writes a new reply to the last answered turn; the old reply stays in the transcript, marked replaced.</summary>
        public async Task<string> RegenerateAsync(string chatId, string actor, CancellationToken ct)
        {
            var chat = RequireChat(chatId, ChatKinds.Chat);
            RequireServing(chat);
            var lines = chats.Lines(chatId);
            var lastReply = lines.LastOrDefault(l => l.Role == "assistant" && l.Meta.Status != TurnStatuses.Replaced);
            if (lastReply?.Meta.TurnId is not { } oldTurn) throw new ReplicaRefusedException("There is no reply to regenerate yet.");
            if (lines.Any(l => l.Role == "user" && l.Meta.Status == TurnStatuses.Pending)) throw new ReplicaRefusedException("A reply is still being written.");
            string turnId = NewTurnId(chatId);
            await chats.PatchAsync(chatId, l => l.Role == "assistant" && l.Meta.TurnId == oldTurn, new { status = TurnStatuses.Replaced, replacedBy = turnId }, ct).ConfigureAwait(false);
            await chats.PatchAsync(chatId, l => l.Role == "user" && l.Meta.TurnId == oldTurn, new { turnId, status = TurnStatuses.Pending, regenerated = true }, ct).ConfigureAwait(false);
            await EnqueueTurnAsync(chatId, turnId, ct).ConfigureAwait(false);
            await OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, "replica.chat.regenerate", chatId, new { oldTurn, turnId }, ct).ConfigureAwait(false);
            return turnId;
        }

        /// <summary>Stops the turn being written (API cancel or the client leaving): the user lines are marked cancelled.</summary>
        public async Task<bool> CancelAsync(string chatId, string reason, CancellationToken ct, string? onlyTurnId = null)
        {
            var lines = chats.Lines(chatId);
            var pending = lines.Where(l => l.Role == "user" && l.Meta.Status == TurnStatuses.Pending && (onlyTurnId == null || l.Meta.TurnId == onlyTurnId))
                .Select(l => l.Meta.TurnId).OfType<string>().Distinct().ToList();
            if (pending.Count == 0) return false;
            await chats.PatchAsync(chatId, l => l.Role == "user" && l.Meta.Status == TurnStatuses.Pending && pending.Contains(l.Meta.TurnId ?? ""),
                new { status = TurnStatuses.Cancelled, error = reason }, ct).ConfigureAwait(false);
            foreach (var turn in pending)
            {
                await StopAsync(chatId, turn, ct).ConfigureAwait(false);
                await s.PublishAsync(ReplicaTopics.Chat, new { chatId, turnId = turn, type = "cancelled", reason }, ct).ConfigureAwait(false);
            }
            return true;
        }

        public async Task RateAsync(ReplicaChatRateRequest r, string actor, CancellationToken ct)
        {
            if (r.Rating is < -1 or > 1) throw new ArgumentException("Rating must be -1, 0 or 1.");
            var chat = chats.Get(r.ChatId) ?? throw new KeyNotFoundException("No such chat.");
            var line = chats.Lines(r.ChatId).FirstOrDefault(l => l.Seq == r.Seq) ?? throw new KeyNotFoundException("No such line.");
            if (line.Role != "assistant") throw new ArgumentException("Only replica lines can be rated.");
            await chats.PatchAsync(r.ChatId, l => l.Seq == r.Seq, new { rating = r.Rating, ratingNote = r.Note }, ct).ConfigureAwait(false);
            string key = Storage.Legacy.LegacyImporter.NaturalKey(new Dictionary<string, object?> { ["personId"] = line.Meta.PersonId ?? chat.PersonId, ["chatId"] = r.ChatId, ["turnId"] = line.Meta.TurnId, ["seq"] = r.Seq });
            string verdict = r.Rating > 0 ? "good" : r.Rating < 0 ? "bad" : "neutral";
            long now = s.Clock.NowMs;
            await s.Stores.Operator.WriteAsync((c, tx) =>
            {
                Storage.Sqlite.SqliteExtensions.Exec(c, tx, "UPDATE gold_labels SET superseded_at_ms=$t WHERE kind='replica.reply_rating' AND natural_key=$k AND superseded_at_ms IS NULL", ("$t", now), ("$k", key));
                Storage.Sqlite.SqliteExtensions.Exec(c, tx, "INSERT INTO gold_labels(kind, natural_key, verdict, correction_json, note, decided_at_ms, actor) VALUES('replica.reply_rating',$k,$v,$j,$n,$t,$a)",
                    ("$k", key), ("$v", verdict), ("$j", RJson.Write(new { rating = r.Rating, versionId = line.Meta.VersionId })), ("$n", r.Note), ("$t", now), ("$a", actor));
            }, ct).ConfigureAwait(false);
        }

        public TurnState Turn(string chatId, string turnId)
        {
            var lines = chats.Lines(chatId);
            var user = lines.Where(l => l.Role == "user" && l.Meta.TurnId == turnId).ToList();
            var replies = lines.Where(l => l.Role == "assistant" && l.Meta.TurnId == turnId && l.Meta.Status != TurnStatuses.Replaced).ToList();
            if (replies.Count > 0) return new TurnState(TurnStatuses.Answered, null, replies);
            if (user.Count == 0) return new TurnState(TurnStatuses.Superseded, null, replies);
            var status = user.Select(l => l.Meta.Status).FirstOrDefault(st => st is TurnStatuses.Failed or TurnStatuses.Cancelled) ?? user[0].Meta.Status ?? TurnStatuses.Pending;
            return new TurnState(status, user.Select(l => l.Meta.Error).FirstOrDefault(e => e != null), replies);
        }

        private async Task EnqueueTurnAsync(string chatId, string turnId, CancellationToken ct) =>
            await s.Kernel.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ReplicaSchema.ModuleId,
                Kind = KindTurn,
                SubjectKind = "replica_chat",
                SubjectId = chatId,
                DedupeKey = JobRequest.KeyFor(ReplicaSchema.ModuleId, KindTurn, "replica_chat", chatId + "#" + turnId),
                Lane = Lane.LlmInteractive,
                Priority = 400,
                MaxAttempts = 2,
                PayloadJson = RJson.Write(new TurnPayload(turnId)),
                RunId = turnId,
            }, ct).ConfigureAwait(false);

        private async Task StopAsync(string chatId, string turnId, CancellationToken ct)
        {
            if (live.TryGetValue(chatId, out var entry) && entry.TurnId == turnId) TryCancel(entry.Cts);
            foreach (var state in new[] { JobStates.Queued, JobStates.Leased })
                foreach (var job in s.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = turnId, State = state, Limit = 10 }).Items)
                    await s.Kernel.Jobs.CancelAsync(job.JobId, ct).ConfigureAwait(false);
        }

        /// <summary>Stops an in-flight generation; false when it had already finished (its token source is disposed).</summary>
        private static bool TryCancel(CancellationTokenSource cts)
        {
            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private static string NewTurnId(string chatId) => "rt_" + TextFeatures.Hash(chatId + "|" + Guid.NewGuid().ToString("N"), 16);

        private ChatRecord RequireChat(string chatId, string kind)
        {
            var chat = chats.Get(chatId) ?? throw new KeyNotFoundException("No such chat.");
            if (chat.Archived) throw new ReplicaRefusedException("This chat is archived.");
            if (chat.Context.Kind != kind) throw new ReplicaRefusedException(kind == ChatKinds.Chat ? "This is a group simulation; use the group routes." : "This is a one-to-one chat.");
            return chat;
        }

        private (string PersonId, string VersionId) RequireServing(ChatRecord chat)
        {
            string canonical;
            using (var corpus = s.Stores.Corpus.OpenRead()) canonical = People.Canonical(corpus, chat.PersonId);
            var person = s.Versions.Person(canonical) ?? throw new ReplicaRefusedException("This person has no replica.");
            if (person.Status == PersonStatus.Blocked) throw new ReplicaRefusedException(person.StatusDetail ?? "This replica is blocked by MinorGuard.");
            var version = person.ServingVersionId ?? throw new ReplicaRefusedException(person.BuildingVersionId != null
                ? "The first version of this replica is still training; chat opens when it is ready."
                : "This replica has not been trained yet.");
            var v = s.Versions.Version(version);
            if (ReplicaPrep.MinorRefusal(s, canonical, person.DisplayName ?? canonical, chat.AsOfMs, v?.AdultFromMs) is { } refusal) throw new ReplicaRefusedException(refusal);
            return (canonical, version);
        }

        /// <summary>The reply job (interactive lane).</summary>
        public async Task<string?> ExecuteTurnAsync(JobContext job)
        {
            var payload = job.Payload<TurnPayload>() ?? throw ReplicaFailures.Permanent("Chat turn without a turn id.");
            string chatId = job.Job.SubjectId ?? throw ReplicaFailures.Permanent("Chat turn without a chat.");
            string turnId = payload.TurnId;
            var chat = chats.Get(chatId);
            if (chat == null) return null;
            var lines = chats.Lines(chatId);
            var mine = lines.Where(l => l.Role == "user" && l.Meta.TurnId == turnId).ToList();
            if (mine.Count == 0 || mine.All(l => l.Meta.Status != TurnStatuses.Pending)) return null; // superseded or cancelled
            if (lines.Any(l => l.Role == "assistant" && l.Meta.TurnId == turnId && l.Meta.Status != TurnStatuses.Replaced))
            {
                await chats.PatchAsync(chatId, l => l.Role == "user" && l.Meta.TurnId == turnId, new { status = TurnStatuses.Answered }, job.Cancellation).ConfigureAwait(false);
                return null; // answered before a crash: never twice
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation);
            live[chatId] = (turnId, cts);
            var ct = cts.Token;
            try
            {
                var (personId, versionId) = RequireServing(chat);
                await Stage("composing").ConfigureAwait(false);
                var snapshot = s.Snapshots.Get(versionId);
                long now = s.Clock.NowMs;
                long coreAsOf = chat.AsOfMs ?? snapshot.DataToMs;
                long nowForReplica = chat.AsOfMs ?? now;
                string? addressee = chat.Context.AddresseePersonId ?? ReplicaPrep.SelfPerson(s, snapshot);
                string addresseeName = addressee == null ? "Klives" : ReplicaPrep.NameAsOf(s, addressee, coreAsOf);
                string context = ReplicaPrep.ResolveContext(chat.Context.ContextKind, snapshot, addressee);
                int firstSeq = mine.Min(l => l.Seq);
                // Everything said before this turn (cancelled or failed user lines were still said); replaced replies are out.
                var history = lines.Where(l => l.Seq < firstSeq && !(l.Role == "assistant" && l.Meta.Status == TurnStatuses.Replaced)).ToList();
                int historyBudget = await ReplicaPrep.HistoryBudgetAsync(s, ct).ConfigureAwait(false);
                var prepared = await memory.PrepareAsync(chatId, history, historyBudget, snapshot.Person.DisplayName, addresseeName, job.Job.JobId, ct).ConfigureAwait(false);
                if (chat.Context.Summary is { Length: > 0 } v2Summary && prepared.Summary == null)
                    prepared = prepared with { Summary = v2Summary }; // a chat migrated from v2 keeps its summary
                var turns = prepared.Window.Select(l => new ChatTurn(l.Role == "assistant" ? "assistant" : "user", l.Content, l.AtMs)).ToList();
                var config = (snapshot.Config with { Candidates = chat.Context.Fidelity ? s.Settings.CandidatesFidelity : s.Settings.CandidatesInteractive, LlmJudge = s.Settings.LlmJudge });
                var request = await ReplicaPrep.RequestAsync(s, snapshot, coreAsOf, nowForReplica, context, addressee, addresseeName, turns, mine.Select(l => l.Content).ToList(),
                    config, prepared.Summary, null, null, ct).ConfigureAwait(false);
                var prompt = s.Composer.Compose(request);
                if (s.Generator == null) throw ReplicaFailures.Permanent("This engine has no LLM gateway.");
                var result = await s.Generator.GenerateAsync(new GenerateRequest
                {
                    Prompt = prompt,
                    Recompose = shrink => s.Composer.Compose(request with { Shrink = shrink }),
                    Snapshot = snapshot,
                    Config = config,
                    Candidates = config.Candidates,
                    LlmJudge = config.LlmJudge,
                    Scope = turnId,
                    Interactive = true,
                    JobId = job.Job.JobId,
                    Progress = (stage, i, of) => Stage(stage, i, of),
                }, ct).ConfigureAwait(false);
                string generationId = "rg_" + TextFeatures.Hash(turnId + "|" + job.Job.Attempts, 16);
                await GenerationLog.WriteAsync(s, generationId, "chat", chatId, turnId, null, personId, versionId, coreAsOf, context, prompt, result, ct).ConfigureAwait(false);
                if (!result.Ok) throw ReplicaFailures.Transient("No usable reply: " + result.Problem);

                var replyLines = result.Lines.Select((text, i) => ("assistant", text, (object)new
                {
                    turnId, versionId, generationId, burstIndex = i, burstSize = result.Lines.Count, simulated = true, status = TurnStatuses.Answered,
                })).ToList();
                // One transaction: the reply lines and the user lines' new status (never a reply without its turn, or the reverse).
                var seqs = await chats.AppendAsync(chatId, replyLines, s.Clock.NowMs, ct, (c, tx) =>
                {
                    foreach (var l in mine)
                        Storage.Sqlite.SqliteExtensions.Exec(c, tx, "UPDATE replica_chat_messages SET meta_json=$m WHERE chat_id=$c AND seq=$s",
                            ("$m", ChatStore.MergeMeta(l.MetaJson, new { status = TurnStatuses.Answered, generationId })), ("$c", chatId), ("$s", l.Seq));
                }).ConfigureAwait(false);
                for (int i = 0; i < seqs.Count; i++)
                    await s.PublishAsync(ReplicaTopics.Chat, new { chatId, turnId, type = "message", seq = seqs[i], index = i, of = seqs.Count }, CancellationToken.None).ConfigureAwait(false);
                await s.PublishAsync(ReplicaTopics.Chat, new { chatId, turnId, type = "done", generationId }, CancellationToken.None).ConfigureAwait(false);
                return RJson.Write(new { chatId, turnId, lines = seqs.Count, generationId });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !job.Cancellation.IsCancellationRequested)
            {
                return null; // superseded, cancelled from the API or the client left: the lines already say so
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { throw; }
            catch (ReplicaRefusedException ex)
            {
                await FailTurnAsync(chatId, turnId, ex.Message).ConfigureAwait(false);
                throw ReplicaFailures.Permanent(ex.Message);
            }
            catch (Exception ex) when (job.Job.Attempts >= job.Job.MaxAttempts || ex is JobFailureException { Class: ErrorClass.Permanent })
            {
                await FailTurnAsync(chatId, turnId, ex is JobFailureException ? ex.Message : "The reply could not be written; the service log has the details.").ConfigureAwait(false);
                throw;
            }
            finally
            {
                live.TryRemove(new KeyValuePair<string, (string, CancellationTokenSource)>(chatId, (turnId, cts)));
            }

            Task Stage(string stage, int index = 0, int of = 0) =>
                s.PublishAsync(ReplicaTopics.Chat, new { chatId, turnId, type = "stage", stage, index, of }, ct);
        }

        private async Task FailTurnAsync(string chatId, string turnId, string error)
        {
            await chats.PatchAsync(chatId, l => l.Role == "user" && l.Meta.TurnId == turnId && l.Meta.Status == TurnStatuses.Pending,
                new { status = TurnStatuses.Failed, error }, CancellationToken.None).ConfigureAwait(false);
            await s.PublishAsync(ReplicaTopics.Chat, new { chatId, turnId, type = "failed", error }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One row per generation in intel.db (<c>replica_generations</c>, the Explore dataset of replica turns):
    /// candidates with their judge scores, the chosen one, coverage of the packed prompt, tokens and cache use.</summary>
    internal static class GenerationLog
    {
        public static Task WriteAsync(ReplicaServices s, string generationId, string source, string? chatId, string? turnId, string? runId, string personId, string versionId,
            long asOfMs, string contextKind, ComposedPrompt prompt, GenerationResult r, CancellationToken ct) =>
            s.Stores.Intel.WriteAsync((c, tx) => Storage.Sqlite.SqliteExtensions.Exec(c, tx, """
                INSERT OR REPLACE INTO replica_generations(generation_id, source, chat_id, turn_id, run_id, person_id, version_id, as_of_ms, context_kind, model, prompt_version,
                    candidates, chosen_index, chosen_score, outcome, latency_ms, prompt_tokens, cached_tokens, completion_tokens, coverage_json, candidates_json, created_at_ms)
                VALUES($g,$src,$c,$t,$r,$p,$v,$a,$ck,$m,$pv,$n,$ci,$cs,$o,$l,$pt,$ct,$cpt,$cov,$cand,$now)
                """, ("$g", generationId), ("$src", source), ("$c", chatId), ("$t", turnId), ("$r", runId), ("$p", personId), ("$v", versionId), ("$a", asOfMs), ("$ck", contextKind),
                ("$m", r.Model), ("$pv", ReplicaPrompts.Compose), ("$n", r.Candidates.Count), ("$ci", r.Chosen < 0 ? null : r.Chosen),
                ("$cs", r.Chosen < 0 ? null : r.Candidates[r.Chosen].Score?.Total), ("$o", r.Ok ? "ok" : "failed"), ("$l", r.LatencyMs), ("$pt", r.PromptTokens),
                ("$ct", r.CachedTokens), ("$cpt", r.CompletionTokens),
                ("$cov", RJson.Write(new { prompt.Coverage, reflex = prompt.ReflexClass, reflexConfidence = prompt.ReflexConfidence, r.Transforms, r.Signals, r.Problem })),
                ("$cand", RJson.Write(r.Candidates)), ("$now", s.Clock.NowMs)), ct);
    }
}
