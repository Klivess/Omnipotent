using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Agents
{
    public sealed class CycleRequest
    {
        public required string GameId { get; init; }
        public required GameStage Stage { get; init; }
        public required string Role { get; init; }
        public required string SystemPrompt { get; init; }
        public required string Briefing { get; init; }
        public required AgentToolbox Tools { get; init; }
        /// <summary>
        /// A guard against a runaway loop, not a work budget. A cycle normally ends when the agent reports, or hands
        /// over when its context fills (<see cref="WrapUpAtPromptTokens"/>); the next cycle carries on from the
        /// hand-over, so work is never cut off by a count.
        /// </summary>
        public int MaxTurns { get; init; } = 300;
        /// <summary>Prompt size at which the agent is asked to hand over (append-only history keeps growing).</summary>
        public long WrapUpAtPromptTokens { get; init; } = 120_000;
        /// <summary>Turns the agent may take to finish the step it is on after being asked to hand over.</summary>
        public int HandOverGraceTurns { get; init; } = 8;
        /// <summary>
        /// Bytes of pictures one request may carry (as sent, before base64). Not a limit on what the agent sees: every
        /// picture is shown, and once those in the history pass this, the oldest are swapped for a one-line note.
        /// AIRouter's front door refuses a body somewhere between 3.9 MB (the largest that got through) and 4.9 MB (the
        /// smallest refused, HTTP 413); 2 MB is 2.7 MB as base64, leaving room for the text of a full context.
        /// </summary>
        public long PictureBytesPerRequest { get; init; } = 2_000_000;
        public LlmCallOptions Options { get; init; } = new();
        /// <summary>Sees the cycle as it runs (the dashboard's live view and the cycle log). Null: nobody watches.</summary>
        public ICycleObserver? Observer { get; init; }
        /// <summary>Messages from the owner that arrived since the last turn (id, text), delivered into the running cycle.</summary>
        public Func<IReadOnlyList<(string Id, string Text)>>? OwnerMessages { get; init; }
    }

    public sealed class CycleOutcome
    {
        public required CycleReport Report { get; init; }
        public bool Finished { get; init; }
        public bool StageComplete { get; init; }
        public string? BlockedReason { get; init; }
        public List<KeyFact> KeyFacts { get; init; } = new();
        public List<string> DropFacts { get; init; } = new();
        public List<(string Id, string Response)> DirectivesDone { get; init; } = new();
        public List<Decision> NewDecisions { get; init; } = new();
        public string? Error { get; init; }
        /// <summary>The model or its provider failed (not the agent): retried later, never counted against the game.</summary>
        public bool ProviderError { get; init; }
        public int ToolErrors { get; init; }
    }

    /// <summary>
    /// Runs one work cycle: a fresh KliveLLM tool session seeded with the role prompt and the dossier
    /// briefing, an append-only tool loop, and a structured hand-over (finish_cycle) at the end. The hand-over
    /// IS the compaction: the next cycle starts from the dossier, not from this transcript.
    ///
    /// What the first studio got wrong, and this runner does instead:
    /// - Replies were capped at 3-6K tokens with reasoning on, so long files and tool calls were cut off mid-way
    ///   and the half-written call still ran. A reply that hits the output limit ("length") is now detected: a
    ///   call whose arguments were cut off is not run, and the agent is told to write in smaller pieces.
    /// - Cycles ended at a fixed turn count. They now end when the agent reports, or hand over when the context
    ///   fills, and the next cycle continues; the turn limit only stops a runaway loop.
    /// - Nobody could see a cycle until it ended. An observer now sees every call, result and picture.
    /// - The owner's directions waited for the next briefing; they are now delivered into the running cycle.
    /// - Pictures went to the model as lossless PNGs and piled up until the request was too large for AIRouter (HTTP
    ///   413), losing the cycle after three or four renders. They now go as JPEG, every one is shown, and once the
    ///   pictures in the history near the limit the oldest are swapped for a note. A refused request drops the
    ///   pictures already sent and carries on.
    ///
    /// Every request of a cycle is the previous one plus new messages, under one unchanging tool list,
    /// so AIRouter's prefix cache can serve everything already sent (see KliveLLM/PromptPrefixStability).
    /// </summary>
    public sealed class AgentCycleRunner
    {
        public const string FinishToolName = "finish_cycle";

        private readonly IOmniBloxLlm llm;
        private readonly Func<DateTime> clock;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public AgentCycleRunner(IOmniBloxLlm llm, Func<DateTime>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            this.llm = llm;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.delay = delay ?? Task.Delay;
        }

        public static AgentTool FinishTool() => new()
        {
            Name = FinishToolName,
            Description = "End this work cycle with a structured hand-over. The next cycle starts ONLY from the dossier and this report, so put everything that matters here: what you did, decisions and why, open problems, the next steps, and exact facts (ids, file names, numbers) that must not be lost. Set stage_complete when this stage's exit criteria are all met; set blocked_reason only if you cannot continue without the owner.",
            Parameters = Schema.Object(
                Schema.Req("summary", Schema.Str("What this cycle achieved, in a few sentences.")),
                Schema.Opt("decisions", Schema.Arr(Schema.Object(Schema.Req("what", Schema.Str("The decision")), Schema.Req("why", Schema.Str("The reason"))), "Decisions made this cycle")),
                Schema.Opt("problems", Schema.StrList("Open problems or risks, one per item")),
                Schema.Opt("next_steps", Schema.StrList("Concrete next steps for the next cycle, in order, one per item")),
                Schema.Opt("key_facts", Schema.Arr(Schema.Object(
                        Schema.Req("key", Schema.Str("A short topic, e.g. 'economy', 'file layout', 'plot layout', 'spawn'. A fact with the same key REPLACES the old one.")),
                        Schema.Req("fact", Schema.Str("The fact, word for word: ids, file names, positions, numbers"))),
                    "Facts to keep in every later briefing. Update a fact by reusing its key rather than adding a near-copy.")),
                Schema.Opt("drop_facts", Schema.StrList("Keys of facts that are no longer true")),
                Schema.Opt("directives_done", Schema.Arr(Schema.Object(Schema.Req("id", Schema.Str("The owner directive's id, e.g. d2")), Schema.Req("response", Schema.Str("What you did about it"))), "Owner directives you completed this cycle")),
                Schema.Opt("stage_complete", Schema.Bool("True when every exit criterion of the current stage is met")),
                Schema.Opt("blocked_reason", Schema.Str("Only if the owner must act before work can continue"))),
            Handler = (args, _) => Task.FromResult(new ToolResult { Text = "Cycle report recorded.", Finish = args }),
        };

        public static string NewCycleId(DateTime utc) => $"c{utc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

        public async Task<CycleOutcome> RunAsync(CycleRequest request, CancellationToken ct)
        {
            if (!request.Tools.Has(FinishToolName)) request.Tools.Add(FinishTool());
            // One tool list for the whole cycle, wrap-up included: the schemas are rendered at the head of
            // every prompt, so changing them for any one call re-prefills the transcript behind them.
            var tools = request.Tools.Definitions;
            var observer = request.Observer ?? NullCycleObserver.Instance;
            var started = clock();
            string cycleId = NewCycleId(started);
            string session = $"omniblox-{request.Role}-{request.GameId}-{Guid.NewGuid():N}";
            var usage = new UsageTotals();
            int turns = 0, toolErrors = 0, toolCalls = 0, nudges = 0, truncations = 0, wrapUpTurn = 0, cutOffFailures = 0;
            long previousPrompt = 0, pictureBudget = request.PictureBytesPerRequest;
            bool wrapUpSent = false, providerError = false, runaway = false;
            // Bytes of each message that still carries its pictures, oldest first.
            var pictureMessages = new List<long>();
            JObject? finish = null;
            string? error = null;
            string lastText = "";
            var failureCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            void Account(LlmTurn turn)
            {
                usage.Add(1, turn.PromptTokens, turn.CachedTokens, turn.CompletionTokens);
                // Append-only, so the previous request is an exact prefix of this one: all of it could
                // have been served from cache, and whatever was not is a lapse worth seeing.
                if (previousPrompt > 0 && turn.PromptTokens > 0) usage.AddResent(previousPrompt, turn.CachedTokens);
                if (turn.PromptTokens > 0) previousPrompt = turn.PromptTokens;
                observer.Usage(turn.PromptTokens, turn.CachedTokens, turn.CompletionTokens);
            }

            void Say(string text)
            {
                llm.AppendUser(session, text);
                observer.Note(text);
            }

            observer.Started(cycleId, request.Role, request.Stage);
            llm.StartSession(session, request.SystemPrompt);
            try
            {
                // The briefing closes with its own "Now:" line. A receipt stamp at its head would stop it
                // sharing its stable sections with the previous cycle's briefing.
                llm.AppendStableUser(session, request.Briefing);
                while (finish == null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (turns >= request.MaxTurns) { runaway = true; break; }
                    DeliverOwnerMessages(request, Say);
                    turns++;
                    var turn = await QueryWithRetryAsync(session, tools, request.Options, ct);
                    Account(turn);
                    if (!turn.Success && turn.Truncated && ++cutOffFailures <= 4)
                    {
                        // A reply cut off inside a tool call the provider could not even parse: nothing was kept.
                        truncations++;
                        Say("Your last reply was cut off at the output limit in the middle of a tool call, so nothing ran. Send smaller calls: a long file as write_file (first part) followed by append_file, and changes to existing files as edit_file.");
                        continue;
                    }
                    if (!turn.Success && turn.TooLarge && llm.DropPictures(session, keepNewest: 0) > 0)
                    {
                        // The pruning below should keep this from happening; if it does, the provider's limit is
                        // lower than measured. Rewriting messages already sent re-prefills the cycle once, which beats
                        // losing it, and requests carry half the pictures from here on.
                        pictureMessages.Clear();
                        pictureBudget /= 2;
                        Say("The request had grown too large for the model provider, so the pictures shown so far were removed (their text results are still here). New pictures are still shown: look again at anything you need.");
                        continue;
                    }
                    if (!turn.Success)
                    {
                        error = turn.Error ?? "the model call failed";
                        providerError = true;
                        break;
                    }
                    cutOffFailures = 0;
                    if (!string.IsNullOrWhiteSpace(turn.Text))
                    {
                        lastText = turn.Text;
                        observer.AssistantText(turn.Text);
                    }
                    if (turn.Truncated) truncations++;

                    if (turn.ToolCalls.Count == 0)
                    {
                        if (turn.Truncated)
                        {
                            Say("Your last reply was cut off at the output limit before it reached a tool call, so nothing ran. Keep replies short and act through the tools: write a long file in parts (write_file for the first part, then append_file), and change existing files with edit_file instead of rewriting them.");
                            continue;
                        }
                        if (++nudges > 3) break;
                        Say("Keep going: act through the tools. When this cycle's work is done (or you must stop), call finish_cycle with a full report.");
                        continue;
                    }
                    nudges = 0;

                    var images = new List<byte[]>();
                    foreach (var call in turn.ToolCalls)
                    {
                        toolCalls++;
                        observer.ToolCall(call.Id, call.Name, call.ArgumentsJson);
                        ToolResult result;
                        if (turn.Truncated && !IsCompleteJson(call.ArgumentsJson))
                            result = ToolResult.Error(CutOffMessage(call.Name));
                        else
                            result = await request.Tools.ExecuteAsync(call.Name, call.ArgumentsJson, ct);
                        string text = result.Text;
                        if (result.IsError)
                        {
                            toolErrors++;
                            string key = call.Name + "|" + Normalize(call.ArgumentsJson);
                            int count = failureCounts[key] = failureCounts.GetValueOrDefault(key) + 1;
                            if (count >= 3) text += "\nThis exact call has now failed " + count + " times. Do something different: read the error, check your inputs, or take another approach.";
                        }
                        llm.AppendToolResult(session, call.Id, call.Name, Clip(text, 16_000));
                        observer.ToolResult(call.Id, call.Name, text, result.IsError, result.Images);
                        if (result.Images.Count > 0) images.AddRange(result.Images);
                        if (result.Finish != null) finish = result.Finish;
                    }
                    if (images.Count > 0 && finish == null)
                    {
                        var shown = images.Select(ModelPictures.ForModel).ToList();
                        llm.AppendUser(session, "Pictures returned by your last tool call(s):", shown);
                        pictureMessages.Add(shown.Sum(p => (long)p.Length));
                        if (pictureMessages.Sum() > pictureBudget)
                        {
                            // Every picture stays in the request until it is swapped out, so the oldest go once they
                            // would carry it past the provider's limit. Pruning down to half the budget, not just under
                            // it, rewrites the sent history (a re-prefill) once per several renders, not every turn.
                            int keep = 1;
                            long kept = pictureMessages[^1];
                            while (keep < pictureMessages.Count && kept + pictureMessages[^(keep + 1)] <= pictureBudget / 2)
                                kept += pictureMessages[^(++keep)];
                            llm.DropPictures(session, keep);
                            pictureMessages.RemoveRange(0, pictureMessages.Count - keep);
                        }
                    }
                    if (finish != null) break;

                    if (!wrapUpSent && turn.PromptTokens >= request.WrapUpAtPromptTokens)
                    {
                        wrapUpSent = true;
                        wrapUpTurn = turns;
                        Say("This cycle's context is nearly full. Finish the step you are on (at most a few more calls), then call finish_cycle with a complete hand-over. Nothing is lost: the next cycle continues from your report, so put the exact next steps and facts in it.");
                    }
                    else if (wrapUpSent && (turns - wrapUpTurn >= request.HandOverGraceTurns || turn.PromptTokens >= request.WrapUpAtPromptTokens * 135 / 100))
                    {
                        break; // into the hand-over below
                    }
                    if (!wrapUpSent && turns == request.MaxTurns - 3)
                    {
                        wrapUpSent = true;
                        wrapUpTurn = turns;
                        Say("This cycle has run very long. Finish the step you are on, then call finish_cycle with a complete hand-over; the next cycle continues from it.");
                    }
                }

                if (finish == null && error == null)
                {
                    // Out of turns, nudges or context: ask for the hand-over under the same tool list (offering only
                    // finish_cycle would re-prefill the whole cycle for this one call). Other calls are answered
                    // without running, so the request stays append-only; a second chance follows.
                    Say(runaway
                        ? "This cycle is over (it ran far longer than any cycle should). Call finish_cycle now with a complete report of what was done and what comes next."
                        : "This cycle is over. Call finish_cycle now with a complete report of what was done and exactly what comes next; the next cycle starts from it.");
                    for (int attempt = 0; attempt < 2 && finish == null; attempt++)
                    {
                        turns++;
                        var last = await QueryWithRetryAsync(session, tools, request.Options, ct);
                        Account(last);
                        if (!last.Success && last.TooLarge && llm.DropPictures(session, keepNewest: 0) > 0)
                        {
                            pictureMessages.Clear();
                            continue;
                        }
                        if (!last.Success) break;
                        if (!string.IsNullOrWhiteSpace(last.Text)) lastText = last.Text;
                        if (last.ToolCalls.Count == 0) continue;
                        foreach (var call in last.ToolCalls)
                        {
                            string result = "Not run: this cycle is over. Call finish_cycle with your report.";
                            if (call.Name == FinishToolName && finish == null)
                            {
                                try
                                {
                                    finish = JObject.Parse(call.ArgumentsJson);
                                    result = "Recorded.";
                                }
                                catch (Newtonsoft.Json.JsonException)
                                {
                                    result = last.Truncated
                                        ? "ERROR: your report was cut off at the output limit. Call finish_cycle again with a shorter report."
                                        : "ERROR: the arguments are not valid JSON. Call finish_cycle again with one JSON object.";
                                }
                            }
                            llm.AppendToolResult(session, call.Id, call.Name, result);
                            observer.ToolResult(call.Id, call.Name, result, finish == null, Array.Empty<byte[]>());
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                llm.EndSession(session);
            }

            var now = clock();
            var report = new CycleReport
            {
                CycleId = cycleId,
                StartedUtc = started,
                Utc = now,
                Stage = request.Stage,
                Role = request.Role,
                Turns = turns,
                ToolCalls = toolCalls,
                ToolErrors = toolErrors,
                Truncations = truncations,
                PromptTokens = usage.PromptTokens,
                CachedTokens = usage.CachedTokens,
                CompletionTokens = usage.CompletionTokens,
                ResentTokens = usage.ResentTokens,
                ResentCachedTokens = usage.ResentCachedTokens,
                Outcome = error != null ? "error" : finish == null ? "cut short" : wrapUpSent ? "handed over" : "finished",
                Error = error,
                Done = finish != null ? Args.Str(finish, "summary") : (error != null ? "Cycle failed: " + error : Clip(lastText, 1500)),
                Problems = finish != null ? Args.Prose(finish, "problems").Take(20).ToList() : new List<string>(),
                NextSteps = finish != null ? Args.Prose(finish, "next_steps").Take(20).ToList() : new List<string>(),
            };
            var decisions = new List<Decision>();
            if (finish?["decisions"] is JArray arr)
                foreach (var d in arr.OfType<JObject>())
                {
                    string what = Args.Str(d, "what");
                    if (what.Length == 0) continue;
                    decisions.Add(new Decision { Utc = now, Stage = request.Stage, What = Clip(what, 400), Why = Clip(Args.Str(d, "why"), 400) });
                    report.Decisions.Add(what);
                }
            observer.Finished(report);
            return new CycleOutcome
            {
                Report = report,
                Finished = finish != null,
                StageComplete = finish != null && Args.Bool(finish, "stage_complete"),
                BlockedReason = finish != null && Args.Str(finish, "blocked_reason") is { Length: > 0 } b ? b : null,
                KeyFacts = finish != null ? ReadFacts(finish["key_facts"], now, request.Stage) : new List<KeyFact>(),
                DropFacts = finish != null ? Args.List(finish, "drop_facts") : new List<string>(),
                DirectivesDone = finish != null ? ReadDirectivesDone(finish["directives_done"]) : new List<(string, string)>(),
                NewDecisions = decisions,
                Error = error,
                ProviderError = providerError,
                ToolErrors = toolErrors,
            };
        }

        private static void DeliverOwnerMessages(CycleRequest request, Action<string> say)
        {
            if (request.OwnerMessages == null) return;
            IReadOnlyList<(string Id, string Text)> messages;
            try { messages = request.OwnerMessages(); }
            catch { return; }
            foreach (var (id, text) in messages)
                say($"Message from the owner (directive {id}), just now: \"{text}\"\nThis takes priority over your current plan. Act on it, and when it is done list it in finish_cycle's directives_done with what you did.");
        }

        /// <summary>Facts from finish_cycle: objects {key, fact}, plain strings, or (the old glitch) one string holding a JSON array.</summary>
        internal static List<KeyFact> ReadFacts(JToken? token, DateTime utc, GameStage stage)
        {
            var facts = new List<KeyFact>();
            if (token == null || token.Type == JTokenType.Null) return facts;
            if (token.Type == JTokenType.String)
            {
                string text = ((string?)token ?? "").Trim();
                if (text.StartsWith('[') && text.EndsWith(']'))
                {
                    try { token = JToken.Parse(text); }
                    catch (Newtonsoft.Json.JsonException) { /* read as lines below */ }
                }
                if (token.Type == JTokenType.String)
                {
                    foreach (var line in Args.ListOf(token, commas: false)) facts.Add(FactBook.Parse(line, utc, stage));
                    return facts;
                }
            }
            if (token is JObject single) token = new JArray(single);
            foreach (var item in token as JArray ?? new JArray())
            {
                if (item is JObject o)
                {
                    string text = Args.Str(o, "fact") is { Length: > 0 } f ? f : Args.Str(o, "text") is { Length: > 0 } t ? t : Args.Str(o, "value");
                    if (text.Length == 0) continue;
                    facts.Add(new KeyFact { Key = FactBook.NormalizeKey(Args.Str(o, "key")), Text = FactBook.Normalize(text), Utc = utc, Stage = stage });
                }
                else if (item.Type == JTokenType.String && ((string?)item ?? "").Trim().Length > 0)
                {
                    facts.Add(FactBook.Parse((string)item!, utc, stage));
                }
            }
            return facts.Take(30).ToList();
        }

        private static List<(string Id, string Response)> ReadDirectivesDone(JToken? token)
        {
            var list = new List<(string, string)>();
            foreach (var item in token as JArray ?? new JArray())
            {
                if (item is JObject o && Args.Str(o, "id") is { Length: > 0 } id) list.Add((id, Clip(Args.Str(o, "response"), 600)));
                else if (item.Type == JTokenType.String && ((string?)item ?? "").Trim() is { Length: > 0 } plain) list.Add((plain, ""));
            }
            return list;
        }

        private static bool IsCompleteJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return true; // a call with no arguments
            try
            {
                JToken.Parse(json);
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        internal static string CutOffMessage(string tool) =>
            $"NOT RUN: your reply hit the output limit while writing this {tool} call, so its arguments were cut off and nothing was changed. " +
            "Send it again in smaller pieces: a long file as write_file (first part) followed by append_file calls, changes to existing files as edit_file, " +
            "and long lists of objects split over several calls.";

        private async Task<LlmTurn> QueryWithRetryAsync(string session, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
        {
            var turn = await llm.QueryAsync(session, tools, options, ct);
            // A reply cut off at the output limit would only be cut off again, and a request refused as too large
            // would only be refused again: the caller handles both.
            for (int attempt = 1; !turn.Success && !turn.Truncated && !turn.TooLarge && attempt <= 3; attempt++)
            {
                await delay(TimeSpan.FromSeconds(10 * attempt), ct);
                turn = await llm.QueryAsync(session, tools, options, ct);
            }
            return turn;
        }

        private static string Normalize(string json)
        {
            try { return JToken.Parse(json).ToString(Newtonsoft.Json.Formatting.None); }
            catch { return json.Trim(); }
        }

        internal static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + $"… [{s.Length - max} more characters cut]";
    }
}
