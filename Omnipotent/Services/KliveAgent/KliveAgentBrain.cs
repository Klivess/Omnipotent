using Omnipotent.Service_Manager;
using Omnipotent.Services.HostControl;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.ComputerControl;
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

        private static readonly PromptToolDescriptor[] TemporalTools =
        [
            new("ScheduleTask", "PROSPECTIVE MEMORY: ScheduleTask(instruction, dueAt, repeatEvery?) schedules a FULL future agent turn — at dueAt (\"in 2h30m\" or a UTC date-time) your instruction fires with all tools and the outcome goes to Klives. Use for anything that must happen later; never just promise future action in prose."),
            new("ListScheduledTasks", "List active scheduled tasks (due times + ages) and recent completed/cancelled ones."),
            new("CancelScheduledTask", "Cancel an active scheduled task by id or short-id prefix.")
        ];

        private static readonly PromptToolDescriptor[] KnowledgeTools =
        [
            new("SearchKnowledge", "Search Klives' whole cross-system knowledge base (past Projects, your conversations & memories, Omniscience facts, repo docs, cached web) by free-text query. Returns cited snippets with doc ids. Use for \"have we done/decided this before\" and \"what do we know about X\" questions."),
            new("ReadKnowledgeDoc", "Open the full text of a knowledge document by the doc id shown in a SearchKnowledge result."),
            new("WebSearch", "Search the LIVE web (self-hosted SearXNG). Use for current/external info the knowledge base won't have; fetchTop>0 also indexes the top pages for full-text follow-up."),
            new("WebFetch", "Download one web page by URL, extract its text, index it, and return the text.")
        ];

        private static readonly PromptToolDescriptor[] ProjectsTools =
        [
            new("CreateProject", "Delegate long-running autonomous work to the Projects task force: CreateProject(name, goal, tokenBudgetUsd, moneyBudgetUsd?, moneyAutonomousThresholdUsd?, subAgentCap?). Returns the new project ID. Use for goals that outlast one chat — the Commander pursues them 24/7 and shares this memory."),
            new("ListProjects", "List all Projects with a one-line status (id, name, status, budget, active agents, last event)."),
            new("GetProjectStatus", "Get one Project's status/budget/agent summary by ID."),
            new("SteerProject", "Send a message to a Project's Commander (steers a live wake or wakes it): SteerProject(projectID, message).")
        ];

        public KliveAgentBrain(KliveAgent agentService, KliveAgentScriptEngine scriptEngine, KliveAgentMemory memory)
        {
            this.agentService = agentService;
            this.scriptEngine = scriptEngine;
            this.memory = memory;
        }

        // â”€â”€ Prompt Assembly â”€â”€

        /// <summary>How long one prompt section may take before the run goes on without it.</summary>
        internal static readonly TimeSpan PromptSectionBudget = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// Builds one prompt section on the thread pool and waits at most <paramref name="budget"/> for it.
        /// Every section is enrichment the run can do without, and some do synchronous work underneath
        /// (SQLite, ONNX, CPU-bound ranking): awaited inline, one slow section once held the whole run at
        /// "preparing context" until the stall watchdog fired. A late or failed section yields
        /// <paramref name="fallback"/>; <paramref name="onDone"/> reports (milliseconds, missedBudget).
        /// </summary>
        internal static async Task<T> BoundedSectionAsync<T>(Func<Task<T>> section, TimeSpan budget, T fallback,
            Action<long, bool>? onDone = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var work = Task.Run(section);
            try
            {
                var value = await work.WaitAsync(budget).ConfigureAwait(false);
                onDone?.Invoke(timer.ElapsedMilliseconds, false);
                return value;
            }
            catch (TimeoutException)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                onDone?.Invoke(timer.ElapsedMilliseconds, true);
                return fallback;
            }
            catch
            {
                onDone?.Invoke(timer.ElapsedMilliseconds, false);
                return fallback;
            }
        }

        public async Task<string> BuildSystemPrompt(string userMessage, AgentConversation conversation, bool toolCallingMode = false, bool computerUseEnabled = false,
            KliveAgentComputerTarget computerTarget = KliveAgentComputerTarget.Host, bool visionEnabled = true,
            KliveAgentPerformance? performance = null)
        {
            var missed = new System.Collections.Concurrent.ConcurrentQueue<string>();
            Task<T> Section<T>(string name, Func<Task<T>> build, T fallback) =>
                BoundedSectionAsync(build, PromptSectionBudget, fallback, (ms, late) =>
                {
                    performance?.Add("prompt." + name, ms);
                    if (late) missed.Enqueue(name);
                });

            var personalityTask = Section("personality", () => agentService.GetStringOmniSetting(
                "KliveAgent_Personality",
                defaultValue: KliveAgentPersonality.Default), KliveAgentPersonality.Default);
            var compactPromptTask = Section("settings", () => agentService.GetBoolOmniSetting("KliveAgent_CompactPrompt", defaultValue: true), true);
            var surfaceTask = Section("serviceSurface",
                () => agentService.ServiceTools?.BuildPromptBlockAsync() ?? Task.FromResult(string.Empty), string.Empty);

            // Extract PascalCase seed words from the user message for repo map personalisation
            var seeds = KliveAgentRepoMap.ExtractSeedsFromText(userMessage);

            // All sections start together; the slowest one, capped at its budget, sets the build time.
            var memoriesTask = Section("memories", () => memory.FormatMemoriesForPrompt(
                userMessage,
                maxMemories: 4,
                maxShortcuts: 3,
                maxTokens: KliveAgentContextBudget.MemoryBudget), string.Empty);

            // Cross-system knowledge (KliveRAG) recall. KliveRAG bounds its own search; the section
            // budget here is the backstop if that ever stops being true.
            var knowledgeTask = Section("knowledge", () => agentService.SearchKnowledgeForPromptAsync(
                userMessage, KliveAgentContextBudget.KnowledgeBudget), string.Empty);

            // Known accounts from the global shared registry, so you reuse them before creating
            // duplicates. Fail-soft (returns "" if the registry is absent).
            var accountsTask = Section("accounts",
                () => agentService.DescribeAccountsForPromptAsync(KliveAgentContextBudget.KnownAccountsBudget), string.Empty);

            // Token-budgeted, task-personalised repo map (only when the task has code signals).
            var repoMapper = agentService.RepoMap;
            var repoMapTask = repoMapper != null && seeds.Count > 0
                ? Section("repoMap", () => Task.FromResult(
                    repoMapper.GetRepoMap(KliveAgentContextBudget.RepoMapBudget, seeds)), string.Empty)
                : Task.FromResult(string.Empty);

            var repoMap = await repoMapTask;
            var memoriesSection = await memoriesTask;

            var sb = new StringBuilder();

            sb.AppendLine(await personalityTask);
            sb.AppendLine();

            bool compactPrompt = await compactPromptTask;
            if (compactPrompt)
            {
                sb.AppendLine(KliveAgentPromptPolicy.Build(toolCallingMode, computerUseEnabled, computerTarget, visionEnabled));
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(KliveAgentPromptPolicy.BuildDetailed(toolCallingMode, computerUseEnabled, computerTarget, visionEnabled));
            }

            // The Service Surface index — which services exist and what each is for. It belongs ABOVE the
            // cache breakpoint because it is identical on every run: the whole point is that the agent
            // never spends a call discovering that a service exists, only how to call it.
            if (agentService.ServiceTools != null)
            {
                string surfaceBlock = string.Empty;
                try { surfaceBlock = await surfaceTask; }
                catch { /* best-effort: a missing index must never break the prompt build */ }
                if (!string.IsNullOrWhiteSpace(surfaceBlock))
                {
                    sb.AppendLine();
                    sb.Append(surfaceBlock);
                }
            }

            // Cache breakpoint: everything ABOVE is the STABLE skeleton (personality + rules + patterns +
            // memory discipline — identical across tasks); everything BELOW is task-VOLATILE (tool guide +
            // repo map + memories). In tool-calling mode (the OpenRouter path) KliveLLM splits the system
            // message here and marks the skeleton with cache_control so its prefill is served from cache on
            // every turn after the first. The marker is split out / stripped before sending — never seen by
            // the model — so it is inert for the local/text path.
            if (toolCallingMode)
                sb.Append(KliveLLM.KliveLLM.CacheBreakpointMarker);

            sb.Append(compactPrompt ? BuildCompactToolGuide(userMessage, toolCallingMode) : BuildToolGuide(userMessage, toolCallingMode));

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

            // Cross-system knowledge block — task-volatile, below the cache breakpoint alongside memories.
            var knowledgeSection = string.Empty;
            try { knowledgeSection = await knowledgeTask; } catch { /* best-effort */ }
            if (!string.IsNullOrWhiteSpace(knowledgeSection))
            {
                sb.AppendLine();
                sb.Append(knowledgeSection);
            }

            // Known accounts (global shared registry) — task-volatile, below the cache breakpoint.
            var accountsSection = string.Empty;
            try { accountsSection = await accountsTask; } catch { /* best-effort */ }
            if (!string.IsNullOrWhiteSpace(accountsSection))
            {
                sb.AppendLine();
                sb.AppendLine("[Known Accounts] (global shared registry — account_list for full details, account_register before any new signup)");
                sb.Append(accountsSection);
            }

            if (!missed.IsEmpty)
                _ = agentService.ServiceLog(
                    $"[KliveAgent] Prompt sections skipped after {PromptSectionBudget.TotalMilliseconds:0}ms: {string.Join(", ", missed)}. The run continued without them.",
                    false);

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

        /// <summary>Native tools dispatched directly (outside Roslyn): the code/file/path tools.
        /// Combined with MemoryToolNames in IsNonScriptTool.</summary>
        private static readonly HashSet<string> NativeNonMemoryTools = new(StringComparer.Ordinal)
        {
            "grep", "read_file", "list_directory", "get_global_path", "search_knowledge", "read_knowledge_doc", "web_search", "web_fetch",
            "account_list", "account_register", "account_update",
            "klivemail_create_mailbox", "klivemail_list_messages", "klivemail_get_message", "klivemail_wait_for_email",
            "notify_klives",
            "schedule_task", "list_scheduled_tasks", "cancel_scheduled_task"
        };

        /// <summary>Computer-use ("host control") tools: dispatched outside Roslyn to HostControlManager,
        /// bypassing the per-script timeout so a legitimately slow GUI step is never killed. Includes the
        /// encrypted-credential vault ops (save/list/delete) since the vault lives in HostControl.</summary>
        private static readonly HashSet<string> ComputerUseToolNames = new(StringComparer.Ordinal)
        {
            "computer_screenshot", "computer_window_state", "computer_read_screen", "computer_move",
            "computer_find_text", "computer_click_text",
            "computer_mouse_move_relative",
            "computer_click", "computer_drag", "computer_scroll", "computer_type", "computer_key",
            "computer_mouse_down", "computer_mouse_up", "computer_key_down", "computer_key_up", "computer_release_all",
            "computer_wait", "computer_focus_window", "computer_launch_app", "computer_open_browser", "computer_navigate",
            "computer_clipboard_get", "computer_clipboard_set", "computer_confirm_action", "computer_confirm_and_click",
            "request_human",
            "save_encrypted_memory", "list_encrypted_memories", "delete_encrypted_memory",
            // KliveAgent's own desktop (container target) adds structured browser control and a
            // container-local terminal on top of the shared visual vocabulary.
            "computer_browser_inspect", "computer_browser_action", "computer_click_browser_control",
            "computer_upload_file", "computer_terminal", "computer_cdp"
        };

        private static bool IsComputerTool(string name) => ComputerUseToolNames.Contains(name);

        /// <summary>The general "wait for an external event" tool — orchestrated by the brain (it needs the
        /// script session + progress heartbeat), NOT subject to the per-script 30s cap. The un-timed sibling
        /// of computer_wait, for service/HTTP/file observables.</summary>
        private static bool IsWaitTool(string name) => name == "wait_for";

        /// <summary>True for native tools that are dispatched directly (outside Roslyn) — the code/file/path
        /// tools plus memory tools plus computer-use tools plus wait_for. Anything else is routed to execute_csharp.</summary>
        private static bool IsNonScriptTool(string name) => NativeNonMemoryTools.Contains(name) || MemoryToolNames.Contains(name) || ComputerUseToolNames.Contains(name) || IsWaitTool(name)
            // Service Surface tools are generated at startup, so no static set can list them. Claiming
            // them here is load-bearing: the caller's fallback treats any unrecognised name as
            // execute_csharp and would compile the arguments JSON as C#.
            || KliveAgentServiceTools.IsHandled(name);

        /// <summary>Read-only native tools have no side effects and never touch the Roslyn session, so
        /// several of them in one turn can run concurrently (their I/O latency overlaps). Write tools
        /// (save_*/delete_*) and execute_csharp are excluded — they run serially, in order.</summary>
        /// <param name="argsJson">Needed for Service Surface tools: one generated tool covers several
        /// operations, so whether the call reads or writes is decided by its 'op', not by its name.</param>
        private static bool IsParallelSafeNativeTool(string name, string? argsJson = null) => name switch
        {
            "grep" or "read_file" or "list_directory" or "get_global_path"
                or "recall_memories" or "recall_memories_by_tag" or "get_shortcuts"
                or "search_knowledge" or "read_knowledge_doc" or "web_search" or "web_fetch"
                or "account_list" or "list_scheduled_tasks"
                or "klivemail_list_messages" or "klivemail_get_message" => true,
            _ => KliveAgentServiceTools.IsParallelSafe(name, argsJson)
        };

        /// <summary>Trim a turn's accumulated clip frames to a total budget, preserving chronological order.
        /// Every action's settled (current-state) frame is kept first; the remaining budget is filled with the
        /// most RECENT intermediate motion frames. So a frame-heavy turn loses old in-between frames before it
        /// ever loses an action's end-state — and when settled frames alone exceed the cap, the newest win.</summary>
        private static List<(byte[] data, string mimeType)> BudgetClipFrames(List<(byte[] data, string mimeType, bool settled)> all, int cap)
        {
            if (all.Count <= cap)
                return all.Select(f => (f.data, f.mimeType)).ToList();

            var keep = new bool[all.Count];
            int kept = 0;
            // 1) keep settled (current-state) frames, newest first
            for (int i = all.Count - 1; i >= 0 && kept < cap; i--)
                if (all[i].settled) { keep[i] = true; kept++; }
            // 2) fill the rest with the most recent intermediate frames
            for (int i = all.Count - 1; i >= 0 && kept < cap; i--)
                if (!all[i].settled && !keep[i]) { keep[i] = true; kept++; }

            var outList = new List<(byte[] data, string mimeType)>();
            for (int i = 0; i < all.Count; i++)
                if (keep[i]) outList.Add((all[i].data, all[i].mimeType));
            return outList;
        }

        /// <summary>Runs a native tool, capturing success/failure as a value instead of throwing, so it
        /// can be awaited from a pre-launched parallel task or inline with identical handling.</summary>
        private static async Task<(bool ok, string output)> RunNativeToolAsync(ScriptGlobals globals, string toolName, string? argsJson)
        {
            try { return (true, await DispatchNativeToolAsync(globals, toolName, argsJson)); }
            catch (Exception ex) { return (false, $"{toolName} tool error: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>
        /// The native tools exposed to the model: execute_csharp (arbitrary C# over the live service graph)
        /// plus first-class MEMORY tools so recall/save are a direct tool call, never a hand-written script.
        /// </summary>
        public static List<HFWrapper.HFTool> BuildToolDefinitions(bool includeComputerUse = false)
        {
            static HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
            {
                type = "function",
                function = new HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
            };

            var tools = new List<HFWrapper.HFTool>
            {
                Tool("execute_csharp",
                    "Execute a C# script IN-PROCESS INSIDE Omnipotent (Roslyn) — the script runs in the live " +
                    "server's context, so it is equally good for general-purpose scripting (computation, parsing, " +
                    "file/data work, HTTP) AND it can read, drive, and control ALL of Omnipotent: every live " +
                    "service, its state, and its methods, via the ScriptGlobals API (GetService, GetTypeSchema, " +
                    "GetObjectMembers, CallObjectMethod, Log, SearchCode, ReadFile, GetRecentErrors, etc.). " +
                    "Locals persist across calls within the same turn. `await` any Task-returning call. " +
                    "Use Log(...) to return observations. Pass RAW C# in the 'code' argument — do NOT wrap it " +
                    "in {{{ }}} or markdown fences. NOTE: memory has its OWN dedicated tools (recall_memories, " +
                    "save_memory, …) — use those, not execute_csharp, for anything memory-related.",
                    new { type = "object", properties = new { code = new { type = "string", description = "The raw C# script to compile and run." } }, required = new[] { "code" } }),

                Tool("grep",
                    "Search the codebase CONTENTS and return matching 'path:line' lines. Runs directly — no C#/compile step, so it cannot fail to compile. Use this for content search INSTEAD of calling SearchCode/SearchCodeRegex inside execute_csharp. 'pattern' is a regex (ripgrep-style) by default; set fixedString=true for a literal substring. Scope with 'path' (a file or subfolder, e.g. \"Omnipotent/Services/KliveAgent\").",
                    new { type = "object", properties = new {
                        pattern = new { type = "string", description = "Regex (or literal, if fixedString=true) to search for in file contents." },
                        path = new { type = "string", description = "Optional file or subfolder to scope the search (e.g. \"Omnipotent/Services\")." },
                        maxResults = new { type = "integer", description = "Max matches to return (default 30)." },
                        fixedString = new { type = "boolean", description = "Treat pattern as a literal substring instead of regex (default false)." }
                    }, required = new[] { "pattern" } }),

                Tool("read_file",
                    "Read a SOURCE file from the codebase directly — no C#/compile step. Path is relative to the repo root (e.g. \"Omnipotent/Services/KliveAgent/KliveAgent.cs\"). NEVER send {\"path\":...} JSON as an execute_csharp script — that's THIS tool; call it. Use startLine/maxLines to page large files.",
                    new { type = "object", properties = new {
                        path = new { type = "string", description = "Repo-relative file path." },
                        startLine = new { type = "integer", description = "1-based start line (default 1)." },
                        maxLines = new { type = "integer", description = "Max lines to return (default 200)." }
                    }, required = new[] { "path" } }),

                Tool("list_directory",
                    "List files and folders in a CODEBASE directory (repo-relative, e.g. \"Omnipotent/Services\"). Runs directly — no compile step. For RUNTIME data directories (SavedData/...), use get_global_path then read in execute_csharp.",
                    new { type = "object", properties = new {
                        path = new { type = "string", description = "Repo-relative directory (default: repo root)." }
                    }, required = Array.Empty<string>() }),

                Tool("get_global_path",
                    "Resolve a runtime DATA path constant to an absolute path (e.g. \"MemeScraperReelsDataDirectory\" -> ...\\SavedData\\MemeScraper\\Instagram\\Reels). Use this instead of reflecting through OmniPaths.GlobalPaths by hand.",
                    new { type = "object", properties = new {
                        key = new { type = "string", description = "A GlobalPaths field name." }
                    }, required = new[] { "key" } }),

                Tool("recall_memories",
                    "Search your long-term memory for facts about Klive, his preferences/people/projects/history, " +
                    "past decisions, or your own prior conclusions. Call this FIRST for any question not purely about the codebase. " +
                    "For time-window questions (\"what did I learn last week\"), pass since/until; with an empty query they browse the window newest-first.",
                    new { type = "object", properties = new {
                        query = new { type = "string", description = "Free-text search query (may be empty when browsing a time window)." },
                        maxResults = new { type = "integer", description = "Max memories to return (default 10)." },
                        since = new { type = "string", description = "Optional window start: UTC date-time (\"2026-07-01\") or lookback (\"7d\", \"24h\")." },
                        until = new { type = "string", description = "Optional window end: UTC date-time or lookback." }
                    }, required = new[] { "query" } }),

                Tool("schedule_task",
                    "PROSPECTIVE MEMORY — schedule your future self to ACT. At dueAt, a full agent turn fires with your instruction " +
                    "and every tool available, and the outcome is reported to Klives. Survives restarts (a missed task fires late, flagged as late). " +
                    "Use for anything that must happen LATER: reminders, follow-ups, checks, recurring routines. Do NOT use wait_for for waits " +
                    "beyond this turn, and never just PROMISE future action in prose — schedule it.",
                    new { type = "object", properties = new {
                        instruction = new { type = "string", description = "What your future self must do, self-contained (it won't remember this turn unless the task fires in this conversation)." },
                        dueAt = new { type = "string", description = "When to fire: relative (\"in 2h30m\", \"45m\") or absolute UTC (\"2026-07-15 09:00\"; a bare time like \"09:00\" means the next occurrence)." },
                        repeatEvery = new { type = "string", description = "Optional recurrence interval (\"1d\", \"2h30m\"); minimum 5m. Omit for one-shot." }
                    }, required = new[] { "instruction", "dueAt" } }),

                Tool("list_scheduled_tasks",
                    "List your active scheduled tasks (due times + ages + recurrence) and recent completed/cancelled ones.",
                    new { type = "object", properties = new { } }),

                Tool("cancel_scheduled_task",
                    "Cancel an active scheduled task by id (or short-id prefix from list_scheduled_tasks).",
                    new { type = "object", properties = new {
                        id = new { type = "string", description = "The task id or short-id prefix." }
                    }, required = new[] { "id" } }),

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

                Tool("search_knowledge",
                    "Semantic + keyword search across Klives' WHOLE knowledge base — past Projects (their decisions/outcomes), your own conversations & memories, Omniscience person facts, repo docs, and cached web pages. " +
                    "Use this for cross-system questions that memory alone won't answer (\"what did project X conclude\", \"have we solved this before\", \"what do we know about <person/topic>\"). " +
                    "Returns cited snippets with a doc:<id> for each; call read_knowledge_doc to open one in full.",
                    new { type = "object", properties = new {
                        query = new { type = "string", description = "Free-text search query." },
                        maxResults = new { type = "integer", description = "Max hits to return (default 8)." },
                        includeMessages = new { type = "boolean", description = "Also search Omniscience's raw message corpus (default true)." }
                    }, required = new[] { "query" } }),

                Tool("read_knowledge_doc",
                    "Open the FULL text of a knowledge document by the doc:<id> shown in a search_knowledge result (e.g. a whole conversation, a repo doc, a project digest). This also opens web pages indexed by web_search/web_fetch.",
                    new { type = "object", properties = new {
                        docId = new { type = "string", description = "The document id (the doc:... value from a search result)." },
                        maxTokens = new { type = "integer", description = "Max tokens of document text to return (default 1500, max 3000)." }
                    }, required = new[] { "docId" } }),

                Tool("web_search",
                    "Search the LIVE web (self-hosted SearXNG, no API key). Use for current/external information the knowledge base won't have. Returns titled results with URLs and snippets; set fetchTop>0 to also download+index the top pages so you can read them in full via read_knowledge_doc. Requires Docker running; if it isn't, you'll get an actionable error and internal search still works.",
                    new { type = "object", properties = new {
                        query = new { type = "string", description = "The web search query." },
                        maxResults = new { type = "integer", description = "Max results (default 6)." },
                        fetchTop = new { type = "integer", description = "Download+index the top N result pages for full-text follow-up (0-3, default 2)." },
                        timeRange = new { type = "string", description = "Optional recency filter: day|week|month|year." }
                    }, required = new[] { "query" } }),

                Tool("web_fetch",
                    "Download ONE web page by URL, extract its readable text, index it, and return the text. Use to read a specific page (e.g. a result URL, a doc link).",
                    new { type = "object", properties = new {
                        url = new { type = "string", description = "The absolute http(s) URL to fetch." }
                    }, required = new[] { "url" } }),

                Tool("account_list",
                    "List accounts in the GLOBAL shared registry (every Project and you share it). ALWAYS call this before signing up on ANY external service — an account may already exist (created by another project). Shows service, username, email, status, owners, and the {account:...} refs to type its secrets. Secret values are never shown.",
                    new { type = "object", properties = new {
                        service = new { type = "string", description = "Optional service filter, e.g. \"github.com\"." }
                    }, required = Array.Empty<string>() }),

                Tool("account_register",
                    "Record an account in the GLOBAL shared registry so no project re-creates it — call it BEFORE filling a signup form, then type its secrets into the form as {account:<service>/<field>}. Pass secrets {\"password\":\"{generate}\"} to have a strong password minted for you without ever seeing it. Prefer a dedicated <something>@klive.dev email (create it with klivemail_create_mailbox; verification/reset mail arrives there). Secrets are stored encrypted and NEVER shown back. If the service already has an account this returns it and registers nothing unless allowDuplicate=true with a reason.",
                    new { type = "object", properties = new {
                        service = new { type = "string", description = "Service name or URL, e.g. \"github.com\"." },
                        username = new { type = "string", description = "The account's username/login." },
                        email = new { type = "string", description = "Email used, ideally a dedicated <x>@klive.dev address." },
                        description = new { type = "string", description = "What this account is for." },
                        secrets = new { type = "object", description = "Named secrets to store encrypted, e.g. {\"password\":\"…\",\"apiKey\":\"…\"}.", additionalProperties = new { type = "string" } },
                        allowDuplicate = new { type = "boolean", description = "Set true ONLY to intentionally create a second account for a service that already has one." },
                        reason = new { type = "string", description = "Required when allowDuplicate=true: why a separate account is needed." }
                    }, required = new[] { "service", "username" } }),

                Tool("account_update",
                    "Update a registered account: add or replace a named secret (e.g. the API key/secret you just obtained — stored encrypted, never shown back, typed or reported as {account:<service>/<username>/<name>}), or change its status/notes. Identify it by service (+ username if the service has several).",
                    new { type = "object", properties = new {
                        service = new { type = "string", description = "Service name or URL, as registered." },
                        username = new { type = "string", description = "Username, needed only when the service has several accounts." },
                        addSecretName = new { type = "string", description = "Name of the secret to add/replace, e.g. \"consumerKey\"." },
                        addSecretValue = new { type = "string", description = "Its value (\"{generate}\" mints a strong password)." },
                        status = new { type = "string", description = "active | dead | banned." },
                        notes = new { type = "string", description = "Free-form notes." },
                        newUsername = new { type = "string", description = "Correct the login/username (e.g. the site rejected the one you registered)." },
                        newEmail = new { type = "string", description = "Correct the email used." }
                    }, required = new[] { "service" } }),

                Tool("klivemail_create_mailbox",
                    "Create (or reuse) an inbox on Klives' own @klive.dev mail server for a signup — e.g. address \"tumblr.klives\" → tumblr.klives@klive.dev. It receives mail immediately. Runs in-process: no HTTP, auth or reflection.",
                    new { type = "object", properties = new {
                        address = new { type = "string", description = "Local part or full @klive.dev address." },
                        displayName = new { type = "string", description = "Optional label shown in the mail client." }
                    }, required = new[] { "address" } }),

                Tool("klivemail_wait_for_email",
                    "Block (no LLM cost) until an email arrives at a @klive.dev inbox, then return it with its verification CODE and its LINKS ranked verification-first (open the link with computer_navigate). Also accepts mail that arrived up to lookbackSeconds before the call, and notices if the site sent it to a slightly different @klive.dev address. Use right after submitting a signup or clicking 'send code'.",
                    new { type = "object", properties = new {
                        mailbox = new { type = "string", description = "The @klive.dev address you used on the site." },
                        fromContains = new { type = "string", description = "Optional sender filter, e.g. \"tumblr\"." },
                        subjectContains = new { type = "string", description = "Optional subject filter." },
                        timeoutSeconds = new { type = "integer", description = "How long to wait (default 300, max 900)." },
                        lookbackSeconds = new { type = "integer", description = "Also accept mail received this long before the call (default 900)." }
                    }, required = new[] { "mailbox" } }),

                Tool("klivemail_list_messages",
                    "List KliveMail messages newest first (id, time, sender, subject, snippet), optionally for one @klive.dev inbox.",
                    new { type = "object", properties = new {
                        mailbox = new { type = "string", description = "Optional @klive.dev inbox." },
                        limit = new { type = "integer", description = "Max messages (default 20, max 100)." },
                        unreadOnly = new { type = "boolean", description = "Only unread (default false)." }
                    }, required = Array.Empty<string>() }),

                Tool("klivemail_get_message",
                    "Read one KliveMail message in full by id: headers, verification code, ranked links and body text.",
                    new { type = "object", properties = new {
                        id = new { type = "string", description = "Message id from klivemail_list_messages." }
                    }, required = new[] { "id" } }),

                Tool("notify_klives",
                    "Message Klives directly on Discord (also listed in the website's Results panel). Use it when he asked to be messaged when something is done, or for something he must know before this run ends. Keep it short; never put passwords or keys in it — those go in your chat reply as {account:...} references.",
                    new { type = "object", properties = new {
                        message = new { type = "string", description = "What to tell him." }
                    }, required = new[] { "message" } }),

                Tool("wait_for",
                    "PAUSE until an external event happens, then continue — without ending your turn and without the 30s script limit. " +
                    "Use this whenever you must wait for someone/something else: a person to act (incl. in a GUI/game), a remote state to change, a file/email/build to appear. " +
                    "Observe ONE of: watch:\"screen\" (wait until the SCREEN changes — the right choice for games/GUIs, e.g. an opponent's move; uses a perceptual diff so a ticking clock doesn't trigger it, tune with 'threshold'), 'url' (poll an HTTP GET), or 'check' (a short C# probe that Log()s the value to watch). If you give none, it watches the screen. " +
                    "Stop condition 'until' (url/check only): \"change\" (default), \"contains\" (returns when it contains 'target'), or \"true\". " +
                    "It blocks (heartbeating, no LLM cost while waiting) up to maxMinutes, polling every intervalSeconds, and ALWAYS returns — the moment the event fires, or with a 'no change yet, decide what to do' summary at maxMinutes, or early if a url/check probe keeps erroring (so a broken check never hangs). " +
                    "For a SHORT settle right after your own action, computer_wait is simpler. NEVER hand-roll a long polling loop in execute_csharp — it gets killed at the per-script timeout.",
                    new { type = "object", properties = new {
                        watch = new { type = "string", description = "\"screen\" to wait until the display changes (no url/check needed). Best for games/GUIs / waiting on another player." },
                        threshold = new { type = "number", description = "For watch:\"screen\": how big a change counts (mean 0..255 perceptual delta, default 2). Lower = more sensitive; raise if minor animation keeps tripping it." },
                        url = new { type = "string", description = "URL to poll with a GET (use this OR 'check' OR watch:\"screen\")." },
                        check = new { type = "string", description = "A short C# probe that Log()s the value to watch. Fully general: services, files, DOM, etc." },
                        until = new { type = "string", description = "\"change\" (default) | \"contains\" | \"true\" (url/check only)." },
                        target = new { type = "string", description = "Text to wait for when until=\"contains\"." },
                        intervalSeconds = new { type = "integer", description = "Poll cadence (default 5, min 2)." },
                        maxMinutes = new { type = "integer", description = "Max time to wait (default 20)." }
                    }, required = Array.Empty<string>() }),
            };

            if (includeComputerUse)
                AddComputerUseTools(tools, Tool);

            return tools;
        }

        /// <summary>
        /// Computer-use ("host control") tools: drive the real Windows desktop (mouse/keyboard/screen) and
        /// the browser. Each returns a text observation; perception tools also return a screenshot fed to
        /// the vision model. Coordinates are in the pixel space of the most recent screenshot. Irreversible
        /// actions (pay/submit/order/send) MUST go through computer_confirm_and_click / computer_confirm_action,
        /// which block on Klive's approval. Only added to the catalogue when KliveAgent_ComputerUseEnabled is on.
        /// </summary>
        private static void AddComputerUseTools(List<HFWrapper.HFTool> tools, Func<string, string, object, HFWrapper.HFTool> Tool)
        {
            object Obj(object props, params string[] required) => new { type = "object", properties = props, required = required };
            var strType = new { type = "string" };
            var intType = new { type = "integer" };

            // The shared visual surface is also used by isolated Project desktops.  Host-only
            // approval, handoff, and encrypted-memory operations are appended below.
            tools.AddRange(VisualComputerToolCatalog.Build(new ComputerCapabilities
            {
                SupportsWindowControl = true,
                SupportsBrowserControl = true,
                SupportsClipboard = true,
                SupportsAppLaunch = true,
                SupportsRelativeMouse = true,
                SupportsHumanization = true,
                SupportsMotionFrames = true,
            }));

            tools.Add(Tool("computer_window_state",
                "Report the active window (title/pos/size), the virtual-screen size, and whether OS-level control is available.", Obj(new { })));
            tools.Add(Tool("computer_read_screen", "List visible window titles/sizes. For content, use computer_screenshot or computer_find_text.", Obj(new { })));
            tools.Add(Tool("computer_mouse_move_relative", "Move the mouse by a raw relative delta for games that ignore absolute pointer movement.", Obj(new { dx = intType, dy = intType, steps = intType }, "dx", "dy")));

            /*tools.Add(Tool("computer_screenshot",
                "Capture the screen as an image you can SEE (fed to your vision). Returns the pixel size; coordinates you give to computer_click/move/etc. are in THIS image's pixel space. ALWAYS screenshot before clicking blind.",
                Obj(new { target = new { type = "string", description = "\"active\" (active window, default), \"fullscreen\", or \"browser\" (the controlled Chrome viewport)." } })));
            tools.Add(Tool("computer_window_state", "Report the active window (title/pos/size), the virtual-screen size, and whether OS-level control is available.", Obj(new { })));
            tools.Add(Tool("computer_read_screen", "List the visible windows (titles/sizes). For page/app CONTENT, call computer_screenshot and read it visually.", Obj(new { })));
            tools.Add(Tool("computer_move", "Move the mouse to (x,y) in the last screenshot's pixel space (read the coordinate-ruler grid to measure).", Obj(new { x = intType, y = intType }, "x", "y")));
            tools.Add(Tool("computer_click",
                "Click at (x,y) — measure from the screenshot's coordinate-ruler grid. button:\"left\"(default)/\"right\"/\"middle\"; clicks:2 = double-click, 3 = triple. modifiers:[\"ctrl\"|\"shift\"|\"alt\"|\"win\"] are HELD during the click (ctrl-click, shift-click range-select, etc.). Reversible only — for irreversible/pay/submit/send buttons use computer_confirm_and_click.",
                Obj(new { x = intType, y = intType, button = strType, clicks = intType, modifiers = new { type = "array", items = strType } }, "x", "y")));
            tools.Add(Tool("computer_drag",
                "Press at (fromX,fromY), drag to (toX,toY), release. button:left(default)/right/middle; modifiers held during the drag. For drag-and-drop, selecting, sliders, reordering.",
                Obj(new { fromX = intType, fromY = intType, toX = intType, toY = intType, button = strType, modifiers = new { type = "array", items = strType } }, "fromX", "fromY", "toX", "toY")));
            tools.Add(Tool("computer_mouse_down", "Press and HOLD a mouse button (without releasing) at (x,y), or at the current cursor if omitted. Pair with computer_mouse_up. For manual drags, hold-to-select, drawing.", Obj(new { x = intType, y = intType, button = strType })));
            tools.Add(Tool("computer_mouse_up", "Release a held mouse button at (x,y), or at the current cursor if omitted.", Obj(new { x = intType, y = intType, button = strType })));
            tools.Add(Tool("computer_key_down", "Press and HOLD a key/modifier (e.g. \"shift\", \"ctrl\", \"a\", \"right\") without releasing (scan-code, so games honour it). Pair with computer_key_up. For hold-to-repeat, holding a modifier across actions, or holding a GAME movement key (e.g. key_down \"right\" to keep walking, then screenshot, then key_up).", Obj(new { key = strType }, "key")));
            tools.Add(Tool("computer_key_up", "Release a held key/modifier.", Obj(new { key = strType }, "key")));
            tools.Add(Tool("computer_release_all", "Release ALL currently-held mouse buttons and keys. Use to recover if a hold got stuck.", Obj(new { })));
            tools.Add(Tool("computer_scroll",
                "Scroll the page/content. {direction:\"down\"|\"up\"|\"left\"|\"right\", amount:N} where N = wheel notches (default 5). Defaults to DOWN. Pass x,y (in the gridded screenshot's pixel space) to scroll a specific pane; else it scrolls the screen centre. Scroll, then computer_screenshot to see the new content.",
                Obj(new { direction = strType, amount = intType, x = intType, y = intType, dy = intType, dx = intType })));
            tools.Add(Tool("computer_type",
                "Type text into the focused field. To enter a saved secret WITHOUT ever seeing it, embed its name in braces, e.g. \"{SainsburyEmail}\" — the harness substitutes the decrypted value at keystroke time. Click the field first.",
                Obj(new { text = strType }, "text")));
            tools.Add(Tool("computer_key", "Press a key or chord (sent as hardware scan codes, so GAMES register it too). Use {key:\"enter\"} or {keys:[\"ctrl\",\"v\"]}. Names: enter,tab,esc,space,backspace,delete,arrows(up/down/left/right),home,end,pageup,pagedown,f1..f12,ctrl,alt,shift,win,a-z,0-9. holdMs = how long to hold the press (default 55; raise to ~120 for stubborn games). repeats = tap it N times (e.g. move a menu cursor down 3).", Obj(new { key = strType, keys = new { type = "array", items = strType }, holdMs = intType, repeats = intType })));
            tools.Add(Tool("computer_mouse_move_relative", "Move the mouse by a RELATIVE delta (raw motion) — for 3D/FPS games that read raw mouse for look/aim and ignore absolute cursor moves. {dx,dy} in pixels (right/down positive); optional steps to smooth it. NOT for normal UI clicking — use computer_move/click there.", Obj(new { dx = intType, dy = intType, steps = intType })));
            tools.Add(Tool("computer_wait", "Wait for the UI to settle WITHOUT a hard timeout risk. Stops early when untilText appears (browser) or untilImageChange is true, else after maxMs (default 4000, capped 600000). Use for page loads/checkouts.", Obj(new { maxMs = intType, untilText = strType, untilImageChange = new { type = "boolean" } })));
            tools.Add(Tool("computer_focus_window", "Bring a window to the foreground by title substring or process name.", Obj(new { titleContains = strType, processName = strType })));
            tools.Add(Tool("computer_launch_app", "Launch an app/exe. Browsers (chrome/edge/firefox/…) are always allowed; other apps must be in KliveAgent_AppAllowList. The launched window is maximized.", Obj(new { path = strType, shellName = strType, args = strType })));
            tools.Add(Tool("computer_clipboard_get", "Read the Windows clipboard text.", Obj(new { })));
            tools.Add(Tool("computer_clipboard_set", "Set the Windows clipboard text.", Obj(new { text = strType }, "text")));
            tools.Add(Tool("computer_open_browser",
                "Open or focus the user's REAL system browser (maximized) so you can drive it like a human. No URL required. To actually go to a page, prefer computer_navigate. Returns a screenshot.",
                Obj(new { url = strType })));
            tools.Add(Tool("computer_navigate",
                "Go to a web page in ONE reliable step: focuses/opens the real browser, focuses the address bar, types the URL, presses Enter, and waits for the page to load. e.g. {url:\"openpsychometrics.org/tests/FSIQ/\"}. Use this instead of manually clicking the address bar.",
                Obj(new { url = strType }, "url")));*/
            tools.Add(Tool("computer_confirm_action",
                "GATE an irreversible non-click action (e.g. pressing Enter to submit/pay/send). Blocks until Klive approves on the website or Discord. On APPROVED, do the action next; on DENIED, stop and report.",
                Obj(new { summary = strType }, "summary")));
            tools.Add(Tool("computer_confirm_and_click",
                "GATE + perform an irreversible click (place order, confirm booking, final Pay, submit, send). Shows Klive the target screenshot and blocks until approved, then clicks (x,y). Use this for EVERY money/outward action.",
                Obj(new { x = intType, y = intType, summary = strType, button = strType }, "x", "y", "summary")));
            tools.Add(Tool("request_human",
                "Hand off to a human (Klive) when you hit something you can't or shouldn't do yourself: a CAPTCHA, a login wall, a 2FA/verification code, or you genuinely can't tell which control is correct. Klive gets a remote-desktop link, takes over the real screen, solves it, and you AUTO-RESUME exactly where you left off — so do NOT end your turn, do NOT abandon the task, and do NOT spin a retry loop. For a simple image-grid captcha you may attempt it yourself ~twice first, then call this. Blocks (no hard timeout) until it's handled.",
                Obj(new { reason = strType, maxMinutes = intType })));
            tools.Add(Tool("save_encrypted_memory",
                "Securely store a credential/secret (e.g. an email or password) under a name. The value is encrypted and NEVER shown back to you — reference it later as {Name} inside computer_type or computer_browser fill.",
                Obj(new { name = strType, value = strType }, "name", "value")));
            tools.Add(Tool("list_encrypted_memories", "List the NAMES of stored encrypted memories (never their values).", Obj(new { })));
            tools.Add(Tool("delete_encrypted_memory", "Delete a stored encrypted memory by name.", Obj(new { name = strType }, "name")));
        }

        /// <summary>
        /// Executes a native tool call (memory tools + `grep`) by dispatching to the live API (reusing
        /// ScriptGlobals so stats/side-effects match the script path). Returns a human-readable result string
        /// for the tool result. `grep` reuses the same code-search engine the scripts use, just without Roslyn.
        /// </summary>
        private static async Task<string> DispatchNativeToolAsync(ScriptGlobals globals, string toolName, string? argsJson)
        {
            // Service Surface (omniservice / omni_api / any generated per-service tool). Handled first:
            // these are the only tools whose names are not known at compile time, and the bridge owns
            // their argument contract, approval gate and audit.
            if (KliveAgentServiceTools.IsHandled(toolName))
            {
                var bridge = globals.AgentService?.ServiceTools;
                if (bridge == null)
                    return $"'{toolName}' is unavailable: the agent is still starting up. Try again in a moment.";
                return await bridge.DispatchAsync(toolName, argsJson, globals.CancellationToken);
            }

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
            bool BoolOr(string name, bool def)
            {
                if (!hasArgs || !root.TryGetProperty(name, out var e)) return def;
                return e.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.String => bool.TryParse(e.GetString(), out var b) ? b : def,
                    _ => def
                };
            }

            switch (toolName)
            {
                case "grep":
                {
                    var pattern = Str("pattern");
                    if (string.IsNullOrWhiteSpace(pattern)) return "Error: 'pattern' is required.";
                    var path = Str("path") ?? string.Empty;
                    var maxResults = IntOr("maxResults", 30);
                    return BoolOr("fixedString", false)
                        ? globals.SearchCode(pattern, path, maxResults)
                        : globals.SearchCodeRegex(pattern, path, maxResults);
                }
                case "read_file":
                {
                    var path = Str("path");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' is required.";
                    return globals.ReadFile(path, IntOr("startLine", 1), IntOr("maxLines", 200));
                }
                case "list_directory":
                    return globals.ListDirectory(Str("path") ?? string.Empty);
                case "get_global_path":
                {
                    var key = Str("key");
                    if (string.IsNullOrWhiteSpace(key)) return "Error: 'key' is required.";
                    return globals.GetGlobalPath(key);
                }
                case "search_knowledge":
                {
                    var query = Str("query");
                    if (string.IsNullOrWhiteSpace(query)) return "Error: 'query' is required.";
                    return await globals.SearchKnowledge(query, IntOr("maxResults", 8), BoolOr("includeMessages", true));
                }
                case "read_knowledge_doc":
                {
                    var docId = Str("docId") ?? Str("id");
                    if (string.IsNullOrWhiteSpace(docId)) return "Error: 'docId' is required.";
                    return globals.ReadKnowledgeDoc(docId, IntOr("maxTokens", 1500));
                }
                case "web_search":
                {
                    var query = Str("query");
                    if (string.IsNullOrWhiteSpace(query)) return "Error: 'query' is required.";
                    return await globals.WebSearch(query, IntOr("maxResults", 6), IntOr("fetchTop", 2), Str("timeRange"));
                }
                case "web_fetch":
                {
                    var url = Str("url");
                    if (string.IsNullOrWhiteSpace(url)) return "Error: 'url' is required.";
                    return await globals.WebFetch(url);
                }
                case "account_list":
                    return await globals.ListAccounts(Str("service"));
                case "account_register":
                {
                    var service = Str("service"); var username = Str("username");
                    if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(username))
                        return "Error: 'service' and 'username' are both required.";
                    var secrets = new Dictionary<string, string>();
                    if (hasArgs && root.TryGetProperty("secrets", out var se) && se.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var p in se.EnumerateObject())
                            secrets[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.ToString();
                    return await globals.RegisterAccount(service, username, Str("email"), secrets, Str("description"), BoolOr("allowDuplicate", false), Str("reason"));
                }
                case "account_update":
                {
                    var service = Str("service");
                    if (string.IsNullOrWhiteSpace(service)) return "Error: 'service' is required.";
                    return await globals.UpdateAccount(service, Str("username"), Str("addSecretName"), Str("addSecretValue"), Str("status"), Str("notes"),
                        Str("newUsername"), Str("newEmail"));
                }
                case "klivemail_create_mailbox":
                    return globals.AgentService == null ? "KliveAgent is not ready." :
                        await KliveAgentMailTools.CreateMailboxAsync(globals.AgentService, Str("address"), Str("displayName"), globals.CancellationToken);
                case "klivemail_list_messages":
                    return globals.AgentService == null ? "KliveAgent is not ready." :
                        await KliveAgentMailTools.ListMessagesAsync(globals.AgentService, Str("mailbox"), IntOr("limit", 20), BoolOr("unreadOnly", false), globals.CancellationToken);
                case "klivemail_get_message":
                    return globals.AgentService == null ? "KliveAgent is not ready." :
                        await KliveAgentMailTools.GetMessageAsync(globals.AgentService, Str("id"), globals.CancellationToken);
                case "klivemail_wait_for_email":
                    return globals.AgentService == null ? "KliveAgent is not ready." :
                        await KliveAgentMailTools.WaitForEmailAsync(globals.AgentService, Str("mailbox"), Str("fromContains") ?? Str("senderContains"),
                            Str("subjectContains"), IntOr("timeoutSeconds", 300), IntOr("lookbackSeconds", 900), globals.CancellationToken);
                case "notify_klives":
                {
                    var message = Str("message") ?? Str("text");
                    if (string.IsNullOrWhiteSpace(message)) return "Error: 'message' is required.";
                    return await globals.NotifyKlives(message);
                }
                case "recall_memories":
                    return FormatMemoriesResult(await globals.RecallMemories(Str("query") ?? string.Empty, IntOr("maxResults", 10), Str("since"), Str("until")));
                case "recall_memories_by_tag":
                    return FormatMemoriesResult(await globals.RecallMemoriesByTag(Str("tag") ?? string.Empty));
                case "save_memory":
                {
                    var content = Str("content");
                    if (string.IsNullOrWhiteSpace(content)) return "Error: 'content' is required.";
                    var id = await globals.SaveMemory(content, Strs("tags"), IntOr("importance", 1));
                    return $"Saved memory {id} at {Data_Handling.TemporalFormat.NowStamp()}.";
                }
                case "save_shortcut":
                {
                    var title = Str("title"); var content = Str("content");
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) return "Error: 'title' and 'content' are both required.";
                    var id = await globals.SaveShortcut(title, content, Strs("tags"));
                    return $"Saved shortcut {id} at {Data_Handling.TemporalFormat.NowStamp()}.";
                }
                case "get_shortcuts":
                    return await globals.GetShortcuts();
                case "schedule_task":
                {
                    var instruction = Str("instruction");
                    var dueAt = Str("dueAt") ?? Str("due_at") ?? Str("when");
                    if (string.IsNullOrWhiteSpace(instruction)) return "Error: 'instruction' is required.";
                    if (string.IsNullOrWhiteSpace(dueAt)) return "Error: 'dueAt' is required (e.g. \"in 2h\" or \"2026-07-15 09:00\").";
                    return await globals.ScheduleTask(instruction, dueAt, Str("repeatEvery") ?? Str("repeat_every"));
                }
                case "list_scheduled_tasks":
                    return globals.ListScheduledTasks();
                case "cancel_scheduled_task":
                {
                    var id = Str("id") ?? Str("taskId");
                    if (string.IsNullOrWhiteSpace(id)) return "Error: 'id' is required.";
                    return await globals.CancelScheduledTask(id);
                }
                case "delete_memory":
                {
                    var id = Str("id") ?? Str("idOrShortId");
                    if (string.IsNullOrWhiteSpace(id)) return "Error: 'id' is required.";
                    return await globals.DeleteMemory(id) ? $"Deleted memory {id}." : $"No memory matched '{id}'.";
                }
                default:
                    return $"Unknown native tool '{toolName}'.";
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
                sb.AppendLine($"[{shortId} · saved {Data_Handling.TemporalFormat.StampWithAge(m.CreatedAt)}] {m.Content}{tags}");
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

        // No retry/stuck ceilings on the agent's own work: the loop self-terminates only when the
        // LLM produces a no-script reply (the final answer). Repeated identical errors or scripts are
        // NOT force-finalized — long, exploratory, error-correcting chains are exactly how API
        // discovery makes progress, and each distinct compile error is a step closer, not a loop.
        // The only hard stops are broken-output circuit breakers (empty completions / malformed tool
        // envelopes) handled inline below, which guard a dead model channel that produces zero work.

        /// <summary>How many of the most recent agent turns replay their scripts+outputs into the
        /// conversation history. Recent turns are where "what did I just run / what did it return"
        /// matters most; older turns stay text-only to keep prompt cost bounded.</summary>
        private const int HistoryScriptRecentTurns = 3;

        /// <summary>How often a long-running tool reports that it is still running.</summary>
        internal static TimeSpan ToolPulseInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>A tool still running after this long stops being pulsed, so the stall watchdog can
        /// reclaim a tool that turned out to be unbounded. Every real tool finishes well inside it.</summary>
        internal static readonly TimeSpan ToolPulseCeiling = TimeSpan.FromMinutes(20);

        internal static string FormatElapsed(TimeSpan elapsed) =>
            elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m"
            : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";

        /// <summary>Backoff between failed model calls. A long computer task must ride out a provider
        /// blip at step forty; the old 1.5s/3s pair gave up inside five seconds.</summary>
        private static readonly double[] ModelRetryScheduleSeconds = { 3, 10, 30, 60, 120, 180 };
        private static readonly TimeSpan MaxModelRetryDelay = TimeSpan.FromMinutes(5);

        internal static TimeSpan ModelRetryDelay(int failedAttempt, Exception error)
        {
            if (error is RemoteLLMException { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero)
                return retryAfter > MaxModelRetryDelay ? MaxModelRetryDelay : retryAfter;
            int index = Math.Clamp(failedAttempt, 0, ModelRetryScheduleSeconds.Length - 1);
            return TimeSpan.FromSeconds(ModelRetryScheduleSeconds[index]);
        }

        /// <summary>A provider refusing the request because it carries images (the model has no vision).</summary>
        internal static bool LooksLikeImageRejection(Exception error)
        {
            if (error is not RemoteLLMException { Kind: RemoteLLMFailureKind.InvalidRequest or RemoteLLMFailureKind.ModelUnavailable } remote)
                return false;
            string message = remote.Message ?? "";
            return message.Contains("image", StringComparison.OrdinalIgnoreCase)
                || message.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || message.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
                || message.Contains("modalit", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Permanent provider errors fail fast; transport trouble (network, timeouts, rate
        /// limits, 5xx, a stalled stream, a malformed body) gets the full patient backoff. Anything
        /// else is more likely a bug than a blip: one quick retry, then the honest answer.</summary>
        internal static bool IsRetryableModelFailure(Exception error, int failedAttempt = 0) => error switch
        {
            RemoteLLMException remote => remote.IsRetryable,
            KliveLLMStreamStalledException or HttpRequestException or IOException or TimeoutException
                or TaskCanceledException or Newtonsoft.Json.JsonException or System.Text.Json.JsonException => true,
            _ => failedAttempt == 0,
        };

        internal static string DescribeModelError(Exception error) => error switch
        {
            KliveLLMStreamStalledException stalled => $"the model provider went silent for {stalled.Idle.TotalSeconds:0}s",
            RemoteLLMException { Kind: RemoteLLMFailureKind.RateLimited } => "the model provider is rate-limiting",
            RemoteLLMException { Kind: RemoteLLMFailureKind.Timeout } => "the model provider timed out",
            RemoteLLMException { Kind: RemoteLLMFailureKind.ProviderUnavailable } => "the model provider is unavailable",
            RemoteLLMException { Kind: RemoteLLMFailureKind.EmptyResponse } => "the model provider returned nothing",
            RemoteLLMException { Kind: RemoteLLMFailureKind.Network } => "a network error reaching the model provider",
            TaskCanceledException or TimeoutException => "the model provider timed out",
            HttpRequestException => "a network error reaching the model provider",
            _ => "the model call failed",
        };

        /// <summary>The final answer when the model layer could not be reached at all. Keeps whatever
        /// the run already said, and says plainly that nothing more was attempted.</summary>
        internal static string DescribeModelFailure(Exception error, int attempts, string progressSoFar)
        {
            string cause = error is RemoteLLMException remote && !remote.IsRetryable
                ? $"The model provider rejected the request ({remote.Kind}): {Trim(remote.Message, 300)}"
                : $"I couldn't reach the model after {attempts} attempt{(attempts == 1 ? "" : "s")} — {DescribeModelError(error)}.";
            string body = $"_({cause} I stopped here; send the message again to retry.)_";
            return string.IsNullOrWhiteSpace(progressSoFar) ? body : progressSoFar.Trim() + "\n\n" + body;

            static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
        }

        /// <summary>Tools that act on the outside world. Using one turns the run into a multi-step task.</summary>
        internal static bool IsTaskTool(string toolName) =>
            toolName.StartsWith("computer_", StringComparison.Ordinal)
            || toolName.StartsWith("klivemail_", StringComparison.Ordinal)
            || toolName is "account_register" or "account_update" or "request_human" or "notify_klives";

        /// <summary>
        /// What the model is told after a step. A question-answering turn should stop the moment it has
        /// its answer; a TASK should not — "those calls succeeded, answer now" after the first successful
        /// click on a signup page is how a twenty-step job ends at step two with a progress report.
        /// </summary>
        internal static string BuildStepGuidance(bool outputBroken, bool cleanProgress, bool taskMode)
        {
            if (outputBroken)
                return "[Output error] Your recent replies weren't valid actions (empty, or malformed tool/XML envelopes that ran nothing). STOP and reply with a final text-only answer that honestly reports what you tried, what worked, and what blocked you.";
            if (taskMode)
                return cleanProgress
                    ? "Those steps succeeded. Verify the new state if it matters, then continue with the NEXT step of the task. Give your final answer only when EVERY part of Klives' request is done and verified (or something genuinely cannot be done) — never stop to report partial progress."
                    : "Read the result above and adjust — a failed step means change the approach (re-inspect the page, use a different control, wait for the page, dismiss an overlay), not repeat it. Keep going until every part of the request is done; finalize only if something is genuinely impossible after trying alternatives.";
            return cleanProgress
                ? "Those calls succeeded. If they answer the user's question, give the final text-only answer NOW (no scripts/tools) — do NOT run more lookups 'to be safe'. Continue only if a SPECIFIC, named piece of the answer is still genuinely missing."
                : "If you have what you need, give the final answer now (no scripts). Otherwise run your next script — prefer ONE composite block over several tiny ones.";
        }

        /// <summary>Truthful closing line for a run that was stopped rather than finished.</summary>
        internal static string DescribeStop(string? reason, string? detail, string lastStatus, TimeSpan elapsed, bool producedOutput,
            bool resumes = false)
        {
            // Status notes are often markdown-italicised ("_…thinking (step 3)_"); quote the words only.
            string status = (lastStatus ?? "").Trim().Trim('_').Trim().TrimStart('…').Trim().TrimEnd('.');
            string doing = status.Length == 0 ? "" : $" I was {status} at the time.";
            return reason switch
            {
                AgentChatRunControl.StopReasonUser => $"_(Stopped by you after {FormatElapsed(elapsed)}.)_",
                AgentChatRunControl.StopReasonStall =>
                    $"_(Stopped automatically: {(string.IsNullOrWhiteSpace(detail) ? "no progress for too long" : detail)}."
                    + $"{(detail?.Contains("last activity", StringComparison.OrdinalIgnoreCase) == true ? "" : doing)} Send the message again to retry.)_",
                AgentChatRunControl.StopReasonShutdown => resumes
                    ? $"_(Interrupted: KliveAgent was restarted or shut down.{doing} I'll continue automatically once it's back.)_"
                    : $"_(Interrupted: KliveAgent was restarted or shut down.{doing} Send the message again to continue.)_",
                _ => producedOutput
                    ? "_(Run stopped before completion.)_"
                    : $"_(Run stopped before I produced any output.{doing})_",
            };
        }

        /// <summary>What one model call costs against the run budget: uncached prompt plus completion,
        /// with cached reads at the ~10% price providers charge for them. Cumulative raw prompt tokens
        /// would instead count the same transcript once per step.</summary>
        internal static long BillableTokens(int promptTokens, int cachedPromptTokens, int completionTokens)
        {
            int cached = Math.Clamp(cachedPromptTokens, 0, Math.Max(0, promptTokens));
            return (long)Math.Max(0, promptTokens - cached) + Math.Max(0, completionTokens) + cached / 10;
        }

        /// <summary>
        /// Implements the wait_for tool: poll an observable (an HTTP url, or a C# probe script) on an
        /// interval and return when it changes / contains text / is true, or when maxMinutes elapses.
        /// Heartbeats via <paramref name="heartbeat"/> (wired by the caller to ReportProgress so the stall
        /// watchdog sees a *waiting* run, not a hung one), honours the run cancellation token, and is NOT
        /// bound by the per-script 30s timeout — each individual probe is a short script, the overall wait
        /// is orchestrated here on the native-tool path.
        /// </summary>
        private async Task<(bool ok, string text)> RunWaitForAsync(string? argsJson, CancellationToken ct, Action<string> heartbeat)
        {
            System.Text.Json.JsonElement a;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
                a = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object ? doc.RootElement : System.Text.Json.JsonDocument.Parse("{}").RootElement;
            }
            catch { a = System.Text.Json.JsonDocument.Parse("{}").RootElement; }

            string? Str(string n) => a.TryGetProperty(n, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
            int IntOr(string n, int def) => a.TryGetProperty(n, out var e) && e.TryGetInt32(out var v) ? v : def;

            string? url = Str("url");
            string? check = Str("check");
            string watch = (Str("watch") ?? "").Trim().ToLowerInvariant();

            string until = (Str("until") ?? "change").Trim().ToLowerInvariant();
            string? target = Str("target");
            int interval = Math.Clamp(IntOr("intervalSeconds", await agentService.GetIntOmniSetting("KliveAgent_WaitPollSeconds", 5)), 2, 300);
            int maxMin = Math.Clamp(IntOr("maxMinutes", await agentService.GetIntOmniSetting("KliveAgent_MaxWaitMinutes", 20)), 1, 720);

            // SCREEN watcher: "wait until the screen changes" — the right tool when waiting on a GUI/game/another
            // player (e.g. an opponent's move) where the observable is the screen itself. Explicit watch:"screen",
            // or implied when no url/check is given. Uses a perceptual diff so a ticking clock / cursor blink
            // doesn't count, but a real change (a piece moves, a dialog appears) does. Always bounded; returns the
            // moment it changes so the next turn can screenshot and react.
            bool screenMode = watch is "screen" or "display" || (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(check));
            if (screenMode)
            {
                var hcm = agentService.GetServiceByName("HostControlManager") as HostControlManager;
                if (hcm == null)
                    return (false, "Can't watch the screen — HostControl isn't available. Provide 'url' or 'check', or use computer_wait.");

                double threshold = a.TryGetProperty("threshold", out var te) && te.TryGetDouble(out var tv) ? tv : 2.0;
                threshold = Math.Clamp(threshold, 0.5, 64);
                var baseSig = hcm.CaptureScreenSignature();
                var swScreen = System.Diagnostics.Stopwatch.StartNew();
                long maxMsScreen = (long)maxMin * 60_000L;
                int lastHbScreen = 0;
                double maxSeen = 0;
                while (swScreen.ElapsedMilliseconds < maxMsScreen)
                {
                    if (ct.IsCancellationRequested) return (false, "Wait cancelled (run stopped).");
                    try { await Task.Delay(interval * 1000, ct); }
                    catch (OperationCanceledException) { return (false, "Wait cancelled (run stopped)."); }

                    var cur = hcm.CaptureScreenSignature();
                    if (baseSig == null) { baseSig = cur; continue; } // recover if the first capture failed
                    if (cur != null)
                    {
                        double d = HostControlManager.ScreenSignatureDelta(baseSig, cur);
                        if (d > maxSeen) maxSeen = d;
                        if (d >= threshold)
                            return (true, $"The screen changed after {swScreen.ElapsedMilliseconds / 1000}s (Δ={d:F1} ≥ {threshold:F1}). Take a screenshot to see the new state and continue.");
                    }
                    if (swScreen.ElapsedMilliseconds - lastHbScreen >= 5000)
                    {
                        lastHbScreen = (int)swScreen.ElapsedMilliseconds;
                        heartbeat($"watching the screen for a change… ({swScreen.ElapsedMilliseconds / 1000}s elapsed)");
                    }
                }
                return (true, $"Watched the screen for {maxMin} min with no change ≥ threshold {threshold:F1} (biggest change seen was Δ={maxSeen:F1}). If the event likely DID happen, screenshot now or wait_for again with threshold a little below {maxSeen:F1}; otherwise proceed.");
            }

            // Probe via a DEDICATED throwaway script session so it can't pollute the agent's main session.
            KliveAgentScriptEngine.ScriptExecutionSession? probe =
                string.IsNullOrWhiteSpace(check) ? null : scriptEngine.CreateSession(new ScriptGlobals(agentService, ct));
            using var http = string.IsNullOrWhiteSpace(url) ? null : new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(25) };

            async Task<string> Observe()
            {
                if (http != null)
                {
                    try { return await http.GetStringAsync(url!, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return "HTTP_ERROR: " + ex.Message; }
                }
                var r = await probe!.ExecuteAsync(check!, TimeSpan.FromSeconds(25));
                return r.Success ? (r.Output ?? string.Empty) : ("PROBE_ERROR: " + r.ErrorMessage);
            }

            static bool Truthy(string s)
            {
                s = s.Trim();
                return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Contains("True", StringComparison.Ordinal);
            }
            bool Met(string baseline, string cur) => until switch
            {
                "contains" => target != null && cur.Contains(target, StringComparison.OrdinalIgnoreCase),
                "true" => Truthy(cur),
                _ => !string.Equals(cur.Trim(), baseline.Trim(), StringComparison.Ordinal),
            };

            static string Trunc(string s) => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= 600 ? s : s.Substring(0, 600) + "…");

            string baseline;
            try { baseline = await Observe(); }
            catch (OperationCanceledException) { return (false, "Wait cancelled (run stopped)."); }

            // contains/true may already hold on the first look.
            if (until != "change" && Met(baseline, baseline))
                return (true, $"Condition already satisfied. Value: {Trunc(baseline)}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long maxMs = (long)maxMin * 60_000L;
            int lastHb = 0;
            int errStreak = 0;
            while (sw.ElapsedMilliseconds < maxMs)
            {
                if (ct.IsCancellationRequested) return (false, "Wait cancelled (run stopped).");
                try { await Task.Delay(interval * 1000, ct); }
                catch (OperationCanceledException) { return (false, "Wait cancelled (run stopped)."); }

                string cur;
                try { cur = await Observe(); }
                catch (OperationCanceledException) { return (false, "Wait cancelled (run stopped)."); }

                // A transient fetch error is not "the thing happened" — unless we were waiting for the
                // observable to come BACK (baseline was itself an error). Otherwise keep waiting.
                bool curErr = cur.StartsWith("HTTP_ERROR", StringComparison.Ordinal) || cur.StartsWith("PROBE_ERROR", StringComparison.Ordinal);
                bool baseErr = baseline.StartsWith("HTTP_ERROR", StringComparison.Ordinal) || baseline.StartsWith("PROBE_ERROR", StringComparison.Ordinal);

                // A BROKEN probe (compile error, bad URL, screenshot that always fails) must not silently burn
                // the whole maxMinutes looking like a hang. If it keeps erroring and we aren't explicitly
                // waiting for it to recover, bail early with the error so the agent fixes its approach.
                if (curErr && !baseErr)
                {
                    if (++errStreak >= 4)
                        return (false, $"wait_for's {(http != null ? "url" : "check")} kept failing ({errStreak}× in a row): {Trunc(cur)}\nFix the probe, or use watch:\"screen\" / computer_wait instead of polling a broken check.");
                }
                else errStreak = 0;

                if (!(curErr && !baseErr) && Met(baseline, cur))
                    return (true, $"Condition met after {sw.ElapsedMilliseconds / 1000}s. Current value:\n{Trunc(cur)}");

                if (sw.ElapsedMilliseconds - lastHb >= 5000)
                {
                    lastHb = (int)sw.ElapsedMilliseconds;
                    heartbeat($"waiting for {(until == "change" ? "a change" : until == "contains" ? $"\"{target}\"" : "the condition")}… ({sw.ElapsedMilliseconds / 1000}s elapsed)");
                }
            }
            return (true, $"Waited {maxMin} min with no match (until={until}). Last value:\n{Trunc(baseline)}\nDecide whether to wait_for again or proceed.");
        }

        public async Task<AgentChatResponse> ProcessMessageAsync(
            string userMessage,
            AgentConversation conversation,
            string? senderName = null,
            Action<AgentProgressUpdate>? onProgress = null,
            CancellationToken cancellationToken = default,
            AgentChatRunControl? runControl = null,
            Action<AgentSteeringMessage>? onSteeringApplied = null,
            IReadOnlyList<AgentAttachment>? messageAttachments = null)
        {
            var performance = new KliveAgentPerformance();
            var preparationTimer = System.Diagnostics.Stopwatch.StartNew();
            bool preparationRecorded = false;
            int firstVisibleTextSeen = 0;
            int totalPromptTokens = 0;
            int totalCompletionTokens = 0;
            int iterationsDone = 0;
            try
            {
                var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();
                // Accumulates the agent's conversational prose across iterations so the user can be
                // shown it "talking" (via onProgress) while its scripts are still running.
                var progressText = new StringBuilder();
                // Background pulses (model activity, long tools) report concurrently with the loop.
                var progressGate = new object();
                string lastStatusNote = "preparing";
                // The container desktop this run drives (once known) — the website streams it live.
                string? computerContainerId = null;
                var llmSessionId = $"kliveagent-{conversation.ConversationId}";
                // The first progress used to arrive only once the system prompt, tool catalogue and
                // attachments were all built; report immediately so the run is visibly alive.
                Pulse("preparing", "preparing context");
                var cacheRunId = Guid.NewGuid().ToString("N");

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
                var settingsTask = KliveAgentRunSettings.LoadAsync(agentService);
                var nativeToolsTask = llm.SupportsNativeToolCallingAsync();
                var flatFeeTask = llm.IsFlatFeeProviderAsync();
                _ = llm.WarmUpConnectionAsync(cancellationToken);

                // Prefer the provider's native structured tool-calling channel: code travels as a JSON
                // string argument, immune to the {{{ }}} framing leaks and brace-truncation that broke the
                // text protocol. Fall back to the text protocol when the active provider is local (no tool
                // channel) or the capability probe fails.
                bool useToolCalling;
                try { useToolCalling = await nativeToolsTask; }
                catch { useToolCalling = false; }

                // Whether this turn's tokens carry a per-token price. A flat-fee router (AIRouter)
                // still produces real token counts, but no money moves with them, so the stats
                // yardstick must not bill them — see KliveAgentStats.EstimateCost.
                bool flatFeeProvider;
                try { flatFeeProvider = await flatFeeTask; }
                catch { flatFeeProvider = false; }
                var settings = await settingsTask;

                // Per-token streaming: when on (and we have a live progress channel), the model's output
                // is surfaced token-by-token as it generates. Configurable so it can be turned off.
                bool streamTokens = onProgress != null && settings.StreamTokens;

                // Computer-use tools are ON by default (set KliveAgent_ComputerUseEnabled=false to disable)
                // and only available on the structured tool-calling path. The computer itself is chosen
                // once per run: KliveAgent's own isolated desktop by default, the host desktop when
                // configured (KliveAgent_ComputerTarget). Irreversible actions still route through the
                // human approval gate on either.
                var computerTarget = useToolCalling && settings.ComputerUseEnabled
                    ? agentService.Computer.ResolveTarget(settings.ComputerTarget)
                    : KliveAgentComputerTarget.None;
                bool computerUseEnabled = computerTarget != KliveAgentComputerTarget.None;
                if (computerTarget == KliveAgentComputerTarget.Container)
                    computerContainerId = agentService.Computer.DesktopContainerID;
                // Raw screenshots reach the model only when this is on; DOM, OCR and terminal
                // perception keep working without it.
                bool visionEnabled = settings.VisionEnabled;

                // Context compaction for the vision loop: how many recent screenshots to keep in the tool
                // session (older ones are flattened to a one-line note so a long GUI task can't overflow the
                // model's context window and start hallucinating stale state).
                int retainedScreenshots = settings.RetainedScreenshots;

                var systemPromptTask = performance.MeasureAsync("systemPrompt", () =>
                    BuildSystemPrompt(userMessage, conversation, toolCallingMode: useToolCalling, computerUseEnabled: computerUseEnabled,
                        computerTarget: computerTarget, visionEnabled: visionEnabled, performance: performance));
                var toolDefinitions = useToolCalling
                    ? BuildToolDefinitions(includeComputerUse: computerTarget == KliveAgentComputerTarget.Host)
                    : null;
                if (toolDefinitions != null && computerTarget == KliveAgentComputerTarget.Container)
                    toolDefinitions.AddRange(KliveAgentComputer.BuildContainerToolDefinitions(visionEnabled));
                async Task<List<HFWrapper.HFTool>> PrepareServiceToolsAsync()
                {
                    if (toolDefinitions == null || agentService.ServiceTools == null) return new();
                    try
                    {
                        return await performance.MeasureAsync("toolDefinitions", agentService.ServiceTools.BuildToolDefinitionsAsync);
                    }
                    catch (Exception ex)
                    {
                        await agentService.ServiceLogError(ex, "Failed to build service tools; continuing without them.");
                        return new();
                    }
                }
                var serviceToolsTask = PrepareServiceToolsAsync();
                string systemPrompt = await systemPromptTask;

                // The Service Surface: dedicated tools for the pinned services plus the two universals,
                // so every OmniService is directly callable. Appended (never interleaved) and built from
                // an immutable catalogue, so the offered array stays byte-stable between runs with the
                // same settings — a churning tools array misses the provider's prefix cache on every
                // request.
                if (toolDefinitions != null && agentService.ServiceTools != null)
                {
                    toolDefinitions.AddRange(await serviceToolsTask);
                }

                llm.ResetSession(llmSessionId);

                var allScriptsExecuted = new List<AgentScriptResult>();
                // Thread the per-run cancellation token into ScriptGlobals so a manual Stop or the stall
                // watchdog also unwinds a script that is mid-execution (ExecuteAsync links this token with
                // its own per-script timeout).
                var sharedGlobals = new ScriptGlobals(agentService, cancellationToken)
                {
                    ConversationId = conversation.ConversationId,
                };
                var scriptSession = scriptEngine.CreateSession(sharedGlobals);

                // Per-script hard timeout. A script that overruns this is abandoned so the agent never
                // hangs; configurable so a legitimately long-running script can be given more room.
                int scriptTimeoutSec = settings.ScriptTimeoutSeconds;
                var scriptTimeout = TimeSpan.FromSeconds(scriptTimeoutSec);

                // Per-role models for adaptive routing (OpenRouter / native tool-calling path). Cheap,
                // on-track turns take the fast model; turns that escalate reasoning effort take the stronger
                // reasoning model. Empty = fall back to the provider's configured default model, so this is
                // fully opt-in and decoupled from KliveLLM's global OpenRouterModelID.
                string fastModel = settings.FastModel;
                string reasoningModel = settings.ReasoningModel;

                // Per-run guardrails: the agentic loop has no iteration cap by design, but an unbounded
                // token/time budget can burn real money if the model never emits a final answer. Soft
                // warn at 80% (nudge it to wrap up), hard-stop at 100% (demand a final answer, then end).
                // 0 disables either cap. The token cap measures BILLABLE tokens and is skipped entirely
                // on a flat-fee provider: a computer task re-sends its whole context every step, so
                // cumulative prompt tokens measure step count, not spend.
                int maxRunTokens = flatFeeProvider ? 0 : settings.MaxRunBillableTokens;
                int maxRunMinutes = settings.MaxTaskMinutes;
                int maxLlmRetries = settings.ModelRetryAttempts;
                long billableTokens = 0;
                var streamIdleLimit = TimeSpan.FromSeconds(settings.ModelStreamIdleSeconds);

                string userPrompt;
                using (performance.Measure("conversationPrompt"))
                    userPrompt = BuildUserPrompt(conversation, userMessage, senderName);
                (string text, List<(byte[] data, string mimeType)> images) preparedAttachments;
                try
                {
                    using (performance.Measure("attachments"))
                        preparedAttachments = await agentService.Attachments.PrepareForModelAsync(
                            messageAttachments, cancellationToken);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.ComponentModel.Win32Exception)
                {
                    return new AgentChatResponse
                    {
                        ConversationId = conversation.ConversationId, Success = false,
                        Response = "I could not read the attachment: " + ex.Message,
                        ErrorMessage = ex.Message
                    };
                }
                if (!string.IsNullOrEmpty(preparedAttachments.text))
                    userPrompt += "\n[Files attached to this message]\n" + preparedAttachments.text;
                if (preparedAttachments.images.Count > 0 && !useToolCalling)
                    return new AgentChatResponse
                    {
                        ConversationId = conversation.ConversationId, Success = false,
                        Response = "The active model cannot receive image or video attachments. Select a vision-capable remote model with native tool calling.",
                        ErrorMessage = "Vision input is unavailable."
                    };
                if (preparedAttachments.images.Count > 0)
                {
                    var modelsToCheck = new[] { fastModel, reasoningModel }
                        .Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal).ToList();
                    if (string.IsNullOrWhiteSpace(fastModel) || string.IsNullOrWhiteSpace(reasoningModel))
                        modelsToCheck.Add("");
                    foreach (string model in modelsToCheck)
                    {
                        var capabilities = await llm.GetModelCapabilitiesAsync(
                            string.IsNullOrEmpty(model) ? null : model, cancellationToken);
                        if (!capabilities.ImageInput)
                            return new AgentChatResponse
                            {
                                ConversationId = conversation.ConversationId, Success = false,
                                Response = $"The selected model {(string.IsNullOrEmpty(model) ? "" : model)} does not accept image input. Choose a vision-capable model for images or video.",
                                ErrorMessage = "The selected model does not accept image input."
                            };
                    }
                }

                // Tool-calling mode: seed the structured session with system + user once; later turns are
                // appended as tool-results / user-guidance. Text mode: keep the system prompt for iter 0
                // and feed observations back via currentPrompt.
                if (useToolCalling)
                {
                    llm.StartToolSession(llmSessionId, systemPrompt);
                    if (preparedAttachments.images.Count > 0)
                        llm.AppendUserContentToToolSession(llmSessionId, userPrompt, preparedAttachments.images,
                            keepRecentImages: retainedScreenshots, preservePromptPrefix: true);
                    else llm.AppendUserMessageToToolSession(llmSessionId, userPrompt);
                }
                var currentPrompt = userPrompt;
                string? firstIterationSystemPrompt = systemPrompt;
                bool stuckForceFinal = false;
                bool finalFormatRetryUsed = false;
                int emptyFinalRetries = 0;
                int consecutiveNoOpResponses = 0;
                // Per-run budget state (see maxRunTokens/maxRunMinutes above).
                bool budgetWarned = false;
                bool budgetForceFinal = false;
                // Set once the run acts on the world (desktop, mail, accounts); see BuildStepGuidance.
                bool taskMode = false;
                int unfinishedTaskNudges = 0;
                // Hand-back guard: a run that ends to ask Klives to clear a CAPTCHA loses its live desktop
                // session; request_human keeps it. Off once a takeover has timed out or been declined.
                int handBackNudges = 0;
                bool humanHandoffUnavailable = false;
                bool visionFallbackUsed = false;
                // Number of consecutive iterations whose scripts produced at least one error. Drives the
                // adaptive thinking budget: a clean cheap turn asks for low reasoning effort; we escalate
                // toward the user's ceiling as the task shows it's hard.
                int consecutiveErrorIters = 0;

                // Streams the agent's current prose + the scripts it has run so far to the live
                // progress channel. NEVER fabricates placeholder text: if the model hasn't actually
                // said anything yet, we send only the live work-status note (or nothing) — no canned
                // "On it." filler.
                // Emits a structured progress update: the agent's accumulated prose (+ optional running
                // note), the scripts run so far, and live transparency fields (phase, iteration, running
                // token totals, and an optional new activity-timeline event). NEVER fabricates placeholder
                // prose — Text is left null when the model hasn't actually said anything, so the host won't
                // overwrite the bubble with filler; status/activity still flow.
                void ReportProgress(string phase, string runningNote, AgentActivityEvent newActivity = null, string? liveText = null, byte[] frame = null, PendingApproval approval = null)
                {
                    if (!string.IsNullOrWhiteSpace(runningNote)) lastStatusNote = runningNote;
                    if (onProgress == null) return;
                    // Compose: committed prose from prior turns + the tokens streaming in this turn (if any)
                    // + an optional status note. liveText is the in-flight model output for the CURRENT turn,
                    // shown token-by-token before it's parsed and committed to progressText.
                    string? body;
                    List<AgentScriptResult> scriptsSnapshot;
                    lock (progressGate)
                    {
                        var composed = new StringBuilder();
                        var committed = progressText.ToString();
                        if (committed.Length > 0) composed.Append(committed);
                        if (!string.IsNullOrEmpty(liveText))
                        {
                            if (composed.Length > 0) composed.Append("\n\n");
                            composed.Append(liveText);
                        }
                        if (!string.IsNullOrWhiteSpace(runningNote))
                        {
                            if (composed.Length > 0) composed.Append("\n\n");
                            composed.Append(runningNote);
                        }
                        body = composed.Length > 0 ? composed.ToString() : null;
                        scriptsSnapshot = new List<AgentScriptResult>(allScriptsExecuted);
                    }
                    try
                    {
                        onProgress(new AgentProgressUpdate
                        {
                            Text = body,
                            Scripts = scriptsSnapshot,
                            Iteration = iterationsDone,
                            Phase = phase,
                            StatusNote = runningNote,
                            PromptTokens = totalPromptTokens,
                            CompletionTokens = totalCompletionTokens,
                            NewActivity = newActivity,
                            Frame = frame,
                            Approval = approval,
                            ComputerContainerId = computerContainerId
                        });
                    }
                    catch { }
                }

                // A liveness update that leaves the answer text alone: only phase/status (and an optional
                // timeline entry) change. Background pulses use this, so a heartbeat can never overwrite
                // tokens that are streaming into the bubble at the same moment.
                void Pulse(string phase, string statusNote, AgentActivityEvent? newActivity = null)
                {
                    if (!string.IsNullOrWhiteSpace(statusNote)) lastStatusNote = statusNote;
                    if (onProgress == null) return;
                    try
                    {
                        onProgress(new AgentProgressUpdate
                        {
                            Text = null,
                            Scripts = null,
                            Iteration = iterationsDone,
                            Phase = phase,
                            StatusNote = statusNote,
                            PromptTokens = totalPromptTokens,
                            CompletionTokens = totalCompletionTokens,
                            NewActivity = newActivity,
                        });
                    }
                    catch { }
                }

                // What the model layer is doing, translated for the person watching. Every report is
                // evidence the request is alive (queued for a slot, prefilling, reasoning, retrying),
                // which is what keeps the stall watchdog from killing a healthy-but-silent call.
                KliveLLM.KliveLLMActivityKind? lastModelActivity = null;
                void OnModelActivity(KliveLLM.KliveLLMActivity activity)
                {
                    int step = Math.Max(1, iterationsDone);
                    string elapsed = activity.Elapsed >= TimeSpan.FromSeconds(1) ? $" · {FormatElapsed(activity.Elapsed)}" : "";
                    bool transition = lastModelActivity != activity.Kind;
                    lastModelActivity = activity.Kind;
                    switch (activity.Kind)
                    {
                        case KliveLLM.KliveLLMActivityKind.Queued:
                            Pulse("queued", $"{activity.Detail}{elapsed}",
                                transition ? new AgentActivityEvent { Iteration = step, Kind = "wait", Text = "queued for a model slot" } : null);
                            break;
                        case KliveLLM.KliveLLMActivityKind.Retrying:
                            Pulse("retrying", activity.Detail,
                                transition ? new AgentActivityEvent { Iteration = step, Kind = "wait", Text = activity.Detail } : null);
                            break;
                        case KliveLLM.KliveLLMActivityKind.Reasoning:
                            Pulse("thinking", $"thinking (step {step}) — {activity.Detail}{elapsed}");
                            break;
                        case KliveLLM.KliveLLMActivityKind.Streaming:
                            Pulse("thinking", $"{activity.Detail} (step {step})");
                            break;
                        default:
                            Pulse("thinking", $"{activity.Detail}{elapsed} (step {step})");
                            break;
                    }
                }

                // Runs a tool while reporting that it is still running. Tools are individually bounded
                // (script timeout, container action deadlines, HTTP timeouts), so pulsing them is honest;
                // the ceiling stops an unexpectedly unbounded one from being kept alive forever.
                async Task<T> WithToolPulse<T>(Task<T> work, string label, TimeSpan? ceiling = null)
                {
                    if (onProgress == null || work.IsCompleted) return await work;
                    var limit = ceiling ?? ToolPulseCeiling;
                    var running = System.Diagnostics.Stopwatch.StartNew();
                    while (!work.IsCompleted)
                    {
                        await Task.WhenAny(work, Task.Delay(ToolPulseInterval));
                        if (!work.IsCompleted && running.Elapsed < limit && !cancellationToken.IsCancellationRequested)
                            Pulse("running", $"{label} · {FormatElapsed(running.Elapsed)}");
                    }
                    return await work;
                }

                // Deliver a retry/guidance/observation prompt the right way for the active mode: a user-role
                // turn in the tool session, or the next currentPrompt in the text-protocol loop.
                void SendModelPrompt(string text)
                {
                    if (useToolCalling) llm.AppendUserMessageToToolSession(llmSessionId, text);
                    else currentPrompt = text;
                }

                void ApplySteering(IReadOnlyList<AgentSteeringMessage>? messages)
                {
                    if (messages == null || messages.Count == 0) return;
                    var guidance = new StringBuilder();
                    guidance.AppendLine("[New user steering - apply this before continuing]");
                    foreach (var steering in messages.OrderBy(x => x.CreatedAt))
                    {
                        steering.Status = "applied";
                        steering.AppliedAt = DateTime.UtcNow;
                        guidance.AppendLine(
                            $"[{Data_Handling.TemporalFormat.StampMinute(steering.CreatedAt)}] {steering.SenderName}: {steering.Message}");
                        try { onSteeringApplied?.Invoke(steering); } catch { }
                        ReportProgress("steering", "_...applying new guidance_", new AgentActivityEvent
                        {
                            Iteration = Math.Max(1, iterationsDone),
                            Kind = "steer",
                            Text = steering.Message.Length > 200
                                ? steering.Message.Substring(0, 200) + "..."
                                : steering.Message
                        });
                    }

                    string text = guidance.ToString().TrimEnd();
                    if (useToolCalling)
                        llm.AppendUserMessageToToolSession(llmSessionId, text);
                    else
                        currentPrompt = currentPrompt + "\n\n" + text;
                }

                // Adaptive reasoning effort for the upcoming LLM call. Cheap, on-track turns ask for LOW
                // effort (no wasted reasoning tokens — the dominant per-turn latency cost). We escalate
                // toward MEDIUM/HIGH only when the task shows it's hard: recent script errors, broken-output
                // no-ops, or a long-running loop. KliveLLM clamps this request to the user's configured
                // ThinkingType, so it never exceeds what they allow and collapses to a no-op at a fixed level.
                string DetermineThinkingLevel(int iter)
                {
                    if (stuckForceFinal || consecutiveErrorIters >= 2 || iter >= 12) return "high";
                    if (consecutiveErrorIters >= 1 || consecutiveNoOpResponses >= 1 || iter >= 6) return "medium";
                    return "low";
                }

                // Adaptive model routing keyed to the same difficulty signal as the thinking budget: a
                // "low" (cheap, on-track) turn takes the fast model; an escalated turn takes the reasoning
                // model. Returns null (→ provider default) when the matching role isn't configured.
                string? DetermineModelOverride(string thinkingLevel)
                {
                    bool heavy = !string.Equals(thinkingLevel, "low", StringComparison.OrdinalIgnoreCase);
                    string pick = heavy ? reasoningModel : fastModel;
                    return string.IsNullOrWhiteSpace(pick) ? null : pick;
                }

                // Build a truthful partial answer when the run is stopped (manual Stop or stall watchdog):
                // surface whatever prose + scripts the agent produced so far, persist the turn, and record
                // stats — exactly like a normal finish, but flagged unsuccessful with a "stopped" note.
                AgentChatResponse BuildStoppedResponse()
                {
                    performance.Outcome = "stopped";
                    string partial;
                    lock (progressGate) partial = progressText.ToString().Trim();
                    string closing = DescribeStop(runControl?.StopReason, runControl?.StopDetail, lastStatusNote,
                        turnStopwatch.Elapsed, partial.Length > 0 || allScriptsExecuted.Count > 0,
                        resumes: runControl?.ResumesAfterShutdown ?? false);
                    var finalText = partial.Length > 0 ? partial + "\n\n" + closing : closing;
                    try { llm.ResetSession(llmSessionId); } catch { }
                    agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                        allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                        turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel, flatFeeProvider);
                    return new AgentChatResponse
                    {
                        ConversationId = conversation.ConversationId,
                        Response = finalText,
                        ScriptsExecuted = allScriptsExecuted,
                        Success = false,
                        ErrorMessage = runControl?.StopReason switch
                        {
                            AgentChatRunControl.StopReasonUser => "Run was stopped by Klives.",
                            AgentChatRunControl.StopReasonStall => "Run was stopped by the stall watchdog: " + (runControl.StopDetail ?? "no progress"),
                            AgentChatRunControl.StopReasonShutdown => "Run was interrupted by a service shutdown.",
                            _ => "Run was stopped.",
                        },
                        PromptTokens = totalPromptTokens,
                        CompletionTokens = totalCompletionTokens,
                        Iterations = iterationsDone
                    };
                }

                // ── Agentic Loop: Think → Script → Observe → repeat ──
                // No iteration cap. The loop ends when the LLM produces a final text-only
                // answer, or when the stuck-detector trips (same error 3x, or same script
                // body re-run). This lets the agent take as many steps as a complex task
                // genuinely requires without an artificial ceiling on its cognition.
                performance.Add("preparation", preparationTimer.ElapsedMilliseconds);
                preparationRecorded = true;
                for (int iteration = 0; ; iteration++)
                {
                    iterationsDone = iteration + 1;

                    // Stopped between iterations (manual Stop / stall watchdog) — bail with a partial answer.
                    if (cancellationToken.IsCancellationRequested)
                        return BuildStoppedResponse();

                    // Steering is a durable inbox on the active run. Consume it only at safe model
                    // boundaries, never by launching another brain against the same provider session.
                    ApplySteering(runControl?.Drain());

                    // Per-run budget guardrail (token/wall-clock). Soft-warn once at 80%; at 100% demand a
                    // final answer this turn and finalize regardless of what the model returns.
                    {
                        long usedTokens = billableTokens;
                        double elapsedMin = turnStopwatch.Elapsed.TotalMinutes;
                        bool tokenHard = maxRunTokens > 0 && usedTokens >= maxRunTokens;
                        bool timeHard = maxRunMinutes > 0 && elapsedMin >= maxRunMinutes;
                        if (tokenHard || timeHard)
                        {
                            budgetForceFinal = true;
                            stuckForceFinal = true; // skip empty/format retries on the final path
                            SendModelPrompt($"[Run limit reached] You have hit this run's {(tokenHard ? "token" : "time")} budget. STOP calling tools now. Reply with your best final answer to the user based on what you already have — no scripts, no tool calls.");
                        }
                        else
                        {
                            double tokenFrac = maxRunTokens > 0 ? usedTokens / (double)maxRunTokens : 0;
                            double timeFrac = maxRunMinutes > 0 ? elapsedMin / maxRunMinutes : 0;
                            if (!budgetWarned && (tokenFrac >= 0.8 || timeFrac >= 0.8))
                            {
                                budgetWarned = true;
                                SendModelPrompt("[Budget notice] You are near this run's limit. Start wrapping up: finish the task in the next step or two, or give your best current answer.");
                            }
                        }
                    }

                    // Surface "thinking" while we wait on the model. This also acts as a stall-watchdog
                    // heartbeat at the start of every step.
                    ReportProgress("thinking", $"_…thinking (step {iteration + 1})_");

                    // Per-iteration live buffer: streamed content tokens accumulate here and are pushed to
                    // the UI as they arrive. Reset every iteration; the parsed prose is committed to
                    // progressText afterwards so there is no double display.
                    var liveTokens = streamTokens
                        ? new KliveAgentTokenBuffer(text =>
                        {
                            if (System.Threading.Interlocked.Exchange(ref firstVisibleTextSeen, 1) == 0)
                                performance.Add("firstVisibleText", performance.DurationMs);
                            ReportProgress("thinking", null, liveText: text);
                        })
                        : null;
                    Action<string>? tokenSink = liveTokens == null ? null : liveTokens.Append;

                    string thinkingForThisCall = DetermineThinkingLevel(iteration);
                    string? modelForThisCall = DetermineModelOverride(thinkingForThisCall);

                    // #9 Speculative dispatch: as each read-only native tool_call finishes streaming, start
                    // it immediately (keyed by call id) so its I/O overlaps with the model still generating
                    // the rest of the turn. #6's pre-launch below reuses these tasks instead of re-running.
                    var speculativeTasks = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<(bool ok, string output)>>();
                    async Task<(bool ok, string output)> RunNativeToolMeasuredAsync(string name, string? content)
                    {
                        using (performance.Measure("nativeTool"))
                            return await RunNativeToolAsync(sharedGlobals, name, content);
                    }
                    void OnToolCallComplete(HFWrapper.HFToolCall tc)
                    {
                        var name = tc?.function?.name;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(tc!.id)) return;
                        if (!IsParallelSafeNativeTool(name, tc.function?.arguments)) return;   // only read-only native tools are safe to start early
                        speculativeTasks.GetOrAdd(tc.id, _ => RunNativeToolMeasuredAsync(name, tc.function?.arguments));
                    }

                    // Brain-level retry: the transport backs off on transient HTTP errors, but an exception
                    // that surfaces here (e.g. a stream that died before the first token) otherwise aborts
                    // the whole run. Retry the turn a few times before giving up.
                    KliveLLM.KliveLLM.KliveLLMResponse llmResponse = null;
                    for (int llmAttempt = 0; ; llmAttempt++)
                    {
                        try
                        {
                            var modelTimer = System.Diagnostics.Stopwatch.StartNew();
                            int firstTokenSeen = 0;
                            Action<string> measuredTokenSink = token =>
                            {
                                if (!string.IsNullOrEmpty(token) && System.Threading.Interlocked.Exchange(ref firstTokenSeen, 1) == 0)
                                    performance.Add("firstToken", modelTimer.ElapsedMilliseconds);
                                tokenSink?.Invoke(token);
                            };
                            lastModelActivity = null;
                            using (performance.Measure("model"))
                            using (KliveLLM.KliveLLM.ObserveActivity(OnModelActivity, streamIdleLimit))
                            {
                                if (useToolCalling)
                                {
                                    // The structured session already holds system + user + any prior tool turns.
                                    // Klives is sitting there waiting on this one, so it is never parked
                                    // behind autonomous background work.
                                    llmResponse = await llm.QueryToolSessionAsync(llmSessionId, toolDefinitions!, modelOverride: modelForThisCall, cancellationToken: cancellationToken, onToken: streamTokens ? measuredTokenSink : null, thinkingOverride: thinkingForThisCall, onToolCallComplete: OnToolCallComplete,
                                        workClass: KliveLLM.AIRouterWorkClass.Interactive);
                                }
                                else
                                {
                                    // Pass system prompt only on iteration 0 so it is set as the LLM session's
                                    // system role message once — not re-injected into every user turn.
                                    llmResponse = await llm.QueryLLM(currentPrompt, llmSessionId,
                                        systemPrompt: firstIterationSystemPrompt, cancellationToken: cancellationToken, onToken: streamTokens ? measuredTokenSink : null, thinkingOverride: thinkingForThisCall);
                                    firstIterationSystemPrompt = null; // don't resend
                                }
                            }
                            liveTokens?.Flush();
                            break;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return BuildStoppedResponse();
                        }
                        catch (Exception llmEx)
                        {
                            performance.Add("modelError", 0);
                            // A model that turns out not to accept screenshots must not end a desktop task:
                            // drop the images, say so once, and carry on with DOM/OCR/terminal perception.
                            if (useToolCalling && visionEnabled && !visionFallbackUsed && LooksLikeImageRejection(llmEx))
                            {
                                visionFallbackUsed = true;
                                visionEnabled = false;
                                llm.StripToolSessionImages(llmSessionId);
                                llm.AppendUserMessageToToolSession(llmSessionId,
                                    "[Vision unavailable] The active model rejected screenshots, so images are no longer attached. "
                                    + "Perceive the desktop with computer_browser_inspect (mode:\"controls\"/\"dom\"), computer_read_screen "
                                    + "(OCR rows with clickable bounds) and computer_window_state, and continue the task.");
                                Pulse("thinking", "the model can't take screenshots — continuing with text perception",
                                    new AgentActivityEvent { Iteration = Math.Max(1, iterationsDone), Kind = "error", Text = "vision rejected by the model; switched to text perception" });
                                llmAttempt--; // not a failed attempt: the request itself changed
                                continue;
                            }
                            // A permanent provider error (bad key, unknown model, rejected request) will
                            // not fix itself; retrying it for minutes only delays the honest answer.
                            if (llmAttempt >= maxLlmRetries || !IsRetryableModelFailure(llmEx, llmAttempt))
                            {
                                string progressed;
                                lock (progressGate) progressed = progressText.ToString().Trim();
                                agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                                    allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                                    turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel, flatFeeProvider);
                                return new AgentChatResponse
                                {
                                    ConversationId = conversation.ConversationId,
                                    Response = DescribeModelFailure(llmEx, llmAttempt + 1, progressed),
                                    ScriptsExecuted = allScriptsExecuted,
                                    Success = false,
                                    ErrorMessage = llmEx.ToString(),
                                    PromptTokens = totalPromptTokens,
                                    CompletionTokens = totalCompletionTokens,
                                    Iterations = iterationsDone
                                };
                            }
                            performance.Add("modelRetry", 0);
                            var backoff = ModelRetryDelay(llmAttempt, llmEx);
                            string why = DescribeModelError(llmEx);
                            Pulse("retrying", $"{why} — retrying in {FormatElapsed(backoff)} (attempt {llmAttempt + 2} of {maxLlmRetries + 1})",
                                new AgentActivityEvent { Iteration = Math.Max(1, iterationsDone), Kind = "wait", Text = $"model call failed ({why}); retrying" });
                            try
                            {
                                using (performance.Measure("retryBackoff"))
                                {
                                    var waited = System.Diagnostics.Stopwatch.StartNew();
                                    while (waited.Elapsed < backoff)
                                    {
                                        var slice = backoff - waited.Elapsed;
                                        await Task.Delay(slice < ToolPulseInterval ? slice : ToolPulseInterval, cancellationToken);
                                        if (waited.Elapsed < backoff)
                                            Pulse("retrying", $"{why} — retrying in {FormatElapsed(backoff - waited.Elapsed)} (attempt {llmAttempt + 2} of {maxLlmRetries + 1})");
                                    }
                                }
                            }
                            catch (OperationCanceledException) { return BuildStoppedResponse(); }
                        }
                    }

                    if (llmResponse == null || !llmResponse.Success)
                    {
                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                            allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                            turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel, flatFeeProvider);
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
                    billableTokens += BillableTokens(llmResponse.PromptTokens, llmResponse.CachedPromptTokens,
                        llmResponse.CompletionTokens);
                    try { agentService.PromptCache.Record(cacheRunId, iteration + 1, llmResponse); }
                    catch (Exception telemetryError)
                    {
                        _ = agentService.ServiceLogError(telemetryError, "KliveAgent prompt-cache telemetry");
                    }

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
                            if (IsNonScriptTool(name))
                                // Memory / grep tool: dispatched straight to its API, not the script engine.
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
                        AgentActivityEvent thoughtEvent = null;
                        if (thought.Length > 0)
                        {
                            lock (progressGate)
                            {
                                if (progressText.Length > 0) progressText.AppendLine().AppendLine();
                                progressText.Append(thought);
                            }
                            thoughtEvent = new AgentActivityEvent
                            {
                                Iteration = iteration + 1,
                                Kind = "think",
                                Text = thought.Length > 200 ? thought.Substring(0, 200) + "…" : thought
                            };
                        }
                        int pendingCount = segments.Count(IsAction);
                        ReportProgress("running",
                            $"_…running {pendingCount} step{(pendingCount == 1 ? "" : "s")} (step {iteration + 1})_",
                            thoughtEvent);
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

                    // No scripts → this is the final answer. Also finalize (ignoring any scripts) when the
                    // per-run budget was hit — the model was told to stop; we end here regardless.
                    if (!hasScripts || budgetForceFinal)
                    {
                        var finalText = string.Join("\n", segments
                            .Where(s => !s.IsScript && s.ToolName == null)
                            .Select(s => s.Content)).Trim();

                        // Budget stop with no clean prose (the model tried to keep working): synthesize an
                        // honest final answer from whatever progress was streamed so the user isn't left blank.
                        if (budgetForceFinal && string.IsNullOrWhiteSpace(finalText))
                        {
                            var progressed = progressText.ToString().Trim();
                            finalText = "I've reached this run's budget limit, so I'm stopping here. "
                                + (progressed.Length > 0 ? "Here's where I got to:\n\n" + progressed
                                                         : "I wasn't able to finish the task within the limit — try narrowing it or raising the run budget.");
                        }

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

                            // Genuinely persistent empty: only fail once steering intake has atomically
                            // closed. A durable reservation may still be finishing its disk write.
                            bool retryForSteering = false;
                            while (runControl != null && !runControl.TrySeal(out var lateSteering))
                            {
                                if (lateSteering.Count > 0)
                                {
                                    ApplySteering(lateSteering);
                                    retryForSteering = true;
                                    break;
                                }
                                await Task.Delay(25, cancellationToken);
                            }
                            if (retryForSteering) continue;

                            // Surface a truthful failure (same path as any other LLM failure), not a
                            // fabricated persona response.
                            llm.ResetSession(llmSessionId);
                            agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                                allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                                turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel, flatFeeProvider);
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

                        // A task whose "final" reply just announces the next step would end right here
                        // with that step never taken. Keep the words as visible progress and send the
                        // model back to work (bounded, so a model that insists still gets to finish).
                        if (taskMode && !stuckForceFinal && !budgetForceFinal && unfinishedTaskNudges < 2
                            && LooksLikeUnfinishedTaskReply(finalText))
                        {
                            unfinishedTaskNudges++;
                            lock (progressGate)
                            {
                                if (progressText.Length > 0) progressText.AppendLine().AppendLine();
                                progressText.Append(finalText);
                            }
                            ReportProgress("running", "continuing the task", new AgentActivityEvent
                            {
                                Iteration = iteration + 1,
                                Kind = "think",
                                Text = finalText.Length > 200 ? finalText.Substring(0, 200) + "…" : finalText
                            });
                            SendModelPrompt("[Not finished] Your reply announces what you will do next, but this run ENDS the moment you reply without a tool call — that next step would never happen. "
                                + "Do it now with your tools, and keep going until everything Klives asked for is done. Reply in text only when it is all done (or something is genuinely impossible).");
                            continue;
                        }

                        // A reply that hands a CAPTCHA or human check back to Klives ends the run, and with
                        // it the live page: a solved reCAPTCHA expires in about two minutes, so by the time
                        // he answers and a new run starts, his work is void. request_human keeps this run
                        // alive while he does it in place. Once per run, and not after a takeover failed.
                        if (taskMode && computerUseEnabled && !stuckForceFinal && !budgetForceFinal && handBackNudges < 1
                            && !humanHandoffUnavailable && LooksLikeHumanCheckHandBack(finalText))
                        {
                            handBackNudges++;
                            lock (progressGate)
                            {
                                if (progressText.Length > 0) progressText.AppendLine().AppendLine();
                                progressText.Append(finalText);
                            }
                            ReportProgress("running", "handing the human check over instead of ending the run", new AgentActivityEvent
                            {
                                Iteration = iteration + 1,
                                Kind = "think",
                                Text = "asked to use request_human instead of ending the run on a human check"
                            });
                            SendModelPrompt("[Use request_human] This reply ends the run to ask Klives to clear a human check (CAPTCHA, \"I'm not a robot\", verification). "
                                + "Ending the run loses the live page: a solved CAPTCHA expires in about two minutes, so by the time he answers and a new run starts it is void. "
                                + "Call request_human with that reason now — he takes over your desktop right here in the chat, and you resume the moment he hands it back. Then finish the rest of the task. "
                                + "If nothing is actually waiting on him, repeat your final answer unchanged.");
                            continue;
                        }

                        // Atomically close the steering inbox immediately before the actual return. If
                        // guidance raced with this answer, apply it and give the model another turn.
                        bool finalRetryForSteering = false;
                        while (runControl != null && !runControl.TrySeal(out var finalSteering))
                        {
                            if (finalSteering.Count > 0)
                            {
                                ApplySteering(finalSteering);
                                finalRetryForSteering = true;
                                break;
                            }
                            await Task.Delay(25, cancellationToken);
                        }
                        if (finalRetryForSteering) continue;

                        llm.ResetSession(llmSessionId);

                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                            allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success),
                            turnStopwatch.ElapsedMilliseconds, conversation.SourceChannel, flatFeeProvider);
                        performance.Outcome = "completed";

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
                    // Motion-clip frames from computer-use tools this turn, fed to the vision model as a user
                    // image message AFTER all role:"tool" results (providers require tool results to stay
                    // text-only). Each action contributes an ordered filmstrip (intermediate motion frames +
                    // a final settled frame); the `settled` flag lets the per-turn budget keep current-state
                    // frames while trimming intermediates when a multi-action turn gets frame-heavy.
                    var pendingModelImages = new List<(byte[] data, string mimeType, bool settled)>();
                    // True once at least one script's result has been delivered as a role:"tool" message.
                    // When set, the trailing guidance goes back as a plain user turn (observations are
                    // already in the tool results); when not, observations themselves are sent back.
                    bool anyToolResultsAppended = false;

                    // #6 Parallel tool execution: read-only native tools in this turn are independent of each
                    // other (and of the turn's scripts), so kick them ALL off up front to overlap their I/O.
                    // Their RESULTS are still consumed below in original segment order, and execute_csharp
                    // scripts (which chain Roslyn session state) + write tools still run serially in order.
                    var prelaunchedTools = new Dictionary<ResponseSegment, Task<(bool ok, string output)>>();
                    foreach (var seg in segments)
                    {
                        if (!seg.IsScript && seg.ToolName != null && IsParallelSafeNativeTool(seg.ToolName, seg.Content))
                        {
                            // Reuse the task already started mid-stream (#9) when available; otherwise start it now.
                            if (seg.ToolCallId != null && speculativeTasks.TryGetValue(seg.ToolCallId, out var specTask))
                                prelaunchedTools[seg] = specTask;
                            else
                                prelaunchedTools[seg] = RunNativeToolMeasuredAsync(seg.ToolName, seg.Content);
                        }
                    }

                    foreach (var segment in segments)
                    {
                        // Prose (no action) — just record the agent's thought.
                        if (IsProse(segment))
                        {
                            if (!string.IsNullOrWhiteSpace(segment.Content))
                                observationSb.AppendLine($"[Agent thought] {segment.Content.Trim()}");
                            continue;
                        }

                        // Native tool call (memory / grep / computer-use) — dispatch straight to its API (no Roslyn).
                        if (!segment.IsScript && segment.ToolName != null)
                        {
                            // Computer-use tool → HostControlManager. Has its own path: it streams annotated
                            // frames + activity to the website, may block on a human approval (heartbeating so
                            // the stall watchdog stays calm), and feeds its screenshot to the vision model.
                            if (IsComputerTool(segment.ToolName))
                            {
                                ComputerToolResult cr;
                                int actingIteration = iteration + 1;
                                using (performance.Measure("computerTool"))
                                    cr = await WithToolPulse(agentService.Computer.ExecuteAsync(computerTarget, segment.ToolName,
                                        segment.Content, conversation.ConversationId, cancellationToken, hcp =>
                                        {
                                            var act = hcp.Activity == null ? null
                                                : new AgentActivityEvent { Iteration = actingIteration, Kind = hcp.Activity.Kind, Text = hcp.Activity.Text };
                                            ReportProgress("running", hcp.Note, act, frame: hcp.AnnotatedFrameJpeg, approval: hcp.Approval);
                                        }), $"running {segment.ToolName}");
                                if (computerTarget == KliveAgentComputerTarget.Container)
                                    computerContainerId = agentService.Computer.DesktopContainerID ?? computerContainerId;
                                // A takeover that timed out or was declined means Klives is not at hand,
                                // so ending the run with a request to him is then the honest option.
                                if (segment.ToolName == "request_human")
                                    humanHandoffUnavailable = !cr.Success;

                                if (!cr.Success) errorCountThisIter++;

                                var crText = KliveAgentContextBudget.TruncateToTokens(cr.Text ?? string.Empty,
                                    cr.Success ? KliveAgentContextBudget.ScriptOutputBudget : KliveAgentContextBudget.ScriptErrorBudget);
                                observationSb.AppendLine($"[{(cr.Success ? "OK" : "ERROR")} | {segment.ToolName}]");
                                if (!string.IsNullOrWhiteSpace(crText)) observationSb.AppendLine(crText);
                                observationSb.AppendLine();

                                ReportProgress("running", null,
                                    new AgentActivityEvent { Iteration = iteration + 1, Kind = cr.Success ? "action" : "error", Text = $"{segment.ToolName} {(cr.Success ? "ok" : "error")}" },
                                    frame: cr.AnnotatedJpeg);

                                if (useToolCalling && !string.IsNullOrEmpty(segment.ToolCallId))
                                {
                                    llm.AppendToolResult(llmSessionId, segment.ToolCallId, segment.ToolName, crText);
                                    anyToolResultsAppended = true;
                                }
                                if (useToolCalling && visionEnabled)
                                {
                                    // Feed the whole clip (oldest→newest) so the model sees what happened during
                                    // the action, not just the end-state. Fall back to the single settled frame.
                                    if (cr.ModelImageFrames != null && cr.ModelImageFrames.Count > 0)
                                    {
                                        foreach (var f in cr.ModelImageFrames)
                                            if (f.Jpeg != null && f.Jpeg.Length > 0)
                                                pendingModelImages.Add((f.Jpeg, "image/jpeg", f.IsSettled));
                                    }
                                    else if (cr.ModelImageJpeg != null)
                                    {
                                        pendingModelImages.Add((cr.ModelImageJpeg, "image/jpeg", true));
                                    }
                                }

                                continue;
                            }

                            // wait_for → pause (un-timed, heartbeating, cancellable) until an external
                            // condition is met. Orchestrated here because it needs the script session +
                            // progress channel; the per-script 30s cap never applies.
                            if (IsWaitTool(segment.ToolName))
                            {
                                (bool wok, string wtext) waitResult;
                                using (performance.Measure("waitTool"))
                                    waitResult = await RunWaitForAsync(segment.Content, cancellationToken, note =>
                                        ReportProgress("waiting", note, new AgentActivityEvent { Iteration = iteration + 1, Kind = "wait", Text = note }));
                                var (wok, wtext) = waitResult;
                                if (!wok) errorCountThisIter++;

                                var wt = KliveAgentContextBudget.TruncateToTokens(wtext ?? string.Empty,
                                    wok ? KliveAgentContextBudget.ScriptOutputBudget : KliveAgentContextBudget.ScriptErrorBudget);
                                observationSb.AppendLine($"[{(wok ? "OK" : "ERROR")} | wait_for]");
                                if (!string.IsNullOrWhiteSpace(wt)) observationSb.AppendLine(wt);
                                observationSb.AppendLine();

                                ReportProgress("running", null,
                                    new AgentActivityEvent { Iteration = iteration + 1, Kind = wok ? "wait" : "error", Text = $"wait_for {(wok ? "done" : "error")}" });

                                if (useToolCalling && !string.IsNullOrEmpty(segment.ToolCallId))
                                {
                                    llm.AppendToolResult(llmSessionId, segment.ToolCallId, "wait_for", wt);
                                    anyToolResultsAppended = true;
                                }
                                continue;
                            }

                            bool memOk; string memOut;
                            if (prelaunchedTools.TryGetValue(segment, out var preTask))
                                (memOk, memOut) = await WithToolPulse(preTask, $"running {segment.ToolName}");        // parallel-safe read-only tool: started up front
                            else
                                (memOk, memOut) = await WithToolPulse(RunNativeToolMeasuredAsync(segment.ToolName, segment.Content), $"running {segment.ToolName}"); // write tool: serial, in order
                            if (!memOk) errorCountThisIter++;

                            var memText = KliveAgentContextBudget.TruncateToTokens(memOut ?? string.Empty,
                                memOk ? KliveAgentContextBudget.ScriptOutputBudget : KliveAgentContextBudget.ScriptErrorBudget);
                            observationSb.AppendLine($"[{(memOk ? "OK" : "ERROR")} | {segment.ToolName}]");
                            if (!string.IsNullOrWhiteSpace(memText)) observationSb.AppendLine(memText);
                            observationSb.AppendLine();

                            // Stream the tool invocation to the live activity timeline.
                            ReportProgress("running", null, new AgentActivityEvent
                            {
                                Iteration = iteration + 1,
                                Kind = memOk ? "tool" : "error",
                                Text = $"{segment.ToolName} {(memOk ? "ok" : "error")}"
                            });

                            if (useToolCalling && !string.IsNullOrEmpty(segment.ToolCallId))
                            {
                                llm.AppendToolResult(llmSessionId, segment.ToolCallId, segment.ToolName, memText);
                                anyToolResultsAppended = true;
                            }
                            continue;
                        }

                        scriptCountThisIter++;

                        var result = await WithToolPulse(scriptSession.ExecuteAsync(segment.Content ?? string.Empty, scriptTimeout), "running a C# script");
                        performance.Add("script", result.ExecutionTimeMs);
                        allScriptsExecuted.Add(result);

                        // Stream the just-completed script (code + output) to the UI as it lands, with a
                        // timeline entry recording success/failure + duration.
                        ReportProgress("observing",
                            $"_…ran {allScriptsExecuted.Count} script{(allScriptsExecuted.Count == 1 ? "" : "s")} so far (step {iteration + 1})_",
                            new AgentActivityEvent
                            {
                                Iteration = iteration + 1,
                                Kind = result.Success ? "script" : "error",
                                Text = $"ran script — {(result.Success ? "OK" : "ERROR")} ({result.ExecutionTimeMs}ms)"
                            });

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

                            // Record the failure category (CS-code / runtime exception type) for the
                            // agent's own GetScriptFailureBreakdown() — no longer a stuck-loop signal.
                            agentService.Stats?.RecordScriptError(ClassifyScriptError(errMsg));

                            scriptObs.AppendLine($"[ERROR | {result.ExecutionTimeMs}ms]");
                            scriptObs.AppendLine(KliveAgentContextBudget.TruncateToTokens(errMsg, KliveAgentContextBudget.ScriptErrorBudget));
                        }

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

                    // Feed any computer-use clip frames to the vision model as ONE user image turn, after all
                    // role:"tool" results (which must stay text-only). Reuses KliveLLM's existing vision path.
                    if (useToolCalling && pendingModelImages.Count > 0)
                    {
                        // Per-turn frame budget: a multi-action turn can pile up many frames. Cap the total,
                        // dropping intermediate (motion) frames oldest-first while preserving every action's
                        // settled current-state frame so on-screen state is never lost.
                        int perTurnCap = settings.ClipMaxFramesPerTurn;
                        var framesToSend = BudgetClipFrames(pendingModelImages, perTurnCap);
                        try { llm.AppendUserContentToToolSession(llmSessionId, "Frames from the computer action(s) above (oldest→newest; each action is a short clip ending in its current on-screen state — the newest frame). Read click coordinates only from the latest gridded frame; use the earlier frames to catch transient changes:", framesToSend, keepRecentImages: retainedScreenshots); }
                        catch { }
                    }

                    // Track the per-turn error streak so the adaptive thinking budget can escalate when
                    // the agent keeps hitting failures (and relax again once a turn comes back clean).
                    if (errorCountThisIter > 0) consecutiveErrorIters++;
                    else consecutiveErrorIters = 0;

                    // A run becomes a TASK the moment it acts on the world (desktop, mail, accounts):
                    // from then on, success means "every part of the request is done", not "this lookup
                    // answered a question", and the guidance below has to say so.
                    if (!taskMode && segments.Any(s => s.ToolName != null && IsTaskTool(s.ToolName)))
                        taskMode = true;

                    // Soft nudge once the work has gone deep — informational, not a stop.
                    string nudge = string.Empty;
                    if (!taskMode && iteration + 1 == 12 && !stuckForceFinal)
                    {
                        nudge = "\n\n[Nudge] You've taken 12 steps on this. If you're making real progress, keep going. " +
                            "If you're spiralling, consider SaveMemory(\"...\") for what you've learned and giving the final answer.";
                    }
                    else if (taskMode && (iteration + 1) % 40 == 0 && !stuckForceFinal)
                    {
                        nudge = $"\n\n[Checkpoint] {iteration + 1} steps into this task. If you are progressing, keep going. If the last few steps "
                            + "repeated the same obstacle, change approach now (inspect the page's controls, dismiss overlays, solve_challenge, "
                            + "a different route, or request_human for a human-only blocker) instead of retrying it.";
                    }

                    // No hard iteration cap and no failure ceiling: the loop runs until the model
                    // returns a no-script final answer. stuckForceFinal is only set by the
                    // broken-output circuit breaker (consecutiveNoOpResponses) above.

                    // Feed the next turn. System prompt + prior turns already live in the session, so we
                    // never re-inject them. In tool mode where results were delivered as role:"tool"
                    // messages, send only the guidance (observations are already attached to the calls);
                    // otherwise send the aggregate observations + guidance as a user turn.
                    // Finalize-sooner nudge: when this turn's actions ALL succeeded, push hard for the
                    // final answer so the agent doesn't burn extra round-trips re-checking what it already
                    // has. This is a prompt nudge only — never a forced stop or iteration cap — so a task
                    // that genuinely needs more steps can still take them.
                    bool madeCleanProgress = hasScripts && errorCountThisIter == 0;
                    string guidance = BuildStepGuidance(stuckForceFinal, madeCleanProgress, taskMode);
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
            finally
            {
                if (!preparationRecorded) performance.Add("preparation", preparationTimer.ElapsedMilliseconds);
                agentService.Stats.RecordPerformance(performance.Snapshot(iterationsDone, totalPromptTokens, totalCompletionTokens));
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

            // Per-turn clock: the one line that anchors "now" for this whole turn. History lines
            // below carry their own stamps so the model can measure gaps against this.
            sb.AppendLine($"[Now: {Data_Handling.TemporalFormat.ClockLine()}]");

            if (!string.IsNullOrEmpty(senderName))
            {
                sb.AppendLine($"[Current User: {senderName}]");
                sb.AppendLine($"Channel: {conversation.SourceChannel}");
                sb.AppendLine();
            }

            // The coordinator persists the current user message before execution. Snapshot only
            // completed history here so that durable acceptance does not duplicate [New Message].
            List<AgentMessage> historySnapshot;
            lock (conversation.SyncRoot)
            {
                historySnapshot = (conversation.Messages ?? new List<AgentMessage>())
                    .Where(m => !string.Equals(m.DeliveryStatus, "running", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Budget-aware conversation history selection
            if (historySnapshot.Count > 0)
            {
                var historyMessages = SelectHistoryMessages(
                    historySnapshot, userMessage, KliveAgentContextBudget.HistoryBudget);

                if (historyMessages.Count > 0)
                {
                    // Compaction: turns that fall before the retained window are summarised into a
                    // short synopsis rather than silently dropped, so the earlier thread survives.
                    var earlierSummary = BuildEarlierSummary(historySnapshot, historyMessages,
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
                        sb.AppendLine($"[{Data_Handling.TemporalFormat.StampMinute(msg.Timestamp)}] {role}: {msg.Content}");
                        if (msg.Attachments?.Count > 0)
                            foreach (var attachment in msg.Attachments)
                                sb.AppendLine($"[Earlier attachment: {attachment.Name}; local path={agentService.Attachments.PathFor(attachment)}]");

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
                earlierTurns.Add($"- [{Data_Handling.TemporalFormat.StampMinute(user.Timestamp)}] {TruncateForMemory(user.Content ?? string.Empty, 90)}"
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

        internal static string BuildCompactToolGuide(string userMessage, bool toolCallingMode)
        {
            if (!toolCallingMode) return BuildToolGuide(userMessage, false);
            var sb = new StringBuilder("[ScriptGlobals — C# helpers]\n");
            sb.AppendLine("Core: " + string.Join(", ", StarterTools.Select(t => t.MethodName)));
            if (ShouldIncludeDiscoveryTools(userMessage))
                sb.AppendLine("Codebase: " + string.Join(", ", DiscoveryTools.Select(t => t.MethodName)));
            if (ShouldIncludeAdvancedRuntimeTools(userMessage))
                sb.AppendLine("Runtime: " + string.Join(", ", AdvancedRuntimeTools.Select(t => t.MethodName)));
            sb.AppendLine("Projects: " + string.Join(", ", ProjectsTools.Select(t => t.MethodName)));
            sb.AppendLine("GetTypeSchema(\"ScriptGlobals\") lists exact signatures; GetMethodDocumentation(\"ScriptGlobals\", name) describes one helper.");
            sb.AppendLine("Example: var svc = GetService(\"Name\"); Log(await CallObjectMethod(svc, \"Method\", args));");
            return sb.ToString();
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
                    + "recall_memories(query, maxResults?, since?, until?) — search memory (do this FIRST for non-code questions; since/until take \"7d\" or a UTC date for time-window recall); "
                    + "recall_memories_by_tag(tag); save_memory(content, tags?, importance?); "
                    + "save_shortcut(title, content, tags?); get_shortcuts(); delete_memory(id).");
            else
                AppendToolNames(sb, "Memory", MemoryTools);

            // Prospective memory (always surfaced): the agent's ability to act at a FUTURE time.
            if (toolCallingMode)
                sb.AppendLine("Time (NATIVE TOOLS): schedule_task(instruction, dueAt, repeatEvery?) — PROSPECTIVE MEMORY: fires a FULL future agent turn (all tools) at dueAt and reports the outcome to Klives; use it for anything that must happen later (reminders, follow-ups, recurring checks) instead of promising in prose. list_scheduled_tasks(); cancel_scheduled_task(id).");
            else
                AppendToolNames(sb, "Time", TemporalTools);

            // Cross-system knowledge retrieval (always surfaced): reaches other Projects, Omniscience,
            // repo docs and cached web that plain memory/codebase search can't.
            if (toolCallingMode)
                sb.AppendLine("Knowledge (NATIVE TOOLS): search_knowledge(query, maxResults?, includeMessages?) — semantic search across Projects history, your memories/conversations, Omniscience facts, repo docs & cached web; read_knowledge_doc(docId, maxTokens?) — open a full result document; web_search(query, maxResults?, fetchTop?, timeRange?) — LIVE web search; web_fetch(url) — read one web page.");
            else
                AppendToolNames(sb, "Knowledge", KnowledgeTools);

            // Projects delegation is always surfaced (small set) — the interactive assistant can hand
            // long-running goals to the autonomous task force it shares memory with.
            AppendToolNames(sb, "Projects", ProjectsTools);

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
        /// Classifies a failed script's error message into a stable category for the agent's own
        /// failure-breakdown stats: the first Roslyn diagnostic id (e.g. "CS1061") for compile
        /// failures, "Runtime:&lt;ExceptionType&gt;" for runtime errors, else a coarse fallback.
        /// </summary>
        internal static string ClassifyScriptError(string? errMsg)
        {
            if (string.IsNullOrWhiteSpace(errMsg)) return "Unknown";

            // Compile failures render as "[CS1061] line ..." — pull the first CS id.
            var cs = Regex.Match(errMsg, @"\bCS\d{3,5}\b");
            if (cs.Success) return cs.Value;

            // Runtime failures render as "Runtime error: <ExceptionType>: <message>".
            var rt = Regex.Match(errMsg, @"Runtime error:\s*([A-Za-z_][A-Za-z0-9_]*)");
            if (rt.Success) return $"Runtime:{rt.Groups[1].Value}";

            if (errMsg.StartsWith("Compilation failed", StringComparison.Ordinal)) return "Compile";
            if (errMsg.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "Timeout";
            return "Runtime";
        }

        private static readonly Regex OfferOrQuestion = new(
            @"\b(if you|would you like|want me to|should i|shall i|let me know|do you want)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ForwardCommitment = new(
            @"\b(?:(?:next|now|then)[,]?\s+(?:i(?:'|’| wi)ll|let me|i(?:'|’)m going to|i am going to)"
            + @"|i(?:'|’| wi)ll (?:now|next|then)\b"
            + @"|let me (?:now|next)\b"
            + @"|i(?:'|’)m (?:now )?going to (?:proceed|continue|create|register|sign|click|navigate|open|fill|submit|verify|check|retrieve|generate|set up|try)"
            + @"|proceeding to|moving on to)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A text-only reply that ends by announcing the next step ("Account created — next I'll register
        /// the app") is not a final answer: the run ends the moment the model replies without tools, so
        /// the next step would silently never happen. Offers and questions ("if you want, I'll…",
        /// "let me know…") are left alone — those are legitimate endings.
        /// </summary>
        internal static bool LooksLikeUnfinishedTaskReply(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string tail = text.Trim();
            if (tail.Length > 280) tail = tail[^280..];
            if (OfferOrQuestion.IsMatch(tail)) return false;
            return ForwardCommitment.IsMatch(tail);
        }

        private static readonly Regex HumanCheckMention = new(
            @"\b(?:re-?captcha|h-?captcha|captcha|turnstile|i'?m not a robot|not a robot|human[- ]verification"
            + @"|confirm (?:your|you're|you are) human(?:ity)?|verify (?:that )?you(?:'re| are) (?:a )?human|are you (?:a )?human)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AskOfKlives = new(
            @"(?:^|[.!?:]\s+|\n\s*(?:[-*]\s+)?)(?:just\s+|then\s+)?(?:tick|check|click|solve|complete|clear)\b"
            + @"|\b(?:can|could|would) you\b|\bplease\b|\bonce you(?:'ve| have)?\b|\bwhen you(?:'ve| have)?\b|\blet me know\b"
            + @"|\bsend me the word\b|\bping me\b|\btell me when\b|\byou(?:'ll| will)? (?:need|have) to\b|\bneeds? you\b"
            + @"|\bfor you to\b|\bover to you\b|\byour (?:turn|move)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A reply asking Klives to clear a CAPTCHA or human check himself ("Tick the box and hit
        /// Register — send me the word"). Both the check and the request must sit in the same paragraph,
        /// so a report that merely mentions a CAPTCHA it already cleared is left alone.
        /// </summary>
        internal static bool LooksLikeHumanCheckHandBack(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var paragraph in Regex.Split(text.Trim(), @"\n\s*\n"))
                if (HumanCheckMention.IsMatch(paragraph) && AskOfKlives.IsMatch(paragraph))
                    return true;
            return false;
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
