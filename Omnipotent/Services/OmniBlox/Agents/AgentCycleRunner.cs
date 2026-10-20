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
        public int MaxTurns { get; init; } = 40;
        /// <summary>Prompt size at which the agent is told to wrap up (append-only history keeps growing).</summary>
        public long WrapUpAtPromptTokens { get; init; } = 70_000;
        public int MaxImageMessages { get; init; } = 8;
        public LlmCallOptions Options { get; init; } = new();
    }

    public sealed class CycleOutcome
    {
        public required CycleReport Report { get; init; }
        public bool Finished { get; init; }
        public bool StageComplete { get; init; }
        public string? BlockedReason { get; init; }
        public List<string> KeyFacts { get; init; } = new();
        public List<Decision> NewDecisions { get; init; } = new();
        public string? Error { get; init; }
        public int ToolErrors { get; init; }
    }

    /// <summary>
    /// Runs one work cycle: a fresh KliveLLM tool session seeded with the role prompt and the dossier
    /// briefing, an append-only tool loop with a turn and context budget, and a structured hand-over
    /// (finish_cycle) at the end. The hand-over IS the compaction: the next cycle starts from the dossier,
    /// not from this transcript. Repeated identical failures and over-long cycles are steered to wrap up.
    ///
    /// Every request of a cycle is the previous one plus new messages, under one unchanging tool list,
    /// so AIRouter's prefix cache can serve everything already sent (see KliveLLM/PromptPrefixStability).
    /// The report counts those re-sent tokens and how many the cache served, so a lapse shows up.
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
                Schema.Opt("problems", Schema.StrList("Open problems or risks")),
                Schema.Opt("next_steps", Schema.StrList("Concrete next steps for the next cycle, in order")),
                Schema.Opt("key_facts", Schema.StrList("Facts to keep verbatim (ids, names, numbers, file paths)")),
                Schema.Opt("stage_complete", Schema.Bool("True when every exit criterion of the current stage is met")),
                Schema.Opt("blocked_reason", Schema.Str("Only if the owner must act before work can continue"))),
            Handler = (args, _) => Task.FromResult(new ToolResult { Text = "Cycle report recorded.", Finish = args }),
        };

        public async Task<CycleOutcome> RunAsync(CycleRequest request, CancellationToken ct)
        {
            if (!request.Tools.Has(FinishToolName)) request.Tools.Add(FinishTool());
            // One tool list for the whole cycle, wrap-up included: the schemas are rendered at the head of
            // every prompt, so changing them for any one call re-prefills the transcript behind them.
            var tools = request.Tools.Definitions;
            string session = $"omniblox-{request.Role}-{request.GameId}-{Guid.NewGuid():N}";
            var usage = new UsageTotals();
            int turns = 0, toolErrors = 0, nudges = 0, imageMessages = 0;
            long previousPrompt = 0;
            bool wrapUpSent = false;
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
            }

            llm.StartSession(session, request.SystemPrompt);
            try
            {
                // The briefing closes with its own "Now:" line. A receipt stamp at its head would stop it
                // sharing its stable sections with the previous cycle's briefing.
                llm.AppendStableUser(session, request.Briefing);
                while (turns < request.MaxTurns && finish == null)
                {
                    ct.ThrowIfCancellationRequested();
                    turns++;
                    var turn = await QueryWithRetryAsync(session, tools, request.Options, ct);
                    Account(turn);
                    if (!turn.Success)
                    {
                        error = turn.Error ?? "the model call failed";
                        break;
                    }
                    if (!string.IsNullOrWhiteSpace(turn.Text)) lastText = turn.Text;

                    if (turn.ToolCalls.Count == 0)
                    {
                        if (++nudges > 2) break;
                        llm.AppendUser(session, "Keep going: act through the tools. When this cycle's work is done (or you must stop), call finish_cycle with a full report.");
                        continue;
                    }

                    var images = new List<byte[]>();
                    foreach (var call in turn.ToolCalls)
                    {
                        var result = await request.Tools.ExecuteAsync(call.Name, call.ArgumentsJson, ct);
                        string text = result.Text;
                        if (result.IsError)
                        {
                            toolErrors++;
                            string key = call.Name + "|" + Normalize(call.ArgumentsJson);
                            int count = failureCounts[key] = failureCounts.GetValueOrDefault(key) + 1;
                            if (count >= 3) text += "\nThis exact call has now failed " + count + " times. Do something different: read the error, check your inputs, or take another approach.";
                        }
                        llm.AppendToolResult(session, call.Id, call.Name, Clip(text, 12_000));
                        if (result.Images.Count > 0) images.AddRange(result.Images);
                        if (result.Finish != null) finish = result.Finish;
                    }
                    if (images.Count > 0 && finish == null)
                    {
                        if (imageMessages < request.MaxImageMessages)
                        {
                            imageMessages++;
                            llm.AppendUser(session, "Pictures returned by your last tool call(s):", images.Take(6).ToList());
                        }
                        else
                        {
                            llm.AppendUser(session, "(Image budget for this cycle is used up; further pictures are not shown. Finish what you can and call finish_cycle.)");
                        }
                    }
                    if (finish == null && !wrapUpSent && turn.PromptTokens >= request.WrapUpAtPromptTokens)
                    {
                        wrapUpSent = true;
                        llm.AppendUser(session, "This cycle's context is nearly full. Finish the step you are on, then call finish_cycle with a complete report — the next cycle will continue from it.");
                    }
                    if (finish == null && turns == request.MaxTurns - 2 && !wrapUpSent)
                    {
                        wrapUpSent = true;
                        llm.AppendUser(session, "Two turns left in this cycle. Call finish_cycle with a complete report now.");
                    }
                }

                if (finish == null && error == null)
                {
                    // Out of turns or nudges: ask for the hand-over under the same tool list (offering only
                    // finish_cycle would re-prefill the whole cycle for this one call). Other calls are
                    // answered without running, so the request stays append-only; a second chance follows.
                    llm.AppendUser(session, "This cycle is over. Call finish_cycle now with a complete report of what was done and what comes next.");
                    for (int attempt = 0; attempt < 2 && finish == null; attempt++)
                    {
                        turns++;
                        var last = await QueryWithRetryAsync(session, tools, request.Options, ct);
                        Account(last);
                        if (!last.Success) break;
                        if (last.ToolCalls.Count == 0)
                        {
                            if (!string.IsNullOrWhiteSpace(last.Text)) lastText = last.Text;
                            break;
                        }
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
                                    result = "ERROR: the arguments are not valid JSON. Call finish_cycle again with one JSON object.";
                                }
                            }
                            llm.AppendToolResult(session, call.Id, call.Name, result);
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

            var report = new CycleReport
            {
                Utc = clock(),
                Stage = request.Stage,
                Role = request.Role,
                Turns = turns,
                PromptTokens = usage.PromptTokens,
                CachedTokens = usage.CachedTokens,
                CompletionTokens = usage.CompletionTokens,
                ResentTokens = usage.ResentTokens,
                ResentCachedTokens = usage.ResentCachedTokens,
                Outcome = error != null ? "error" : finish != null ? "finished" : "cut short",
                Done = finish != null ? Args.Str(finish, "summary") : (error != null ? "Cycle failed: " + error : Clip(lastText, 1500)),
                Problems = finish != null ? Args.List(finish, "problems") : new List<string>(),
                NextSteps = finish != null ? Args.List(finish, "next_steps") : new List<string>(),
            };
            var decisions = new List<Decision>();
            if (finish?["decisions"] is JArray arr)
                foreach (var d in arr.OfType<JObject>())
                {
                    string what = Args.Str(d, "what");
                    if (what.Length == 0) continue;
                    decisions.Add(new Decision { Utc = clock(), Stage = request.Stage, What = Clip(what, 400), Why = Clip(Args.Str(d, "why"), 400) });
                    report.Decisions.Add(what);
                }
            return new CycleOutcome
            {
                Report = report,
                Finished = finish != null,
                StageComplete = finish != null && Args.Bool(finish, "stage_complete"),
                BlockedReason = finish != null && Args.Str(finish, "blocked_reason") is { Length: > 0 } b ? b : null,
                KeyFacts = finish != null ? Args.List(finish, "key_facts").Select(f => Clip(f, 300)).ToList() : new List<string>(),
                NewDecisions = decisions,
                Error = error,
                ToolErrors = toolErrors,
            };
        }

        private async Task<LlmTurn> QueryWithRetryAsync(string session, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
        {
            var turn = await llm.QueryAsync(session, tools, options, ct);
            for (int attempt = 1; !turn.Success && attempt <= 2; attempt++)
            {
                await delay(TimeSpan.FromSeconds(5 * attempt), ct);
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
