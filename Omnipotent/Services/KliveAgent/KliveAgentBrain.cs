using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// The central orchestration brain for KliveAgent.
    ///
    /// Every user message goes through a Think â†’ Script â†’ Observe loop:
    ///   1. Build a rich system prompt: personality + repo map (task-personalised) + BM25 memories + tool catalogue.
    ///   2. LLM responds with a planning statement and optional {{{ C# script }}} blocks.
    ///   3. Scripts are executed; structured observations are fed back.
    ///   4. Loop continues until the LLM produces a text-only response (the final answer).
    ///
    /// Spec references:
    ///   Chapter 7  -- Repo map injected in every prompt
    ///   Chapter 9  -- Agentic Loop Architecture
    ///   Chapter 10 -- Context Window Management &amp; Token Budgeting
    /// </summary>
    public class KliveAgentBrain
    {
        private readonly KliveAgent agentService;
        private readonly KliveAgentScriptEngine scriptEngine;
        private readonly KliveAgentMemory memory;

        private sealed record PromptToolDescriptor(string MethodName, string Description);

        private static readonly PromptToolDescriptor[] StarterTools =
        [
            new("ListProjectClasses", "Browse project types by name, namespace, or path."),
            new("FindProjectClass", "Jump straight to a likely project type when you already know its name."),
            new("ExploreClassCode", "Read the implementation around a target type declaration."),
            new("GetMethodDocumentation", "Inspect exact method signatures, defaults, and XML docs before calling unfamiliar APIs."),
            new("GetTypeSchema", "Inspect a type's members. Each Methods[] entry has a ready-to-call .Signature; params expose .Type (and .ParameterType). One shot — no per-method GetMethodDocumentation needed."),
            new("ListServices", "See which OmniServices are live right now."),
            new("GetService", "Fetch a live service instance by type name or display name."),
            new("GetObjectTypeInfo", "Quick string view of a live object's members; pass a filter, e.g. GetObjectTypeInfo(obj, \"Guild\")."),
            new("GetObjectMembers", "STRUCTURED, filterable introspection of a live object: GetObjectMembers(obj, \"nameFilter\", \"method|property|field\"). Returns a list you LINQ over inline; each method carries a ready-to-call .Signature. Prefer this over JSON-dumping GetObjectTypeInfo."),
            new("ExecuteServiceMethod", "Call a known service method directly when you already know the exact service type and method name."),
            new("CallObjectMethod", "Invoke a method on a live object after inspecting it. Await Task-returning methods."),
            new("GetRecentErrors", "Pull recent Error-level entries from OmniLogging directly. Prefer this over reflecting into the logger."),
            new("Log", "Write short structured observations back to the loop.")
        ];

        private static readonly PromptToolDescriptor[] DiscoveryTools =
        [
            new("SearchCodeHybrid", "Use BM25 search when you need a relevant code entry point fast."),
            new("FindDefinition", "Jump to the exact definition of a type or method."),
            new("FindReferences", "See where a type is used before changing behavior."),
            new("ReadFile", "Read the source directly with line pagination."),
            new("GetFileSymbols", "List the symbols declared in one file."),
            new("ListDirectory", "Browse a directory relative to the codebase root."),
            new("FindFiles", "Find files by FILENAME pattern (e.g. '*Routes*.cs', 'KliveBot*'). Use this for filename-only queries — much cheaper than SearchCode."),
            new("GetRepoMap", "Refresh the live repo map if you need broader structure."),
            new("SearchCode", "Use plain-text search only when a narrower type lookup did not find the target."),
            new("SearchCodeRegex", "Use regex search for signature-shaped lookups.")
        ];

        private static readonly PromptToolDescriptor[] AdvancedRuntimeTools =
        [
            new("GetServiceObject", "Read a field or property from a live service by type name."),
            new("GetObjectMember", "Inspect a specific field or property on a live object."),
            new("GetTypeInfo", "Get a human-readable API summary for a type."),
            new("SearchSymbols", "Search loaded Omnipotent assemblies for matching types, methods, or properties."),
            new("BrowseNamespace", "Browse public types inside a namespace."),
            new("GetFullTypeHierarchy", "Inspect inherited members before calling into framework or base-class behavior."),
            new("ListAgentCapabilities", "See if an explicit typed capability already exists for the task."),
            new("ExecuteAgentCapabilityAsync", "Run an explicit capability when one exists instead of stitching raw reflection together."),
            new("SpawnBackgroundTask", "Launch long-running work only when the task truly needs isolation."),
            new("Delay", "Pause inside long-running scripts while respecting cancellation.")
        ];

        private static readonly PromptToolDescriptor[] MemoryTools =
        [
            new("SaveMemory", "Persist a durable fact about reality (about Klive, Omnipotent, or how a service really behaves). Tags must be a string array, e.g. new[] { \"klive\", \"identity\" }. Returns the saved memory id (string) — log it if the user asked you to confirm."),
            new("RecallMemories", "Search saved memories by free-text query (matches both content and tags as text)."),
            new("RecallMemoriesByTag", "Return memories whose Tags collection contains the given tag exactly (case-insensitive). Prefer over RecallMemories when filtering by a known tag."),
            new("DeleteMemory", "Forget a memory by its id (or short-id prefix shown in prompts). Use this to curate noise/duplicates/outdated beliefs."),
            new("SaveShortcut", "Store a reusable recipe immediately after solving a non-obvious task. Returns the saved memory id (string)."),
            new("GetShortcuts", "Review saved shortcuts before rediscovering a workflow."),
            new("GetAgentStats", "Return today's KliveAgent run-time stats. Top-level: lifetimeScriptsRun, lifetimeScriptFailures, lifetimeScriptFailureRatePct, todayUtcDate, fullSummary. fullSummary contains nested objects: lifetime{messages,promptTokens,completionTokens,totalTokens,iterations,scripts,scriptFailures,scriptSuccessRatePct,...}, today{messages,promptTokens,completionTokens,totalTokens,scripts,scriptFailures,scriptFailureRatePct,...} (today is null on a fresh day before any activity), and dailyHistory[] with the same per-day shape. Access nested values via JSON serialization or GetObjectMember chains.")
        ];

        public KliveAgentBrain(KliveAgent agentService, KliveAgentScriptEngine scriptEngine, KliveAgentMemory memory)
        {
            this.agentService = agentService;
            this.scriptEngine = scriptEngine;
            this.memory = memory;
        }

        // â”€â”€ Prompt Assembly â”€â”€

        public async Task<string> BuildSystemPrompt(string userMessage, AgentConversation conversation)
        {
            var personality = await agentService.GetStringOmniSetting(
                "KliveAgent_Personality",
                defaultValue: KliveAgentPersonality.Default);

            // Extract PascalCase seed words from the user message for repo map personalisation
            var seeds = KliveAgentRepoMap.ExtractSeedsFromText(userMessage);

            // Build token-budgeted, task-personalised repo map (only when the task has code signals).
            var repoMap = string.Empty;
            try
            {
                if (agentService.RepoMap != null && seeds.Count > 0)
                    repoMap = agentService.RepoMap.GetRepoMap(KliveAgentContextBudget.RepoMapBudget, seeds);
            }
            catch { /* best-effort */ }

            // BM25-ranked memories, budget-capped
            var memoriesSection = string.Empty;
            try
            {
                memoriesSection = await memory.FormatMemoriesForPrompt(
                    userMessage,
                    maxMemories: 4,
                    maxShortcuts: 3,
                    maxTokens: KliveAgentContextBudget.MemoryBudget);
            }
            catch { }

            var sb = new StringBuilder();

            sb.AppendLine(personality);
            sb.AppendLine();

            sb.AppendLine("[Rules]");
            sb.AppendLine("- Write C# inside {{{ ... }}} (or ```csharp fences) to inspect/act. Locals persist across blocks in the same reply.");
            sb.AppendLine("- DO NOT emit XML tool-call tags like <function>, <tool_use>, <parameters>, or any JSON tool envelope. They are NOT parsed. The ONLY way to invoke a tool is C# inside {{{ ... }}}.");
            sb.AppendLine("- ONE composite script beats many tiny ones. Do discovery + action + Log() in a single block whenever you can.");
            sb.AppendLine("- TALK WHILE YOU WORK: when a request needs data-gathering scripts, OPEN with a one-line conversational acknowledgement in plain prose BEFORE the {{{ script }}} (e.g. \"On it, pulling that now.\"). The user sees your prose immediately while the script runs. Keep it to one short line — don't narrate every step.");
            sb.AppendLine("- await Task / Task<T> ONLY. GetTypeSchema, GetService, ListServices, CallObjectMethod, ExecuteServiceMethod (non-async overload), Log are SYNC — do not await.");
            sb.AppendLine("- GetService(name) returns object. To call a method on it: CallObjectMethod(svc, \"Method\", args) — it auto-awaits Tasks for you.");
            sb.AppendLine("- If a script errors, READ the error and change approach. Never retry the same failing code.");
            sb.AppendLine("- Never claim an action is done unless a script in this turn ran and returned [OK].");
            sb.AppendLine("- TRUST the tool result. If GetRecentErrors(N) returns an empty list, that means there are zero errors — that IS the answer. Do NOT reflect into OmniLogging fields to second-guess it.");
            sb.AppendLine("- NEVER invent identifiers. Method names, line numbers, file contents, and field values you put in your final answer MUST come verbatim from a tool output you actually received this turn. If the tool returned nothing useful, say 'I couldn't find that' — do NOT confabulate plausible-sounding C# names.");
            sb.AppendLine("- To list private static METHODS in a file (not fields), use SearchCodeRegex with a method-signature pattern: `SearchCodeRegex(@\"^\\s*private\\s+static\\s+(?!readonly)[\\w<>?,\\s\\[\\]]+\\s+\\w+\\s*\\(\", \"path/to/File.cs\")`. The `(?!readonly)` excludes field declarations.");
            sb.AppendLine("- When the user names a specific file (e.g. 'Read X.cs and ...'), call ReadFile(path) directly. Use SearchCode/SearchCodeHybrid only when the file or location is unknown.");
            sb.AppendLine("- SearchCode(text, subfolder) accepts a single .cs file path as the second arg, not just a directory — pass the full file path when you want to search inside ONE file.");
            sb.AppendLine("- If the SAME tool errors twice with the SAME message, STOP retrying it. Switch tools (e.g. SearchCode → ReadFile, or RecallMemories → RecallMemoriesByTag) or accept the answer and finalize.");
            sb.AppendLine("- For run-time stats about yourself (scripts run today, failure rate, token usage), call GetAgentStats() — do NOT search the codebase or claim 'no metric exists'.");
            sb.AppendLine("- For 'in the last N minutes' filters on errors, call GetRecentErrors(50) once and filter the formatted timestamps yourself. Do NOT call it repeatedly with shrinking limits.");
            sb.AppendLine("- To find FILES by filename (e.g. 'every .cs file containing X in the name'), use FindFiles(\"*Pattern*.cs\", \"subfolder\") — it returns the file list directly. Do NOT use SearchCode for filename queries; SearchCode searches CONTENT, not filenames.");
            sb.AppendLine("- To count or list PUBLIC METHODS of a class, call GetTypeSchema(\"TypeName\").Methods (already public-only). Filter `m.IsStatic` for instance vs static. Do NOT try to parse method signatures with SearchCodeRegex when GetTypeSchema works.");
            sb.AppendLine("- For MULTI-STEP tasks (output of step 1 feeds step 2 feeds step 3), chain everything in ONE script block using local variables and `Log` each intermediate so you can see the chain. Use ONLY real APIs from this guide — do NOT invent helper functions like `ParseTopService` or `Aggregate`; if you need parsing, write the regex / LINQ inline. The full pipeline template appears below in [Common Patterns]. Do NOT split a pipeline into separate scripts — locals from script 1 are GONE in script 2.");
            sb.AppendLine("- EMPTY-PREMISE RULE: if the data needed to answer is empty (zero errors, zero matches, no memories with that tag), the EMPTY STATE IS THE ANSWER. Report it directly. Do NOT save a vacuous self-improvement memory, propose imaginary fixes, or fabricate work — 'no errors today' is a complete answer.");
            sb.AppendLine("- To discover an unknown object's members, call GetObjectMembers(obj, \"nameFilter\", \"method|property|field\") and LINQ over the result inline — each method has a ready-to-call .Signature. Do NOT JSON-serialize GetObjectTypeInfo and string-split it, and do NOT guess names. Discover ONCE, then filter→pick→call in the same block.");
            sb.AppendLine("- DSharpPlus live objects cache STALE/empty collections (DiscordGuild.Channels, GuildContainingKlives.Channels). For authoritative data use the async accessors: var g = await CallObjectMethod(GetObjectMember(GetService(\"KliveBotDiscord\"),\"Client\"), \"GetGuildAsync\", guildId, (bool?)null); then await CallObjectMethod(g, \"GetChannelsAsync\"). The live client field is 'Client', NOT 'botClient'.");
            sb.AppendLine("- Final answer = a reply with NO script blocks. Keep it punchy. Final replies must contain the actual answer — NEVER finalize with phrases like 'Let me get/find/check/call X' or 'I'll now Y'; those mean you should run another script in the SAME turn.");
            sb.AppendLine();

            sb.AppendLine("[Common Patterns]");
            sb.AppendLine("// Call any service method (sync or async) — works for object returned by GetService:");
            sb.AppendLine("var svc = GetService(\"KliveBotDiscord\"); var r = await CallObjectMethod(svc, \"SendMessageToKlives\", \"hello\");");
            sb.AppendLine("// Inspect a service's API before guessing (each method has a ready-to-call .Signature):");
            sb.AppendLine("var schema = GetTypeSchema(\"KliveBotDiscord\"); foreach (var m in schema.Methods) Log(m.Signature);");
            sb.AppendLine("// Discover a live object's members cheaply (filter + kind), then call — ONE round-trip:");
            sb.AppendLine("var client = GetObjectMember(GetService(\"KliveBotDiscord\"), \"Client\"); foreach (var m in GetObjectMembers(client, \"Guild\", \"method\")) Log(m.Signature);");
            sb.AppendLine("// Read a property/field on a live object:");
            sb.AppendLine("var mem = GetObjectMember(GetService(\"KliveAgent\"), \"Memory\");");
            sb.AppendLine("// Get all KliveAgent memories:");
            sb.AppendLine("var all = await CallObjectMethod(GetObjectMember(GetService(\"KliveAgent\"), \"Memory\"), \"GetAllMemoriesAsync\"); Log(((System.Collections.ICollection)all).Count.ToString());");
            sb.AppendLine("// List all running services with name + uptime (ListServices() already filters to active):");
            sb.AppendLine("foreach (var s in ListServices()) Log($\"{s.TypeName}/{s.Name} up={s.Uptime}\");");
            sb.AppendLine("// Recent errors from OmniLogging — overallMessages is the source of truth, type==Error means error:");
            sb.AppendLine("var recent = GetRecentErrors(10); foreach (var line in recent) Log(line);");
            sb.AppendLine("// Save TWO (or more) memories in ONE block and capture the returned ids:");
            sb.AppendLine("var idA = await SaveMemory(\"fact A\", new[]{\"tag\"}); var idB = await SaveMemory(\"fact B\", new[]{\"tag\"}); Log($\"a={idA} b={idB}\");");
            sb.AppendLine("// Find a symbol when location is UNKNOWN (search codebase, then read the file at the line):");
            sb.AppendLine("Log(SearchCode(\"Bm25Score\", \"Omnipotent/Services/KliveAgent\")); // returns file:line matches");
            sb.AppendLine("// Search inside ONE known file (subfolder = file path):");
            sb.AppendLine("Log(SearchCode(\"BM25\", \"Omnipotent/Services/KliveAgent/KliveAgentMemory.cs\"));");
            sb.AppendLine("// List private static METHODS in a file (NOT fields — the negative lookahead skips `private static readonly`):");
            sb.AppendLine("Log(SearchCodeRegex(@\"^\\s*private\\s+static\\s+(?!readonly)[\\w<>?,\\s\\[\\]]+\\s+\\w+\\s*\\(\", \"Omnipotent/Services/KliveAgent/KliveAgentBrain.cs\"));");
            sb.AppendLine("// Find every .cs FILE matching a name pattern under a subfolder (filename-only, no content scan):");
            sb.AppendLine("Log(FindFiles(\"*Routes*.cs\", \"Omnipotent/Services\"));");
            sb.AppendLine("// Count + list PUBLIC INSTANCE methods of a class (GetTypeSchema returns only public methods):");
            sb.AppendLine("var sch = GetTypeSchema(\"ScriptGlobals\"); var inst = sch.Methods.Where(m => !m.IsStatic).ToList(); Log($\"{inst.Count} pub instance methods. First 3: {string.Join(\\\", \\\", inst.Take(3).Select(m => m.Name))}\");");
            sb.AppendLine("// Read a known file directly (user named it):");
            sb.AppendLine("Log(ReadFile(\"Omnipotent/Services/KliveAgent/KliveAgentBrain.cs\", startLine: 1, maxLines: 250));");
            sb.AppendLine("// Filter memories by exact tag (instead of full-text search):");
            sb.AppendLine("foreach (var m in await RecallMemoriesByTag(\"preferences\")) Log($\"{m.Id.Substring(0,8)} {m.Content}\");");
            sb.AppendLine("// Get today's run-time stats (no codebase search needed):");
            sb.AppendLine("var st = GetAgentStats(); Log(System.Text.Json.JsonSerializer.Serialize(st));");
            sb.AppendLine("// Discover an unknown object's shape (when you don't remember the field names):");
            sb.AppendLine("var info = GetService(\"Omniscience\"); Log(System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions{WriteIndented=true,MaxDepth=3}));");
            sb.AppendLine("// MULTI-STEP PIPELINE in ONE script (output of step N → input of step N+1, with logs at each gate):");
            sb.AppendLine("var errs = GetRecentErrors(50);");
            sb.AppendLine("if (errs.Count == 0) { Log(\"no errors today\"); return; } // empty-premise short-circuit");
            sb.AppendLine("var groups = errs.Select(e => System.Text.RegularExpressions.Regex.Match(e, @\"Omnipotent\\.Services\\.([\\w\\.]+)\").Groups[1].Value)");
            sb.AppendLine("    .Where(s => !string.IsNullOrEmpty(s)).GroupBy(s => s).OrderByDescending(g => g.Count()).ToList();");
            sb.AppendLine("var topSvc = groups.First().Key; Log($\"step1 topSvc={topSvc} count={groups.First().Count()}\");");
            sb.AppendLine("var files = FindFiles($\"*{topSvc.Split('.').Last()}*.cs\", \"Omnipotent/Services\");");
            sb.AppendLine("Log($\"step2 files={files.Count} first={files.FirstOrDefault()}\");");
            sb.AppendLine("if (files.Count > 0) { var src = ReadFile(files[0]); var n = System.Text.RegularExpressions.Regex.Matches(src, @\"\\bcatch\\b\").Count; Log($\"step3 catches={n}\"); }");
            sb.AppendLine("// CHAINED MEMORY SAVE (later step references id from earlier step in SAME block):");
            sb.AppendLine("var anchorId = await SaveMemory(\"anchor content\", new[]{\"anchor\"});");
            sb.AppendLine("var refId = await SaveMemory($\"references {anchorId}\", new[]{\"reference\"}); Log($\"anchor={anchorId} ref={refId}\");");
            sb.AppendLine();

            sb.AppendLine("[Memory Discipline]");
            sb.AppendLine("Memory is your long-term knowledge of reality across conversations. Treat it like human memory.");
            sb.AppendLine("DO save (call SaveMemory): durable facts about Klive, about yourself, about how Omnipotent actually works,");
            sb.AppendLine("non-obvious recipes for using a service, things Klive explicitly tells you to remember.");
            sb.AppendLine("DO NOT save: a record that you just answered a question, summaries of what you did this turn,");
            sb.AppendLine("greetings, jokes, transient state, or anything already obvious from the conversation.");
            sb.AppendLine("If a memory shown in [Memories & Shortcuts] is junk (a per-turn task changelog, an outdated belief,");
            sb.AppendLine("a duplicate), call DeleteMemory(id) to forget it. Curate aggressively — fewer, better memories beat many noisy ones.");
            sb.AppendLine();

            sb.Append(BuildToolGuide(userMessage));

            if (!string.IsNullOrWhiteSpace(repoMap))
            {
                sb.AppendLine();
                sb.Append(repoMap);
            }

            if (!string.IsNullOrWhiteSpace(memoriesSection))
            {
                sb.AppendLine();
                sb.Append(memoriesSection);
            }

            // No hard truncation here: the system prompt is composed of bounded, deliberate
            // sections (slim personality + short rule block + tool names + budgeted repo map
            // + budgeted memories). A blanket truncate would silently cut tools or memories,
            // which is a capability loss. If a section is too big, lower its own budget.
            return sb.ToString();
        }
        public static List<ResponseSegment> ParseLLMResponse(string response)
        {
            var segments = new List<ResponseSegment>();
            if (string.IsNullOrEmpty(response)) return segments;

            // Primary delimiter: {{{ ... }}}
            // Fallback delimiter: ```csharp ... ``` (or plain ``` ... ```)
            // Both are matched in a single pass by alternation so their relative order is preserved.
            var pattern = @"\{\{\{(.*?)\}\}\}|```(?:csharp|cs)?\s*\n(.*?)```";
            var matches = Regex.Matches(response, pattern, RegexOptions.Singleline);

            int lastIndex = 0;
            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    var text = response.Substring(lastIndex, match.Index - lastIndex).Trim();
                    if (!string.IsNullOrEmpty(text))
                        segments.Add(new ResponseSegment { IsScript = false, Content = text });
                }

                // Group 1 = {{{ }}} capture, Group 2 = ``` ``` capture
                var code = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
                if (!string.IsNullOrEmpty(code))
                    segments.Add(new ResponseSegment { IsScript = true, Content = code });

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < response.Length)
            {
                var text = response.Substring(lastIndex).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    // Tolerant fallback: the LLM sometimes drops one closing brace and emits
                    // "{{{ ...code... }}" instead of "{{{ ...code... }}}". Recover the script.
                    var openIdx = text.IndexOf("{{{", StringComparison.Ordinal);
                    if (openIdx >= 0 && !text.AsSpan(openIdx).Contains("}}}".AsSpan(), StringComparison.Ordinal))
                    {
                        var prefix = text.Substring(0, openIdx).Trim();
                        if (!string.IsNullOrEmpty(prefix))
                            segments.Add(new ResponseSegment { IsScript = false, Content = prefix });
                        var inner = text.Substring(openIdx + 3).TrimEnd();
                        // Strip up to two trailing close-braces if present (handles "}}" / "}" tails).
                        if (inner.EndsWith("}}")) inner = inner.Substring(0, inner.Length - 2).TrimEnd();
                        else if (inner.EndsWith("}")) inner = inner.Substring(0, inner.Length - 1).TrimEnd();
                        if (!string.IsNullOrEmpty(inner))
                            segments.Add(new ResponseSegment { IsScript = true, Content = inner });
                    }
                    else
                    {
                        segments.Add(new ResponseSegment { IsScript = false, Content = text });
                    }
                }
            }

            return segments;
        }
        // No hard iteration cap. The loop self-terminates when the LLM produces a no-script
        // reply (final answer) or when the stuck-detector trips on truly looping behaviour
        // (same error 3x or same script body run twice). This lets KliveAgent run arbitrarily
        // long, complex tasks without an artificial ceiling on its cognition.
        private const int StuckErrorThreshold = 3;
        private const int StuckScriptRepeatThreshold = 2;

        /// <summary>How many of the most recent agent turns replay their scripts+outputs into the
        /// conversation history. Recent turns are where "what did I just run / what did it return"
        /// matters most; older turns stay text-only to keep prompt cost bounded.</summary>
        private const int HistoryScriptRecentTurns = 3;

        public async Task<AgentChatResponse> ProcessMessageAsync(
            string userMessage,
            AgentConversation conversation,
            string? senderName = null,
            Action<string, List<AgentScriptResult>>? onProgress = null)
        {
            try
            {
                var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();
                // Accumulates the agent's conversational prose across iterations so the user can be
                // shown it "talking" (via onProgress) while its scripts are still running.
                var progressText = new StringBuilder();
                var systemPrompt = await BuildSystemPrompt(userMessage, conversation);
                var llmSessionId = $"kliveagent-{conversation.ConversationId}";

                var llmServices = await agentService.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                {
                    return new AgentChatResponse
                    {
                        ConversationId = conversation.ConversationId,
                        Response = "My brain (KliveLLM) isn't running right now. I'm essentially lobotomized. Try again later.",
                        Success = false,
                        ErrorMessage = "KliveLLM service not available."
                    };
                }

                var llm = (KliveLLM.KliveLLM)llmServices[0];
                llm.ResetSession(llmSessionId);

                var allScriptsExecuted = new List<AgentScriptResult>();
                var sharedGlobals = new ScriptGlobals(agentService);
                var scriptSession = scriptEngine.CreateSession(sharedGlobals);
                int totalPromptTokens = 0;
                int totalCompletionTokens = 0;
                int iterationsDone = 0;

                // First iteration: pass system prompt so KliveLLM sets it as the system role (not stuffed into the user turn).
                // Subsequent iterations only send observations — the session retains the system message.
                var currentPrompt = BuildUserPrompt(conversation, userMessage, senderName);
                string? firstIterationSystemPrompt = systemPrompt;
                var errorFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
                var scriptFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
                bool stuckForceFinal = false;
                bool finalFormatRetryUsed = false;
                int consecutiveFailedScripts = 0;
                int consecutiveNoOpResponses = 0;

                // Streams the agent's current prose + the scripts it has run so far to the live
                // progress channel, so the UI shows it talking AND shows the code as it executes
                // (replacing the old static "I'm on it…" placeholder).
                void ReportProgress(string runningNote)
                {
                    if (onProgress == null) return;
                    var shown = progressText.Length > 0 ? progressText.ToString() : "On it.";
                    var body = string.IsNullOrWhiteSpace(runningNote) ? shown : $"{shown}\n\n{runningNote}";
                    try { onProgress(body, new List<AgentScriptResult>(allScriptsExecuted)); } catch { }
                }

                // ── Agentic Loop: Think → Script → Observe → repeat ──
                // No iteration cap. The loop ends when the LLM produces a final text-only
                // answer, or when the stuck-detector trips (same error 3x, or same script
                // body re-run). This lets the agent take as many steps as a complex task
                // genuinely requires without an artificial ceiling on its cognition.
                for (int iteration = 0; ; iteration++)
                {
                    iterationsDone = iteration + 1;

                    KliveLLM.KliveLLM.KliveLLMResponse llmResponse;
                    try
                    {
                        // Pass system prompt only on iteration 0 so it is set as the LLM session's
                        // system role message once — not re-injected into every user turn.
                        llmResponse = await llm.QueryLLM(currentPrompt, llmSessionId,
                            systemPrompt: firstIterationSystemPrompt);
                        firstIterationSystemPrompt = null; // don't resend
                    }
                    catch (Exception llmEx)
                    {
                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = $"LLM query failed: {llmEx.Message}",
                            Success = false,
                            ErrorMessage = llmEx.ToString()
                        };
                    }

                    if (llmResponse == null || !llmResponse.Success)
                    {
                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                            allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                            turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel);
                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = "Something went wrong with my brain. " + (llmResponse?.ErrorMessage ?? "LLM returned null."),
                            Success = false,
                            ErrorMessage = llmResponse?.ErrorMessage ?? "LLM returned null response."
                        };
                    }

                    totalPromptTokens += llmResponse.PromptTokens;
                    totalCompletionTokens += llmResponse.CompletionTokens;

                    var segments = ParseLLMResponse(llmResponse.Response ?? "");
                    var hasScripts = segments.Any(s => s.IsScript);

                    // Talk while working: push the agent's conversational prose to the live progress
                    // channel the moment we see it, before the scripts in this turn execute. The user
                    // reads "On it — pulling that now…" while the data-gathering runs in the background.
                    if (hasScripts && onProgress != null)
                    {
                        var thought = string.Join("\n", segments.Where(s => !s.IsScript)
                            .Select(s => s.Content)).Trim();
                        if (thought.Length > 0)
                        {
                            if (progressText.Length > 0) progressText.AppendLine().AppendLine();
                            progressText.Append(thought);
                        }
                        int pendingScriptCount = segments.Count(s => s.IsScript);
                        ReportProgress($"_…running {pendingScriptCount} script{(pendingScriptCount == 1 ? "" : "s")} (step {iteration + 1})_");
                    }

                    // Detect Anthropic/OpenAI-style tool-call XML or JSON in the raw output — the
                    // model thinks it's calling a tool but the parser will never see it. After 2
                    // such turns in a row, stop the loop.
                    bool looksLikeXmlToolCall = !hasScripts
                        && Regex.IsMatch(llmResponse.Response ?? string.Empty,
                            @"<\s*(function|tool_use|tool_call|parameters|invoke)\b", RegexOptions.IgnoreCase);
                    if (looksLikeXmlToolCall) consecutiveNoOpResponses++;
                    else consecutiveNoOpResponses = 0;
                    if (consecutiveNoOpResponses >= 2) stuckForceFinal = true;

                    // No scripts â†’ this is the final answer
                    if (!hasScripts)
                    {
                        var finalText = string.Join("\n", segments
                            .Where(s => !s.IsScript)
                            .Select(s => s.Content)).Trim();

                        // Guard: model sometimes emits C# code as plain text (unbalanced ``` fence,
                        // or naked tool calls like "ReadFile(...)") thinking it's a final answer.
                        // Re-prompt once to force a real final reply or a properly-fenced script.
                        if (!finalFormatRetryUsed && !stuckForceFinal && LooksLikeUnexecutedScript(finalText))
                        {
                            finalFormatRetryUsed = true;
                            currentPrompt = "[Format error] Your last reply was not a real final answer. Either it (a) contained C# code as plain text instead of an executable {{{ ... }}} block, (b) was a stub like 'Let me get X' or 'I'll now Y' that promises to act but contains no answer, or (c) was an XML/JSON tool envelope. "
                                + "Pick ONE: actually run the script wrapped in {{{ ... }}} so it executes, OR write the real text-only final answer with NO code, NO fences, NO tool names, NO 'Let me X'. "
                                + "Do not stall. Do not promise. Either act or answer.";
                            continue;
                        }

                        llm.ResetSession(llmSessionId);

                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                        conversation.Messages.Add(new AgentMessage
                        {
                            Role = AgentMessageRole.Agent,
                            Content = finalText,
                            // Persist the code KliveAgent wrote+ran this turn (and its outputs) so it
                            // can see its own prior actions on later turns and the UI can replay them.
                            ScriptResults = allScriptsExecuted.Count > 0
                                ? new List<AgentScriptResult>(allScriptsExecuted)
                                : null
                        });
                        conversation.LastUpdated = DateTime.UtcNow;

                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                            allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                            turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel);

                        // NOTE: We deliberately do NOT auto-save "task completed" summaries here.
                        // Memory is for durable facts about reality (who Klives is, how a service
                        // actually behaves, things the user has told the agent about itself or its
                        // world) — NOT a per-turn changelog. Saving every completed task created
                        // prompt bloat with information the conversation history already carries.
                        // The agent decides what's worth remembering by calling SaveMemory() itself.

                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = finalText,
                            ScriptsExecuted = allScriptsExecuted,
                            Success = true,
                            PromptTokens = totalPromptTokens,
                            CompletionTokens = totalCompletionTokens,
                            Iterations = iterationsDone
                        };
                    }

                    // Execute scripts → collect structured observations
                    var observationSb = new StringBuilder();
                    observationSb.AppendLine("[Script Observations]");

                    int scriptCountThisIter = 0;
                    int errorCountThisIter = 0;

                    foreach (var segment in segments)
                    {
                        if (!segment.IsScript)
                        {
                            if (!string.IsNullOrWhiteSpace(segment.Content))
                                observationSb.AppendLine($"[Agent thought] {segment.Content.Trim()}");
                            continue;
                        }

                        scriptCountThisIter++;

                        // Track repeated script bodies (a key stuck-loop signal).
                        var scriptKey = NormaliseForFingerprint(segment.Content ?? string.Empty);
                        if (!string.IsNullOrEmpty(scriptKey))
                        {
                            scriptFrequency.TryGetValue(scriptKey, out var prev);
                            scriptFrequency[scriptKey] = prev + 1;
                            if (scriptFrequency[scriptKey] >= StuckScriptRepeatThreshold)
                                stuckForceFinal = true;
                        }

                        var result = await scriptSession.ExecuteAsync(segment.Content ?? string.Empty);
                        allScriptsExecuted.Add(result);

                        // Stream the just-completed script (code + output) to the UI as it lands.
                        ReportProgress($"_…ran {allScriptsExecuted.Count} script{(allScriptsExecuted.Count == 1 ? "" : "s")} so far (step {iteration + 1})_");

                        if (result.Success)
                        {
                            var output = KliveAgentContextBudget.TruncateToTokens(
                                result.Output ?? string.Empty,
                                KliveAgentContextBudget.ScriptOutputBudget);

                            observationSb.AppendLine($"[OK | {result.ExecutionTimeMs}ms]");
                            if (!string.IsNullOrWhiteSpace(output))
                                observationSb.AppendLine(output);
                        }
                        else
                        {
                            errorCountThisIter++;
                            var errMsg = result.ErrorMessage ?? "Unknown error.";
                            var errKey = NormaliseForFingerprint(errMsg);
                            if (!string.IsNullOrEmpty(errKey))
                            {
                                errorFrequency.TryGetValue(errKey, out var prev);
                                errorFrequency[errKey] = prev + 1;
                                if (errorFrequency[errKey] >= StuckErrorThreshold)
                                    stuckForceFinal = true;
                            }

                            observationSb.AppendLine($"[ERROR | {result.ExecutionTimeMs}ms]");
                            observationSb.AppendLine(KliveAgentContextBudget.TruncateToTokens(errMsg, 300));
                            consecutiveFailedScripts++;
                            if (consecutiveFailedScripts >= 5) stuckForceFinal = true;
                        }

                        if (result.Success) consecutiveFailedScripts = 0;

                        observationSb.AppendLine();
                    }

                    // Soft nudge once the task has gone deep — informational, not a stop.
                    if (iteration + 1 == 12 && !stuckForceFinal)
                    {
                        observationSb.AppendLine();
                        observationSb.AppendLine("[Nudge] You've taken 12 steps on this. If you're making real progress, keep going. " +
                            "If you're spiralling, consider SaveMemory(\"...\") for what you've learned and giving the final answer.");
                    }

                    // Hard safety cap: prevent runaway loops that never produce a final answer.
                    // After 30 iterations, force the final-answer path. Real questions that
                    // need >30 scripted steps should be split by the user; the agent should
                    // not hang an API request indefinitely (exposed as a Tier-5 hang bug).
                    if (iteration + 1 >= 30 && !stuckForceFinal)
                    {
                        stuckForceFinal = true;
                    }

                    // Feed observations back as the next prompt turn. System prompt + prior turns
                    // already live in the LLM session — do not re-inject them.
                    if (stuckForceFinal)
                    {
                        currentPrompt = observationSb.ToString().TrimEnd()
                            + "\n\n[Stuck-loop detected] You repeated a script or hit the same error 3+ times. "
                            + "STOP scripting. Reply with a final text-only answer that honestly reports what you tried, what worked, and what blocked you.";
                    }
                    else
                    {
                        currentPrompt = observationSb.ToString().TrimEnd()
                            + "\n\nIf you have what you need, give the final answer now (no scripts). "
                            + "Otherwise write your next script — prefer ONE composite block over several tiny ones.";
                    }
                }

                // Loop is unbounded; this point is unreachable in practice (the loop only
                // exits via the no-script final-answer return above). Kept here so the
                // catch-all exception path below stays valid C#.
                llm.ResetSession(llmSessionId);
                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = "[Internal] Agent loop exited unexpectedly.",
                    ScriptsExecuted = allScriptsExecuted,
                    Success = false,
                    PromptTokens = totalPromptTokens,
                    CompletionTokens = totalCompletionTokens,
                    Iterations = iterationsDone
                };
            }
            catch (Exception ex)
            {
                await agentService.ServiceLogError(ex, "KliveAgentBrain.ProcessMessageAsync");
                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = $"Something broke in my brain: {ex.Message}",
                    Success = false,
                    ErrorMessage = ex.ToString()
                };
            }
        }

        // â”€â”€ Conversation Prompt Builder â”€â”€

        /// <summary>
        /// Builds the user-turn portion of the prompt (history + new message).
        /// The system prompt is no longer embedded here — it is passed separately to KliveLLM
        /// so it lands in the 'system' role of the chat, not the 'user' role.
        /// </summary>
        private string BuildUserPrompt(
            AgentConversation conversation,
            string userMessage,
            string? senderName = null)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(senderName))
            {
                sb.AppendLine($"[Current User: {senderName}]");
                sb.AppendLine($"Channel: {conversation.SourceChannel}");
                sb.AppendLine();
            }

            // Budget-aware conversation history selection
            if (conversation.Messages.Count > 0)
            {
                var historyMessages = SelectHistoryMessages(
                    conversation.Messages, userMessage, KliveAgentContextBudget.HistoryBudget);

                if (historyMessages.Count > 0)
                {
                    // Compaction: turns that fall before the retained window are summarised into a
                    // short synopsis rather than silently dropped, so the earlier thread survives.
                    var earlierSummary = BuildEarlierSummary(conversation.Messages, historyMessages,
                        KliveAgentContextBudget.HistorySummaryBudget);
                    if (!string.IsNullOrWhiteSpace(earlierSummary))
                    {
                        sb.AppendLine("[Earlier Conversation Summary]");
                        sb.AppendLine(earlierSummary);
                        sb.AppendLine();
                    }

                    sb.AppendLine("[Conversation History]");

                    // Only the most recent agent turns replay their scripts+outputs (higher fidelity
                    // where it matters; older turns stay text-only to bound prompt cost).
                    var scriptCarryingTurns = historyMessages
                        .Where(m => m.Role == AgentMessageRole.Agent
                            && m.ScriptResults != null && m.ScriptResults.Count > 0)
                        .TakeLast(HistoryScriptRecentTurns)
                        .ToHashSet();

                    foreach (var msg in historyMessages)
                    {
                        var role = msg.Role == AgentMessageRole.User ? "User" : "KliveAgent";
                        sb.AppendLine($"{role}: {msg.Content}");

                        if (scriptCarryingTurns.Contains(msg))
                        {
                            var scripts = FormatHistoryScripts(msg, KliveAgentContextBudget.HistoryScriptBudget);
                            if (!string.IsNullOrWhiteSpace(scripts))
                                sb.AppendLine(scripts);
                        }
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("[New Message]");
            sb.AppendLine(string.IsNullOrEmpty(senderName) ? $"User: {userMessage}" : $"{senderName}: {userMessage}");

            return sb.ToString();
        }

        private static List<AgentMessage> SelectHistoryMessages(
            List<AgentMessage> messages,
            string currentQuery,
            int maxTokens)
        {
            if (messages.Count == 0) return new List<AgentMessage>();

            var pairs = new List<(int idx, AgentMessage user, AgentMessage? agent, double score)>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role != AgentMessageRole.User) continue;
                var agent = (i + 1 < messages.Count && messages[i + 1].Role == AgentMessageRole.Agent)
                    ? messages[i + 1] : null;

                var indexFromEnd = messages.Count - i;
                var score = KliveAgentContextBudget.ScoreMessage(
                    messages[i].Content + " " + (agent?.Content ?? ""),
                    currentQuery,
                    indexFromEnd);

                pairs.Add((i, messages[i], agent, score));
            }

            var usedTokens = 0;
            var selected = new List<(int idx, AgentMessage user, AgentMessage? agent)>();

            foreach (var (idx, user, agent, _) in pairs.OrderByDescending(p => p.score))
            {
                var cost = KliveAgentContextBudget.EstimateTokens(
                    user.Content + " " + (agent?.Content ?? ""));
                if (usedTokens + cost > maxTokens) continue;
                usedTokens += cost;
                selected.Add((idx, user, agent));
            }

            return selected
                .OrderBy(x => x.idx)
                .SelectMany(x => x.agent != null
                    ? new[] { x.user, x.agent }
                    : new[] { x.user })
                .ToList();
        }

        /// <summary>
        /// Builds a compacted, token-budgeted synopsis of the conversation turns that fall *before*
        /// the retained history window. Keeps the earlier thread alive without paying full token cost.
        /// </summary>
        private static string BuildEarlierSummary(List<AgentMessage> allMessages, List<AgentMessage> retained, int budget)
        {
            if (allMessages.Count == 0 || retained.Count == 0) return string.Empty;

            // Index of the earliest retained message; everything before it is "earlier".
            int firstRetainedIdx = int.MaxValue;
            foreach (var m in retained)
            {
                var idx = allMessages.IndexOf(m);
                if (idx >= 0 && idx < firstRetainedIdx) firstRetainedIdx = idx;
            }
            if (firstRetainedIdx is int.MaxValue or 0) return string.Empty;

            // Collect earlier user→agent turn pairs as truncated one-liners (oldest first).
            var earlierTurns = new List<string>();
            for (int i = 0; i < firstRetainedIdx; i++)
            {
                if (allMessages[i].Role != AgentMessageRole.User) continue;
                var user = allMessages[i];
                var agent = (i + 1 < firstRetainedIdx && allMessages[i + 1].Role == AgentMessageRole.Agent)
                    ? allMessages[i + 1] : null;
                earlierTurns.Add($"- {TruncateForMemory(user.Content ?? string.Empty, 90)}"
                    + (agent != null ? $" → {TruncateForMemory(agent.Content ?? string.Empty, 90)}" : string.Empty));
            }
            if (earlierTurns.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            int used = 0;
            for (int i = 0; i < earlierTurns.Count; i++)
            {
                var cost = KliveAgentContextBudget.EstimateTokens(earlierTurns[i]);
                if (used + cost > budget)
                {
                    sb.AppendLine($"- (+{earlierTurns.Count - i} earlier turn(s) omitted)");
                    break;
                }
                sb.AppendLine(earlierTurns[i]);
                used += cost;
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Renders the scripts an agent turn ran (and their outputs/errors) into a compact,
        /// token-budgeted block for replay in the conversation history. This is what lets
        /// KliveAgent see "the code I wrote last turn and what it returned" on a fresh turn.
        /// </summary>
        private static string FormatHistoryScripts(AgentMessage msg, int perTurnBudget)
        {
            if (msg.ScriptResults == null || msg.ScriptResults.Count == 0) return string.Empty;

            // Share the per-turn budget across this turn's scripts so a single turn can't blow it.
            var perScript = Math.Max(80, perTurnBudget / Math.Max(1, msg.ScriptResults.Count));

            var sb = new StringBuilder();
            sb.AppendLine("  [KliveAgent ran this turn]");
            foreach (var sr in msg.ScriptResults)
            {
                var code = KliveAgentContextBudget.TruncateToTokens(sr.Code ?? string.Empty, perScript).Trim();
                if (!string.IsNullOrWhiteSpace(code))
                    sb.AppendLine($"  script: {code}");

                if (sr.Success)
                {
                    var outp = KliveAgentContextBudget.TruncateToTokens(sr.Output ?? string.Empty, perScript).Trim();
                    sb.AppendLine(string.IsNullOrWhiteSpace(outp) ? "  -> [OK, no output]" : $"  -> {outp}");
                }
                else
                {
                    var err = KliveAgentContextBudget.TruncateToTokens(sr.ErrorMessage ?? "Unknown error.", 100).Trim();
                    sb.AppendLine($"  -> [ERROR] {err}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildToolGuide(string userMessage)
        {
            var sb = new StringBuilder();

            sb.AppendLine("[Tools] (public methods on ScriptGlobals — call by name)");

            AppendToolNames(sb, "Core", StarterTools);

            if (ShouldIncludeDiscoveryTools(userMessage))
                AppendToolNames(sb, "Codebase", DiscoveryTools);

            if (ShouldIncludeAdvancedRuntimeTools(userMessage))
                AppendToolNames(sb, "Runtime", AdvancedRuntimeTools);

            if (ShouldIncludeMemoryTools(userMessage))
                AppendToolNames(sb, "Memory", MemoryTools);

            sb.AppendLine("If a tool you need isn't listed, run: GetTypeSchema(\"ScriptGlobals\") to see every tool, or GetMethodDocumentation(\"ScriptGlobals\", \"ToolName\") for one signature.");

            return sb.ToString().TrimEnd();
        }

        private static void AppendToolNames(StringBuilder sb, string title, IEnumerable<PromptToolDescriptor> tools)
        {
            sb.Append(title).Append(": ");
            sb.AppendLine(string.Join("; ", tools.Select(t => $"{t.MethodName} — {t.Description}")));
        }

        private static string? FormatToolSignature(string methodName)
        {
            var method = typeof(ScriptGlobals)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.Ordinal));

            if (method == null)
                return null;

            var parameters = string.Join(", ", method.GetParameters().Select(FormatParameterSignature));
            return $"{method.Name}({parameters}) -> {FormatTypeName(method.ReturnType)}";
        }

        private static string FormatParameterSignature(ParameterInfo parameter)
        {
            var prefix = parameter.GetCustomAttribute<ParamArrayAttribute>() != null ? "params " : string.Empty;
            var signature = $"{prefix}{FormatTypeName(parameter.ParameterType)} {parameter.Name}";

            if (parameter.HasDefaultValue)
                signature += $" = {FormatDefaultValue(parameter.DefaultValue)}";

            return signature;
        }

        private static string FormatTypeName(Type type)
        {
            if (type == typeof(void))
                return "void";

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
                return $"{FormatTypeName(nullable)}?";

            if (type.IsArray)
                return $"{FormatTypeName(type.GetElementType()!)}[]";

            if (type.IsGenericType)
            {
                var genericName = type.Name;
                var tickIndex = genericName.IndexOf('`');
                if (tickIndex >= 0)
                    genericName = genericName[..tickIndex];

                var genericArguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
                return $"{genericName}<{genericArguments}>";
            }

            return type.Name switch
            {
                nameof(String) => "string",
                nameof(Int32) => "int",
                nameof(Int64) => "long",
                nameof(UInt32) => "uint",
                nameof(UInt64) => "ulong",
                nameof(Int16) => "short",
                nameof(UInt16) => "ushort",
                nameof(Boolean) => "bool",
                nameof(Object) => "object",
                nameof(Double) => "double",
                nameof(Single) => "float",
                nameof(Decimal) => "decimal",
                nameof(Byte) => "byte",
                nameof(SByte) => "sbyte",
                nameof(Char) => "char",
                _ => type.Name,
            };
        }

        private static string FormatDefaultValue(object? value)
        {
            return value switch
            {
                null => "null",
                string text => $"\"{text}\"",
                char character => $"'{character}'",
                bool boolean => boolean ? "true" : "false",
                Enum enumValue => $"{enumValue.GetType().Name}.{enumValue}",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            };
        }

        private static bool ShouldIncludeDiscoveryTools(string userMessage)
        {
            return KliveAgentRepoMap.ExtractSeedsFromText(userMessage).Count > 0
                || ContainsAny(userMessage, "code", "class", "method", "file", "repo", "source", "implementation", "where", "why", "how");
        }

        private static bool ShouldIncludeAdvancedRuntimeTools(string userMessage)
        {
            return ContainsAny(userMessage, "service", "capability", "background", "task", "property", "field", "member", "invoke", "call", "execute");
        }

        private static bool ShouldIncludeMemoryTools(string userMessage)
        {
            return ContainsAny(userMessage, "memory", "memories", "remember", "recall", "shortcut");
        }

        private static bool ContainsAny(string userMessage, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return false;

            return keywords.Any(keyword => userMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static string TruncateForMemory(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s[..max] + "…";
        }

        /// <summary>
        /// Reduces a script body or error string to a stable fingerprint for stuck-loop
        /// detection: trims, collapses whitespace, strips most punctuation, lowercases,
        /// and truncates. Two scripts that differ only in formatting will hash to the
        /// same key, two genuinely different scripts will not.
        /// </summary>
        private static string NormaliseForFingerprint(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            bool lastSpace = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastSpace = false;
                }
            }
            var trimmed = sb.ToString().Trim();
            return trimmed.Length <= 240 ? trimmed : trimmed[..240];
        }

        /// <summary>
        /// Heuristic: does this "final answer" text actually look like it's secretly C# code
        /// (or a half-broken code fence) that the agent meant to execute but didn't wrap?
        /// We trip on: unbalanced ``` fence count, or several lines that look like tool
        /// calls / variable declarations / awaited Tasks.
        /// </summary>
        private static bool LooksLikeUnexecutedScript(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            // Stray opening triple-brace anywhere in a final reply means the LLM tried to emit
            // a script but mismatched the closer (e.g. "{{{ ... }}" instead of "{{{ ... }}}").
            if (text.Contains("{{{", StringComparison.Ordinal))
                return true;

            // XML/JSON tool-call envelope (Claude/OpenAI style) — always wrong here.
            if (Regex.IsMatch(text, @"<\s*(function|tool_use|tool_call|parameters|invoke)\b", RegexOptions.IgnoreCase))
                return true;

            // Stub final: a short reply that promises to do something next ("Let me get/find/check X")
            // instead of containing the actual answer. The LLM mistook a script preamble for a final reply.
            var trimmed = text.Trim();
            if (trimmed.Length <= 400
                && Regex.IsMatch(trimmed,
                    @"\b(let me (get|find|check|call|fetch|look|see|grab|pull|inspect|query|search)|i'?ll (now|first|just|go) (get|find|check|call|fetch|look|see|grab|pull|inspect|query|search|run|try)|going to (get|find|check|call|fetch|look|see|grab|pull|inspect|query|search|run|try))\b",
                    RegexOptions.IgnoreCase))
                return true;

            // Unbalanced triple-backtick → there's a fence opener or closer dangling.
            // Only flag when there are 3+ fences (so a single literal "```" inside an answer
            // explaining markdown fences doesn't trip the heuristic).
            int fenceCount = 0;
            for (int i = 0; i + 2 < text.Length; i++)
            {
                if (text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
                {
                    fenceCount++;
                    i += 2;
                }
            }
            if (fenceCount >= 3 && (fenceCount & 1) == 1) return true;

            // Count lines that look like raw C# tool calls / declarations.
            // Skip lines that are clearly prose (start with bullet markers, numbers, common English words),
            // or quoted (inside backticks). Only a leading bare identifier in caller position counts.
            var lines = text.Split('\n');
            int codeyLines = 0;
            foreach (var raw in lines)
            {
                var line = raw.TrimStart();
                if (line.Length == 0) continue;
                // Bullets / numbered list items / blockquotes / table cells are prose, not code.
                if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("> ")
                    || line.StartsWith("|") || Regex.IsMatch(line, @"^\d+[\.\)]\s"))
                    continue;
                // Lines that begin with a backtick are inline-code in markdown — prose-ish.
                if (line.StartsWith("`")) continue;
                if (line.StartsWith("//")) { codeyLines++; continue; }
                if (Regex.IsMatch(line, @"^(var|await|foreach|for|if|return|using|Log|GetService|GetServiceMember|GetTypeSchema|GetTypeInfo|GetObjectMember|CallObjectMethod|ExecuteServiceMethod|ListServices|SearchSymbols|SearchCode|SearchCodeRegex|SearchCodeHybrid|ReadFile|WriteFile|FindFiles|ListDirectory|SaveMemory|RecallMemories|RecallMemoriesByTag|DeleteMemory|GetRecentErrors|GetAgentStats|SaveShortcut|GetShortcuts)\b"))
                    codeyLines++;
                if (line.EndsWith(";") && Regex.IsMatch(line, @"[A-Za-z_]\w*\s*\("))
                    codeyLines++;
            }
            return codeyLines >= 3;
        }
    }

    public class ResponseSegment
    {
        public bool IsScript { get; set; }
        public string? Content { get; set; }
    }
}
