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
            new("CallObjectMethod", "Invoke a method (or property getter) on a live object. Returns Task<object?> — ALWAYS `await` it; it auto-unwraps the called method's own Task. Never use its result without await, or you get a Task object instead of the value."),
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

        public async Task<string> BuildSystemPrompt(string userMessage, AgentConversation conversation, bool toolCallingMode = false)
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

            // Memory is a FIRST-CLASS native tool in tool-calling mode (recall_memories / save_memory / …);
            // in the text-protocol fallback it's the ScriptGlobals C# methods. Reference the right one.
            string recallTool = toolCallingMode ? "the recall_memories tool" : "RecallMemories(query)";
            string recallByTagTool = toolCallingMode ? "the recall_memories_by_tag tool" : "RecallMemoriesByTag";
            string saveTool = toolCallingMode ? "the save_memory tool" : "SaveMemory";

            sb.AppendLine("[Rules]");
            if (toolCallingMode)
            {
                sb.AppendLine("- To inspect or act, CALL THE execute_csharp TOOL with your C# in the 'code' argument. Locals persist across calls within the same turn.");
                sb.AppendLine("- Do NOT wrap code in {{{ ... }}} or markdown fences, and do NOT emit XML tool-call tags. Use the native execute_csharp tool channel only. ONE call can do discovery + action + Log() together.");
            }
            else
            {
                sb.AppendLine("- Write C# inside {{{ ... }}} (or ```csharp fences) to inspect/act. Locals persist across blocks in the same reply.");
                sb.AppendLine("- DO NOT emit XML tool-call tags like <function>, <tool_use>, <parameters>, or any JSON tool envelope. They are NOT parsed. The ONLY way to invoke a tool is C# inside {{{ ... }}}.");
            }
            sb.AppendLine($"- MEMORY-FIRST (do this constantly): before answering ANYTHING that is not fully derivable from the codebase or live services — i.e. anything about Klive, his preferences, the people/projects/plans around him, past decisions, prior conversations, or your own earlier conclusions — you MUST call {recallTool} (or {recallByTagTool}) FIRST, even if you think you already know. Recall is a cheap reflex; a guessed or forgotten answer is not. Codebase = source of truth for code; MEMORY = source of truth for everything personal/world/historical. When in any doubt, recall before you answer or before you say 'I don't know'.");
            sb.AppendLine($"- The [Memories & Shortcuts] block below is ONLY the few auto-matched memories, not your whole memory. If the question needs anything beyond what's shown there, call {recallTool} / {recallByTagTool} to search the rest before concluding.");
            sb.AppendLine("- ONE composite script beats many tiny ones. Do discovery + action + Log() in a single block whenever you can. A memory recall fits cheaply inside that same block — fold it in rather than skipping it.");
            sb.AppendLine("- NO FILLER ACKNOWLEDGEMENTS. Never open with throwaway placeholders like \"On it\", \"Sure\", \"Let me…\", or \"Pulling that now\". Only write prose when you have something substantive to tell the user; otherwise just run the tool/script silently — the UI already shows your work executing. Your final reply must be the actual answer, never a stalling acknowledgement.");
            sb.AppendLine("- await Task / Task<T> ONLY. GetTypeSchema, GetService, ListServices, ExecuteServiceMethod (non-async overload), Log are SYNC — do not await.");
            sb.AppendLine("- CallObjectMethod ALWAYS returns Task<object?> — you MUST `await` it; it auto-unwraps the called method's own Task/Task<T> (and property getters) for you. NEVER write `var x = CallObjectMethod(...)` without await, or x is a Task object, not the value (tell-tale: output shows 'System.Threading.Tasks.Task`1[...]').");
            sb.AppendLine("- GetService(name) returns object (sync). To read/call on it: `await CallObjectMethod(GetService(\"X\"), \"Method\", args)`, or `GetObjectMember(GetService(\"X\"), \"Field\")`.");
            sb.AppendLine("- If a script errors, READ the error and change approach. Never retry the same failing code. Compile errors now show the error id, the exact line:col, the offending source line, and a caret (^) under the bad token — fix THAT line; don't blame a different call. Runtime errors show the exception type, inner-cause chain, and stack — read them.");
            sb.AppendLine("- RETURN TYPES: FindFiles, SearchCode, SearchCodeRegex, SearchCodeHybrid, ReadFile, GetRepoMap, GetMethodDocumentation each return ONE formatted string — Log() it directly; NEVER `foreach` over it (iterating a string yields chars → 'cannot convert char to string'). Only ListServices, GetObjectMembers, RecallMemories/ByTag return lists you loop.");
            sb.AppendLine("- DON'T CHASE SOURCE FILES you don't need: once GetTypeSchema/GetObjectMembers have shown you a live object's methods, just call them. Read .cs source only when you genuinely need implementation details the live API can't give you.");
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
            sb.AppendLine("- Final answer = a reply that runs NO scripts (no execute_csharp call and no {{{ }}} block). Keep it punchy. Final replies must contain the actual answer — NEVER finalize with phrases like 'Let me get/find/check/call X' or 'I'll now Y'; those mean you should run another script in the SAME turn.");
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
            sb.AppendLine("Memory is your long-term knowledge of reality across conversations. Treat it like human memory — and CONSULT IT CONSTANTLY, not just when asked to 'remember'.");
            sb.AppendLine($"RECALL FIRST — the default reflex: for ANY question not purely about the codebase (anything about Klive, his preferences, the people/projects/plans around him, past decisions, or your own earlier conclusions), call {recallTool} / {recallByTagTool} BEFORE answering, BEFORE guessing, and BEFORE saying 'I don't know'. Assume a relevant memory may exist and go look; the worst case is one cheap empty result. Skipping recall and confabulating is the cardinal sin. If recall returns nothing, THEN say you don't have it (and consider whether the answer is worth saving once found).");
            sb.AppendLine($"DO save (call {saveTool}): durable facts about Klive, about yourself, about how Omnipotent actually works,");
            sb.AppendLine("non-obvious recipes for using a service, things Klive explicitly tells you to remember.");
            sb.AppendLine("DO NOT save: a record that you just answered a question, summaries of what you did this turn,");
            sb.AppendLine("greetings, jokes, transient state, or anything already obvious from the conversation.");
            sb.AppendLine($"If a memory shown in [Memories & Shortcuts] is junk (a per-turn task changelog, an outdated belief, a duplicate), {(toolCallingMode ? "call the delete_memory tool" : "call DeleteMemory(id)")} to forget it. Curate aggressively — fewer, better memories beat many noisy ones.");
            sb.AppendLine();

            sb.Append(BuildToolGuide(userMessage, toolCallingMode));

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
        // Lines that are pure delimiter/framing noise the model sometimes leaks INTO the code buffer
        // (a stray quote, a Python triple-quote, a YAML "---", leftover markdown fences, or a half
        // brace-delimiter like "{'"). These caused the real "Newline in constant / Invalid expression
        // term '{'" compile failures, so we strip them from both ends of an extracted block.
        private static readonly Regex FramingJunkLine = new(
            @"^\s*(?:'''|""""""|`{3,}\s*(?:csharp|cs)?|'|""|`|-{3,}|\{'|'\}|\{\{\{|\}\}\})\s*$",
            RegexOptions.Compiled);

        private static string StripCodeFraming(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return string.Empty;
            var lines = code.Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[0]) || FramingJunkLine.IsMatch(lines[0])))
                lines.RemoveAt(0);
            while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[^1]) || FramingJunkLine.IsMatch(lines[^1])))
                lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines).Trim();
        }

        /// <summary>
        /// Text-protocol parser (fallback when native tool calling is unavailable). Splits a reply into
        /// prose + executable C# blocks. Hardened vs. the original regex:
        ///   • {{{ ... }}} is extracted brace-DEPTH aware, so a script containing "}}}" (nested closers,
        ///     interpolation) is no longer truncated at the first "}}}".
        ///   • Each extracted block is run through StripCodeFraming to drop leaked delimiter noise.
        ///   • ``` fences are only treated as code when their info string is empty / csharp / cs.
        /// </summary>
        public static List<ResponseSegment> ParseLLMResponse(string response)
        {
            var segments = new List<ResponseSegment>();
            if (string.IsNullOrEmpty(response)) return segments;

            int n = response.Length;
            var prose = new StringBuilder();

            void FlushProse()
            {
                var t = prose.ToString().Trim();
                if (!string.IsNullOrEmpty(t))
                    segments.Add(new ResponseSegment { IsScript = false, Content = t });
                prose.Clear();
            }

            void AddScript(string raw)
            {
                var code = StripCodeFraming(raw);
                if (!string.IsNullOrEmpty(code))
                    segments.Add(new ResponseSegment { IsScript = true, Content = code });
            }

            int i = 0;
            while (i < n)
            {
                // {{{ ... }}} — find the closing triple-brace at C# brace-depth 0 so embedded "}}}" survives.
                if (i + 3 <= n && response[i] == '{' && response[i + 1] == '{' && response[i + 2] == '{')
                {
                    int codeStart = i + 3;
                    int depth = 0;
                    int close = -1;
                    for (int j = codeStart; j < n; j++)
                    {
                        if (depth == 0 && j + 3 <= n && response[j] == '}' && response[j + 1] == '}' && response[j + 2] == '}')
                        { close = j; break; }
                        char c = response[j];
                        if (c == '{') depth++;
                        else if (c == '}' && depth > 0) depth--;
                    }

                    FlushProse();
                    if (close >= 0)
                    {
                        AddScript(response.Substring(codeStart, close - codeStart));
                        i = close + 3;
                    }
                    else
                    {
                        // Tolerant tail: opener with no matching "}}}" — treat the remainder as the script
                        // (StripCodeFraming removes any dangling "}}"/"}" framing line).
                        AddScript(response.Substring(codeStart));
                        i = n;
                    }
                    continue;
                }

                // ```csharp / ```cs / ``` fenced code (only these info strings count as runnable C#).
                if (i + 3 <= n && response[i] == '`' && response[i + 1] == '`' && response[i + 2] == '`')
                {
                    int lineEnd = response.IndexOf('\n', i);
                    if (lineEnd > i)
                    {
                        var info = response.Substring(i + 3, lineEnd - (i + 3)).Trim().ToLowerInvariant();
                        if (info is "" or "csharp" or "cs")
                        {
                            int fenceStart = lineEnd + 1;
                            int close = response.IndexOf("```", fenceStart, StringComparison.Ordinal);
                            if (close >= 0)
                            {
                                FlushProse();
                                AddScript(response.Substring(fenceStart, close - fenceStart));
                                i = close + 3;
                                continue;
                            }
                        }
                    }
                    // Non-C# fence or unterminated → fall through and treat as prose.
                }

                prose.Append(response[i]);
                i++;
            }

            FlushProse();
            return segments;
        }

        /// <summary>Native tool names that are memory operations (dispatched straight to KliveAgentMemory,
        /// NOT through the Roslyn script engine). Anything not in this set is treated as execute_csharp.</summary>
        private static readonly HashSet<string> MemoryToolNames = new(StringComparer.Ordinal)
        {
            "recall_memories", "recall_memories_by_tag", "save_memory", "save_shortcut", "get_shortcuts", "delete_memory"
        };

        /// <summary>
        /// The native tools exposed to the model: execute_csharp (arbitrary C# over the live service graph)
        /// plus first-class MEMORY tools so recall/save are a direct tool call, never a hand-written script.
        /// </summary>
        public static List<HFWrapper.HFTool> BuildToolDefinitions()
        {
            static HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
            {
                type = "function",
                function = new HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
            };

            return new List<HFWrapper.HFTool>
            {
                Tool("execute_csharp",
                    "Execute a C# script in-process against Omnipotent's live service graph (Roslyn). " +
                    "Write plain C# using the ScriptGlobals API (GetService, GetTypeSchema, GetObjectMembers, " +
                    "CallObjectMethod, Log, SearchCode, ReadFile, GetRecentErrors, etc.). " +
                    "Locals persist across calls within the same turn. `await` any Task-returning call. " +
                    "Use Log(...) to return observations. Pass RAW C# in the 'code' argument — do NOT wrap it " +
                    "in {{{ }}} or markdown fences. NOTE: memory has its OWN dedicated tools (recall_memories, " +
                    "save_memory, …) — use those, not execute_csharp, for anything memory-related.",
                    new { type = "object", properties = new { code = new { type = "string", description = "The raw C# script to compile and run." } }, required = new[] { "code" } }),

                Tool("recall_memories",
                    "Search your long-term memory for facts about Klive, his preferences/people/projects/history, " +
                    "past decisions, or your own prior conclusions. Call this FIRST for any question not purely about the codebase.",
                    new { type = "object", properties = new { query = new { type = "string", description = "Free-text search query." }, maxResults = new { type = "integer", description = "Max memories to return (default 10)." } }, required = new[] { "query" } }),

                Tool("recall_memories_by_tag",
                    "Return all memories tagged with an exact tag (case-insensitive). Prefer over recall_memories when filtering by a known tag.",
                    new { type = "object", properties = new { tag = new { type = "string", description = "The exact tag to filter by." } }, required = new[] { "tag" } }),

                Tool("save_memory",
                    "Persist a durable fact about reality (about Klive, yourself, or how a service really behaves). " +
                    "Do NOT save per-turn changelogs, greetings, or transient state.",
                    new { type = "object", properties = new { content = new { type = "string", description = "The fact to remember." }, tags = new { type = "array", items = new { type = "string" }, description = "Optional tags." }, importance = new { type = "integer", description = "1-5 (default 1)." } }, required = new[] { "content" } }),

                Tool("save_shortcut",
                    "Store a reusable recipe (how-to) you discovered for a non-obvious task, so you can skip rediscovery next time.",
                    new { type = "object", properties = new { title = new { type = "string", description = "Short label." }, content = new { type = "string", description = "The step-by-step recipe." }, tags = new { type = "array", items = new { type = "string" }, description = "Optional tags." } }, required = new[] { "title", "content" } }),

                Tool("get_shortcuts",
                    "List all saved shortcuts (reusable recipes).",
                    new { type = "object", properties = new { } }),

                Tool("delete_memory",
                    "Forget a memory by its id (or short-id prefix shown in recalls). Use to curate noise/duplicates/outdated beliefs.",
                    new { type = "object", properties = new { id = new { type = "string", description = "The memory id or short-id prefix." } }, required = new[] { "id" } }),
            };
        }

        /// <summary>
        /// Executes a native MEMORY tool call by dispatching to the live memory API (reusing ScriptGlobals so
        /// stats/side-effects match the script path). Returns a human-readable result string for the tool result.
        /// </summary>
        private static async Task<string> DispatchMemoryToolAsync(ScriptGlobals globals, string toolName, string? argsJson)
        {
            System.Text.Json.JsonElement root = default;
            bool hasArgs = false;
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                try
                {
                    root = System.Text.Json.JsonDocument.Parse(argsJson).RootElement;
                    hasArgs = root.ValueKind == System.Text.Json.JsonValueKind.Object;
                }
                catch { }
            }

            string? Str(string name) => hasArgs && root.TryGetProperty(name, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
            int IntOr(string name, int def) => hasArgs && root.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : def;
            string[]? Strs(string name) => hasArgs && root.TryGetProperty(name, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : null;

            switch (toolName)
            {
                case "recall_memories":
                    return FormatMemoriesResult(await globals.RecallMemories(Str("query") ?? string.Empty, IntOr("maxResults", 10)));
                case "recall_memories_by_tag":
                    return FormatMemoriesResult(await globals.RecallMemoriesByTag(Str("tag") ?? string.Empty));
                case "save_memory":
                {
                    var content = Str("content");
                    if (string.IsNullOrWhiteSpace(content)) return "Error: 'content' is required.";
                    var id = await globals.SaveMemory(content, Strs("tags"), IntOr("importance", 1));
                    return $"Saved memory {id}.";
                }
                case "save_shortcut":
                {
                    var title = Str("title"); var content = Str("content");
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) return "Error: 'title' and 'content' are both required.";
                    var id = await globals.SaveShortcut(title, content, Strs("tags"));
                    return $"Saved shortcut {id}.";
                }
                case "get_shortcuts":
                    return await globals.GetShortcuts();
                case "delete_memory":
                {
                    var id = Str("id") ?? Str("idOrShortId");
                    if (string.IsNullOrWhiteSpace(id)) return "Error: 'id' is required.";
                    return await globals.DeleteMemory(id) ? $"Deleted memory {id}." : $"No memory matched '{id}'.";
                }
                default:
                    return $"Unknown memory tool '{toolName}'.";
            }
        }

        private static string FormatMemoriesResult(List<AgentMemoryEntry> memories)
        {
            if (memories == null || memories.Count == 0) return "No memories matched.";
            var sb = new StringBuilder();
            sb.AppendLine($"{memories.Count} memor{(memories.Count == 1 ? "y" : "ies")}:");
            foreach (var m in memories)
            {
                var shortId = !string.IsNullOrEmpty(m.Id) && m.Id.Length >= 8 ? m.Id.Substring(0, 8) : m.Id;
                var tags = m.Tags != null && m.Tags.Count > 0 ? $"  (#{string.Join(" #", m.Tags)})" : string.Empty;
                sb.AppendLine($"[{shortId}] {m.Content}{tags}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Pull the 'code' string out of a tool_call's JSON arguments. Falls back to the raw
        /// arguments text if it isn't the expected {"code": "..."} shape (some models send bare code).</summary>
        private static string ExtractCodeFromToolCall(HFWrapper.HFToolCall toolCall)
        {
            var args = toolCall?.function?.arguments;
            if (string.IsNullOrWhiteSpace(args)) return string.Empty;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(args);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("code", out var codeEl)
                    && codeEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return codeEl.GetString() ?? string.Empty;
                }
            }
            catch { /* not JSON, or no 'code' field — treat the whole argument blob as code */ }
            return args;
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

                // Prefer the provider's native structured tool-calling channel: code travels as a JSON
                // string argument, immune to the {{{ }}} framing leaks and brace-truncation that broke the
                // text protocol. Fall back to the text protocol when the active provider is local (no tool
                // channel) or the capability probe fails.
                bool useToolCalling;
                try { useToolCalling = await llm.SupportsNativeToolCallingAsync(); }
                catch { useToolCalling = false; }

                var systemPrompt = await BuildSystemPrompt(userMessage, conversation, toolCallingMode: useToolCalling);
                var toolDefinitions = useToolCalling ? BuildToolDefinitions() : null;

                llm.ResetSession(llmSessionId);

                var allScriptsExecuted = new List<AgentScriptResult>();
                var sharedGlobals = new ScriptGlobals(agentService);
                var scriptSession = scriptEngine.CreateSession(sharedGlobals);
                int totalPromptTokens = 0;
                int totalCompletionTokens = 0;
                int iterationsDone = 0;

                var userPrompt = BuildUserPrompt(conversation, userMessage, senderName);

                // Tool-calling mode: seed the structured session with system + user once; later turns are
                // appended as tool-results / user-guidance. Text mode: keep the system prompt for iter 0
                // and feed observations back via currentPrompt.
                if (useToolCalling)
                {
                    llm.StartToolSession(llmSessionId, systemPrompt);
                    llm.AppendUserMessageToToolSession(llmSessionId, userPrompt);
                }
                var currentPrompt = userPrompt;
                string? firstIterationSystemPrompt = systemPrompt;
                var errorFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
                var scriptFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
                bool stuckForceFinal = false;
                bool finalFormatRetryUsed = false;
                int emptyFinalRetries = 0;
                int consecutiveFailedScripts = 0;
                int consecutiveNoOpResponses = 0;

                // Streams the agent's current prose + the scripts it has run so far to the live
                // progress channel. NEVER fabricates placeholder text: if the model hasn't actually
                // said anything yet, we send only the live work-status note (or nothing) — no canned
                // "On it." filler.
                void ReportProgress(string runningNote)
                {
                    if (onProgress == null) return;
                    var shown = progressText.ToString();
                    string body;
                    if (shown.Length > 0 && !string.IsNullOrWhiteSpace(runningNote)) body = $"{shown}\n\n{runningNote}";
                    else if (shown.Length > 0) body = shown;
                    else if (!string.IsNullOrWhiteSpace(runningNote)) body = runningNote;
                    else return; // nothing real to report — don't push a placeholder
                    try { onProgress(body, new List<AgentScriptResult>(allScriptsExecuted)); } catch { }
                }

                // Deliver a retry/guidance/observation prompt the right way for the active mode: a user-role
                // turn in the tool session, or the next currentPrompt in the text-protocol loop.
                void SendModelPrompt(string text)
                {
                    if (useToolCalling) llm.AppendUserMessageToToolSession(llmSessionId, text);
                    else currentPrompt = text;
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
                        if (useToolCalling)
                        {
                            // The structured session already holds system + user + any prior tool turns.
                            llmResponse = await llm.QueryToolSessionAsync(llmSessionId, toolDefinitions!);
                        }
                        else
                        {
                            // Pass system prompt only on iteration 0 so it is set as the LLM session's
                            // system role message once — not re-injected into every user turn.
                            llmResponse = await llm.QueryLLM(currentPrompt, llmSessionId,
                                systemPrompt: firstIterationSystemPrompt);
                            firstIterationSystemPrompt = null; // don't resend
                        }
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

                    // Normalize the model turn into segments (prose + scripts) regardless of channel.
                    // Tool-calling: each tool_call becomes a script segment tagged with its ToolCallId so
                    // its result can be routed back as a role:"tool" message. Otherwise (text mode, or a
                    // tool-capable model that replied in prose/{{{ }}}) fall back to the text parser.
                    List<ResponseSegment> segments;
                    if (useToolCalling && llmResponse.ToolCalls != null && llmResponse.ToolCalls.Count > 0)
                    {
                        segments = new List<ResponseSegment>();
                        if (!string.IsNullOrWhiteSpace(llmResponse.Response))
                            segments.Add(new ResponseSegment { IsScript = false, Content = llmResponse.Response.Trim() });
                        foreach (var tc in llmResponse.ToolCalls)
                        {
                            var name = tc.function?.name ?? string.Empty;
                            if (MemoryToolNames.Contains(name))
                                // Memory tool: dispatched straight to the memory API, not the script engine.
                                segments.Add(new ResponseSegment { IsScript = false, ToolName = name, Content = tc.function?.arguments ?? string.Empty, ToolCallId = tc.id });
                            else
                                segments.Add(new ResponseSegment { IsScript = true, ToolName = "execute_csharp", Content = ExtractCodeFromToolCall(tc), ToolCallId = tc.id });
                        }
                    }
                    else
                    {
                        segments = ParseLLMResponse(llmResponse.Response ?? "");
                    }
                    // An "action" is a script OR a native (memory) tool call. Anything else is prose.
                    bool IsAction(ResponseSegment s) => s.IsScript || s.ToolName != null;
                    bool IsProse(ResponseSegment s) => !s.IsScript && s.ToolName == null;
                    var hasScripts = segments.Any(IsAction);

                    // Talk while working: push the agent's conversational prose to the live progress
                    // channel the moment we see it, before the scripts in this turn execute. The user
                    // reads "On it — pulling that now…" while the data-gathering runs in the background.
                    if (hasScripts && onProgress != null)
                    {
                        var thought = string.Join("\n", segments.Where(IsProse)
                            .Select(s => s.Content)).Trim();
                        if (thought.Length > 0)
                        {
                            if (progressText.Length > 0) progressText.AppendLine().AppendLine();
                            progressText.Append(thought);
                        }
                        int pendingCount = segments.Count(IsAction);
                        ReportProgress($"_…running {pendingCount} step{(pendingCount == 1 ? "" : "s")} (step {iteration + 1})_");
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

                        // Guard: the model occasionally returns an empty/whitespace completion (no text,
                        // no script). Don't surface a blank bubble and DON'T fake a canned reply — empties
                        // are transient, so re-ask the model (a few times) to get a REAL answer. Only if it
                        // stubbornly returns nothing do we report it honestly as an LLM failure.
                        if (string.IsNullOrWhiteSpace(finalText))
                        {
                            if (emptyFinalRetries < 3 && !stuckForceFinal)
                            {
                                emptyFinalRetries++;
                                SendModelPrompt("(Your last message came through completely empty — nothing was sent to the user. Answer their previous message now.)");
                                continue;
                            }

                            // Genuinely persistent empty: surface a truthful failure (same path as any
                            // other LLM failure), not a fabricated persona response.
                            llm.ResetSession(llmSessionId);
                            agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                                allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                                turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel);
                            return new AgentChatResponse
                            {
                                ConversationId = conversation.ConversationId,
                                Response = "Something went wrong with my brain — the model returned an empty response several times in a row.",
                                ScriptsExecuted = allScriptsExecuted,
                                Success = false,
                                ErrorMessage = "LLM returned an empty completion after retries.",
                                PromptTokens = totalPromptTokens,
                                CompletionTokens = totalCompletionTokens,
                                Iterations = iterationsDone
                            };
                        }

                        // Guard: model sometimes emits C# code as plain text (unbalanced ``` fence,
                        // or naked tool calls like "ReadFile(...)") thinking it's a final answer.
                        // Re-prompt once to force a real final reply or a properly-fenced script.
                        if (!finalFormatRetryUsed && !stuckForceFinal && LooksLikeUnexecutedScript(finalText))
                        {
                            finalFormatRetryUsed = true;
                            SendModelPrompt("[Format error] Your last reply was not a real final answer. Either it (a) contained C# code as plain text instead of actually running it, (b) was a stub like 'Let me get X' or 'I'll now Y' that promises to act but contains no answer, or (c) was an XML/JSON tool envelope. "
                                + (useToolCalling
                                    ? "Pick ONE: actually run the script by CALLING the execute_csharp tool, OR write the real text-only final answer with NO code, NO tool envelopes, NO 'Let me X'. "
                                    : "Pick ONE: actually run the script wrapped in {{{ ... }}} so it executes, OR write the real text-only final answer with NO code, NO fences, NO tool names, NO 'Let me X'. ")
                                + "Do not stall. Do not promise. Either act or answer.");
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
                    // True once at least one script's result has been delivered as a role:"tool" message.
                    // When set, the trailing guidance goes back as a plain user turn (observations are
                    // already in the tool results); when not, observations themselves are sent back.
                    bool anyToolResultsAppended = false;

                    foreach (var segment in segments)
                    {
                        // Prose (no action) — just record the agent's thought.
                        if (IsProse(segment))
                        {
                            if (!string.IsNullOrWhiteSpace(segment.Content))
                                observationSb.AppendLine($"[Agent thought] {segment.Content.Trim()}");
                            continue;
                        }

                        // Native MEMORY tool call — dispatch straight to the memory API (no Roslyn).
                        if (!segment.IsScript && segment.ToolName != null)
                        {
                            string memOut; bool memOk = true;
                            try { memOut = await DispatchMemoryToolAsync(sharedGlobals, segment.ToolName, segment.Content); }
                            catch (Exception mex) { memOk = false; memOut = $"Memory tool error: {mex.GetType().Name}: {mex.Message}"; }

                            var memText = KliveAgentContextBudget.TruncateToTokens(memOut ?? string.Empty,
                                memOk ? KliveAgentContextBudget.ScriptOutputBudget : KliveAgentContextBudget.ScriptErrorBudget);
                            observationSb.AppendLine($"[{(memOk ? "OK" : "ERROR")} | {segment.ToolName}]");
                            if (!string.IsNullOrWhiteSpace(memText)) observationSb.AppendLine(memText);
                            observationSb.AppendLine();

                            if (useToolCalling && !string.IsNullOrEmpty(segment.ToolCallId))
                            {
                                llm.AppendToolResult(llmSessionId, segment.ToolCallId, segment.ToolName, memText);
                                anyToolResultsAppended = true;
                            }
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

                        // Build this script's observation once, then route it to BOTH the aggregate
                        // observation buffer (text-mode feedback) and — when this came from a native
                        // tool_call — back as a role:"tool" result keyed to its id.
                        var scriptObs = new StringBuilder();
                        if (result.Success)
                        {
                            var output = KliveAgentContextBudget.TruncateToTokens(
                                result.Output ?? string.Empty,
                                KliveAgentContextBudget.ScriptOutputBudget);

                            scriptObs.AppendLine($"[OK | {result.ExecutionTimeMs}ms]");
                            if (!string.IsNullOrWhiteSpace(output))
                                scriptObs.AppendLine(output);
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

                            scriptObs.AppendLine($"[ERROR | {result.ExecutionTimeMs}ms]");
                            scriptObs.AppendLine(KliveAgentContextBudget.TruncateToTokens(errMsg, KliveAgentContextBudget.ScriptErrorBudget));
                            consecutiveFailedScripts++;
                            if (consecutiveFailedScripts >= 5) stuckForceFinal = true;
                        }

                        if (result.Success) consecutiveFailedScripts = 0;

                        var scriptObsText = scriptObs.ToString().TrimEnd();
                        observationSb.AppendLine(scriptObsText);
                        observationSb.AppendLine();

                        // Native tool_call → answer it with a role:"tool" message so the model's next turn
                        // sees the result attached to the exact call it made.
                        if (useToolCalling && !string.IsNullOrEmpty(segment.ToolCallId))
                        {
                            llm.AppendToolResult(llmSessionId, segment.ToolCallId, "execute_csharp", scriptObsText);
                            anyToolResultsAppended = true;
                        }
                    }

                    // Soft nudge once the task has gone deep — informational, not a stop.
                    string nudge = string.Empty;
                    if (iteration + 1 == 12 && !stuckForceFinal)
                    {
                        nudge = "\n\n[Nudge] You've taken 12 steps on this. If you're making real progress, keep going. " +
                            "If you're spiralling, consider SaveMemory(\"...\") for what you've learned and giving the final answer.";
                    }

                    // Hard safety cap: prevent runaway loops that never produce a final answer.
                    // After 30 iterations, force the final-answer path. Real questions that
                    // need >30 scripted steps should be split by the user; the agent should
                    // not hang an API request indefinitely (exposed as a Tier-5 hang bug).
                    if (iteration + 1 >= 30 && !stuckForceFinal)
                    {
                        stuckForceFinal = true;
                    }

                    // Feed the next turn. System prompt + prior turns already live in the session, so we
                    // never re-inject them. In tool mode where results were delivered as role:"tool"
                    // messages, send only the guidance (observations are already attached to the calls);
                    // otherwise send the aggregate observations + guidance as a user turn.
                    string guidance = stuckForceFinal
                        ? "[Stuck-loop detected] You repeated a script or hit the same error 3+ times. STOP scripting. Reply with a final text-only answer that honestly reports what you tried, what worked, and what blocked you."
                        : "If you have what you need, give the final answer now (no scripts). Otherwise run your next script — prefer ONE composite block over several tiny ones.";
                    guidance += nudge;

                    if (useToolCalling && anyToolResultsAppended)
                        SendModelPrompt(guidance);
                    else
                        SendModelPrompt(observationSb.ToString().TrimEnd() + "\n\n" + guidance);
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

        public static string BuildToolGuide(string userMessage, bool toolCallingMode = false)
        {
            var sb = new StringBuilder();

            sb.AppendLine("[Tools] (public methods on ScriptGlobals — call by name)");

            AppendToolNames(sb, "Core", StarterTools);

            if (ShouldIncludeDiscoveryTools(userMessage))
                AppendToolNames(sb, "Codebase", DiscoveryTools);

            if (ShouldIncludeAdvancedRuntimeTools(userMessage))
                AppendToolNames(sb, "Runtime", AdvancedRuntimeTools);

            // Memory is ALWAYS surfaced (recall is meant to be a reflex every turn). In tool-calling mode
            // it's a set of FIRST-CLASS native tools — call them directly, never via execute_csharp. In the
            // text-protocol fallback it's the ScriptGlobals C# methods.
            if (toolCallingMode)
                sb.AppendLine("Memory (NATIVE TOOLS — call these directly as tool calls, NEVER via execute_csharp): "
                    + "recall_memories(query, maxResults?) — search memory (do this FIRST for non-code questions); "
                    + "recall_memories_by_tag(tag); save_memory(content, tags?, importance?); "
                    + "save_shortcut(title, content, tags?); get_shortcuts(); delete_memory(id).");
            else
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

        // Set only when this script segment originated from a native tool_call, so its result can be
        // routed back as a role:"tool" message keyed to this id. Null for text-protocol scripts.
        public string? ToolCallId { get; set; }

        // The native tool that produced this segment: "execute_csharp" (then IsScript=true and Content is
        // the C# code) or a memory tool name (then IsScript=false and Content is the raw JSON arguments).
        // Null for text-protocol prose/scripts.
        public string? ToolName { get; set; }
    }
}
