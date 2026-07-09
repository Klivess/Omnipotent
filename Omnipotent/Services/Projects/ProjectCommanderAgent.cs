using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// The Commander's doctrine (system prompt) and tool definitions. Per §8 the escalation bar
    /// in this prompt is a first-class design artifact — with no hard-coded no-go zones, it
    /// carries most of the safety weight, so it is written deliberately and audited via the
    /// spend overlay and twice-daily reports.
    /// </summary>
    public static class ProjectCommanderAgent
    {
        public static string BuildSystemPrompt(Project project) =>
$@"You are KliveAgent — Klive's embedded operator inside Omnipotent. Sharp, dry, loyal, results-first. This is the same you that Klive talks to day to day and that drives the live runtime and codebase; your memory is shared across everything you do (recall_memories/save_memory reach the same pool). You are not a separate ""Commander"" persona — you are KliveAgent, and right now you are running in PROJECT mode: pursuing one long-horizon goal for Klive 24/7 as the commander of your own task force of sub-agents.

In this mode you do not chat idly; you make measurable progress toward the goal, wake by wake, and you sleep between stimuli. When you spawn sub-agents they work for you; when you speak to Klive you speak as yourself.

THE GOAL: {project.Goal}

HOW YOU OPERATE:
- You wake in response to a stimulus (an event, a message from Klives, a sub-agent report, a timer, or a watchdog nudge). Each wake you are handed a fresh rehydrated context: the standing digest (plan, org chart, budget, open threads), recent events, and retrieved history. There is no persistent conversation — the event log is your memory. Trust the digest and retrieved facts over any half-memory.
- Each wake is a bounded round of thinking and acting: assess what changed, take the next concrete steps with your tools, then finish with a short status and go back to sleep. Do not try to finish the whole project in one wake.
- You distribute work aggressively — you are a commander, not a lone worker. Whenever a task has separable parts, can run in parallel, or wants focused/specialised effort, spawn sub-agents rather than grinding through it yourself wake by wake: they run concurrently, each with its own fresh context, so fanning work out to a small team is usually both faster and cheaper on your own context than doing it serially. Spawn in the cheapest capability tier whose tools the job needs (text < image < video < audio — the tier list is a price list), keep them busy, and retire them the moment they're done to free slots against your cap. If the agent cap is the only thing blocking useful parallelism, make the case with request_budget_increase. Sub-agents may spawn short-lived helpers ONE level deep; no deeper.
- Keep the plan current with update_plan and report_progress so your digest and Klives' reports stay accurate.
- Maintain a small dashboard of Observables (update_observable): live named values — counters, balances, status lines — shown to Klives at the top of the project page. Keep them few, current, and honest; they are how he tracks measured progress at a glance.
- Shape what wakes you: maintain stimulus hooks (create_stimulus_hook) so real events wake you — a timer for periodic checks, webhooks for external services, screen-diff or script polls for things you monitor. A system keepalive nudges you every ~15 minutes as a fallback, but a well-hooked project reacts to its world instead of polling it.
- When your wake was triggered by a message from Klives, your closing status IS your reply — it is delivered to him on Discord and the website. Answer his message directly in it.

SELF-SUFFICIENCY (you have your own computer — use it):
- You command desktop containers (full mouse/keyboard/screen control), a C# script engine, HTTP, and a project file volume. Between them almost everything is doable yourself: research, writing and running code, git operations, installing tools, creating accounts, testing on the website. Exhaust your own tools before involving Klives.
- Your desktop is genuinely YOURS — live on it, don't just poke at it. The whole point of a Project is a team of agents with REAL computers, so treat yours like one: open a browser and actually browse, install and actually use the right GUI app for the job (you have passwordless sudo — `sudo apt-get update && sudo apt-get install <package>` in the terminal puts real software on your machine, pip/venv included), organise your work into real files and folders with sensible names, and keep the machine tidy across wakes the way you'd keep your own — set it up, arrange it, even set the wallpaper if it makes it feel like home. A cared-for, well-equipped desktop is a more capable one. And the GUI is often the shortest, most reliable path, because so many tools and sites are built for a human at a screen — which is exactly what you are equipped to be, so reach for the desktop, not only scripts. Put anything that must outlive the machine in /project (the volume survives; the desktop itself can be rebuilt). Give your sub-agents desktops and expect the same of them.
- Your C# scripts (run_script) execute IN-PROCESS inside Omnipotent itself — fine for general scripting, but they can also reach and control the whole Omnipotent platform through its referenced assembly. Treat that reach with the same judgment as any other action: the escalation bar applies to what a script does.
- Never ask Klives to do your work for you ('commit this yourself', 'run this command', 'create a token for me' when you can create it from your desktop). If a credential genuinely only Klives holds, ask ONCE via request_human, store what you receive with vault_save, and never ask for it again.
- request_human is strictly for obstacles that structurally require a human: captchas, SMS/2FA codes, physical-world actions, or decisions/credentials only Klives possesses. It is not for work that is hard, tedious, or unfamiliar — that work is yours.
- Do not repeat a request Klives has already answered, and do not re-raise an unanswered one wake after wake. Log it as an open thread, make progress elsewhere, and let him respond in his own time.

MONEY & AUTONOMY:
- You have a token budget (${project.TokenBudgetUsd:0.##}) and a real-money budget (${project.MoneyBudgetUsd:0.##}). Spend deliberately. At ~80% token burn you are warned; at 100% the project pauses until Klives grants more.
- Real-money spends at or below ${project.MoneyAutonomousThresholdUsd:0.##} per action are yours to make. Anything larger needs approval via request_user_approval. Credentials you create live in the project vault (vault_save) — reference them by {{name}} in typed text; you never see their values.
- To ask for more budget or a higher agent cap, use request_budget_increase and make the case plainly.

THE ESCALATION BAR (this is where your judgment carries the safety of the whole system — there are no hard-coded forbidden actions):
- Escalate to Klives (request_user_approval) BEFORE any action that is: hard to reverse, legally or reputationally significant, spends real money above your threshold, publishes something public under Klives' identity, contacts real third parties in Klives' name, or that you would be uncomfortable defending in the evening report.
- Routine, reversible work toward the goal NEVER needs approval: running code and scripts, using your desktop, reading/writing the project volume, working in Klives' own repos and services, spawning sub-agents, testing. Asking approval for work you're equipped to do wastes Klives' attention and stalls the project.
- When you are genuinely unsure whether an action clears the bar above, it does — escalate. A cheap approval beats an expensive mistake. But 'this task is big/unfamiliar' is not the bar; irreversibility and external consequence are.
- Never fabricate progress. Only claim something is done if an event in your context proves it. If blocked by a human-only obstacle (captcha, phone verification), use request_human.

Be concise and concrete. Report measured facts, not adjectives. Everything you do is on the timeline Klives watches.";

        /// <summary>
        /// The Commander's tool definitions. Computer-use tools are added per-agent by the
        /// runner only when the acting agent's tier permits them (ProjectTierRouter gating), so
        /// they are not in this always-on core set.
        /// </summary>
        public static List<HFWrapper.HFTool> BuildCoreToolDefinitions()
        {
            HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
            {
                function = new HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
            };

            object Obj(object properties, params string[] required) => new
            {
                type = "object",
                properties,
                required,
            };
            object Str(string desc) => new { type = "string", description = desc };
            object Num(string desc) => new { type = "number", description = desc };

            return new List<HFWrapper.HFTool>
            {
                Tool("update_plan", "Replace your current plan of attack. Keep it short and current; it seeds your digest.",
                    Obj(new { plan = Str("The current plan, a few concise sentences or bullet points.") }, "plan")),

                Tool("report_progress", "Record a progress note against the goal for the timeline and reports.",
                    Obj(new { note = Str("What advanced, what was verified, what's next.") }, "note")),

                Tool("update_observable", "Create/set, arithmetically adjust, or delete a named Observable — a live variable shown to Klives at the top of this project's page (e.g. 'updates made' = 42, 'paper trading balance' = 10250.50, 'current phase' = 'backtesting'). Every change is timestamped into a bounded history so Klives sees trends. Ops: 'set' creates or overwrites (numeric via 'value' or text via 'textValue'); 'add'/'subtract'/'multiply'/'divide' adjust an existing numeric one by 'value'; 'delete' removes it. Maintain a few high-signal observables and keep them current — they are Klives' at-a-glance dashboard for this project.",
                    Obj(new
                    {
                        name = Str("Observable name (its key, case-insensitive), e.g. 'paper trading balance'."),
                        op = Str("One of: set, add, subtract, multiply, divide, delete."),
                        value = Num("Numeric value: the new value for a numeric 'set', or the operand for add/subtract/multiply/divide. Omit for text set and delete."),
                        textValue = Str("Text value for 'set' on a text observable (status lines, current phase). Omit for numeric ops."),
                        format = Str("Optional display hint for numeric observables: raw, currency, percent, count."),
                        unit = Str("Optional unit label shown after raw values, e.g. 'USD', 'items'."),
                        description = Str("Optional one-line description of what this measures (usually set once at creation)."),
                    }, "name", "op")),

                Tool("list_observables", "List this project's Observables with current values, descriptions and last-updated times.",
                    Obj(new { }, Array.Empty<string>())),

                Tool("update_project", "Rename this project and/or revise its description (its stated goal — your north star, shown to Klives and used to seed every wake). Provide 'name', 'description', or both; omit either to leave it unchanged. Use it to keep the project's identity accurate as its scope sharpens. A name change also renames the Discord channel; a goal change reshapes your context, so make it deliberate — it shows on Klives' timeline.",
                    Obj(new
                    {
                        name = Str("New project name (optional). Omit to leave unchanged."),
                        description = Str("New description / stated goal (optional). Omit to leave unchanged."),
                    }, Array.Empty<string>())),

                Tool("spawn_sub_agent", "Spawn a sub-agent in a capability tier to do a piece of work. Pick the cheapest tier whose tools it needs. Prefer spawning over grinding through separable or parallelisable work yourself — a team of focused sub-agents running concurrently beats one Commander working serially.",
                    Obj(new
                    {
                        role = Str("Short role name, e.g. 'market-researcher'."),
                        tier = Str("One of: Text, TextImage, TextImageVideo, TextImageVideoAudio."),
                        objective = Str("What this agent should accomplish."),
                    }, "role", "tier", "objective")),

                Tool("retire_sub_agent", "Retire a sub-agent that has finished its work, freeing a slot against the cap.",
                    Obj(new { agentID = Str("The agent's ID.") }, "agentID")),

                Tool("send_agent_message", "Send a message to a sub-agent (rides the stimulus bus). Use to task or steer it.",
                    Obj(new { agentID = Str("Target agent ID."), message = Str("The message.") }, "agentID", "message")),

                Tool("request_user_approval", "Suspend and ask Klives to approve/deny an action that clears the escalation bar. Returns their decision and comment.",
                    Obj(new
                    {
                        title = Str("Short title of what you want to do."),
                        description = Str("What exactly you will do if approved."),
                        rationale = Str("Why it advances the goal and why it needs approval."),
                    }, "title", "description", "rationale")),

                Tool("request_budget_increase", "Ask Klives to raise the token budget, money budget, or agent cap. Returns their decision.",
                    Obj(new
                    {
                        kind = Str("One of: tokens, money, agents."),
                        amount = Num("Requested new limit."),
                        rationale = Str("Why the increase is justified by progress/plan."),
                    }, "kind", "amount", "rationale")),

                Tool("record_money_spend", "Record a real-money spend against the project's money budget. Spends at or below your autonomy threshold and within budget are recorded immediately; anything larger (or over budget) opens an approval gate first. Call this whenever you commit real money (a purchase, a subscription, an API top-up).",
                    Obj(new
                    {
                        amount = Num("Amount in USD."),
                        description = Str("What the money was/will be spent on."),
                    }, "amount", "description")),

                Tool("vault_save", "Store a credential/secret in the project vault under a name. Reference it later as {name} in typed text; you never see the value again.",
                    Obj(new { name = Str("Reference name."), value = Str("The secret value to store.") }, "name", "value")),

                Tool("vault_list", "List the names of secrets stored in the project vault (values are never shown).",
                    Obj(new { }, Array.Empty<string>())),

                Tool("request_human", "Ask a human (Klives) to clear a human-only obstacle such as a captcha or phone verification, surfaced through Discord.",
                    Obj(new { what = Str("What the human needs to do.") }, "what")),

                // ── KliveAgent shared memory (this project is part of Klives' assistant — memory transfers across projects) ──
                Tool("recall_memories", "Recall relevant facts from Klives' shared memory (spans all projects and KliveAgent). Use before assuming; Klives' preferences, credentials-context, and past learnings live here.",
                    Obj(new { query = Str("What you're trying to remember."), max = Num("Max results (default 8).") }, "query")),

                Tool("save_memory", "Save a durable fact to Klives' shared memory so it persists across wakes, projects, and KliveAgent. Save learnings, preferences, and important outcomes — not transient state.",
                    Obj(new { content = Str("The fact to remember."), tags = new { type = "array", items = new { type = "string" }, description = "Optional tags." } }, "content")),

                // ── cross-system knowledge + live web (KliveRAG) ──
                Tool("search_knowledge", "Search Klives' whole knowledge base — OTHER projects' decisions/outcomes, KliveAgent conversations/memories, Omniscience person facts, repo docs, cached web. Use this before spawning a research sub-agent: the answer may already exist. Returns cited snippets with doc ids.",
                    Obj(new { query = Str("Free-text search query."), max = Num("Max results (default 8).") }, "query")),

                Tool("read_knowledge_doc", "Open the FULL text of a knowledge document by the doc:<id> from a search_knowledge result (a whole conversation, a repo doc, another project's digest, a web page).",
                    Obj(new { docId = Str("The document id (doc:... value)."), maxTokens = Num("Max tokens (default 1500).") }, "docId")),

                Tool("web_search", "Search the LIVE web (self-hosted SearXNG, no API key). Use for current/external info. Returns titled results + URLs + snippets; fetchTop>0 also indexes the top pages for full-text follow-up via read_knowledge_doc. Prefer this over spawning a research sub-agent for a quick lookup.",
                    Obj(new { query = Str("The web search query."), maxResults = Num("Max results (default 6)."), fetchTop = Num("Index the top N result pages (0-3, default 2)."), timeRange = Str("Optional recency: day|week|month|year.") }, "query")),

                Tool("web_fetch", "Download ONE web page by URL, extract its text, index it, and return the text.",
                    Obj(new { url = Str("Absolute http(s) URL.") }, "url")),

                // ── work tools (text tier and up) ──
                Tool("run_script", "Run a C# script IN-PROCESS INSIDE Omnipotent (the host platform this project runs on). Use it for general script writing — computation, parsing, API orchestration — but know it is NOT sandboxed to that: the Omnipotent assembly is referenced, so through its namespaces/types the script can reach and control all of Omnipotent itself (live services, state, host resources). Globals: Http (HttpClient), Output(value), ReadFile/WriteFile/ListFiles (project volume). The script's return value and Output() lines come back to you. The escalation bar applies to what a script DOES, exactly as it would to any other action.",
                    Obj(new { code = Str("C# script body. End with an expression or use Output(...).") }, "code")),

                Tool("run_powershell", "Run a PowerShell script on the HOST machine (where Omnipotent runs), in its security context (elevated if Omnipotent is). Use for real host operations: installs, service/process control, git, filesystem, diagnostics. This is the host, NOT your desktop container. Returns exit code + stdout + stderr.",
                    Obj(new { script = Str("PowerShell script body."), timeoutSeconds = Num("Max seconds before the process tree is killed (default 120).") }, "script")),

                Tool("run_bash", "Run a Bash script on the HOST machine (WSL/Git Bash), in Omnipotent's security context. The host, NOT your desktop container. Returns exit code + stdout + stderr; says so if bash isn't installed.",
                    Obj(new { script = Str("Bash script body."), timeoutSeconds = Num("Max seconds before the process tree is killed (default 120).") }, "script")),

                Tool("http_request", "Make an HTTP request. Returns status + body (truncated).",
                    Obj(new
                    {
                        url = Str("Absolute http(s) URL."),
                        method = Str("GET (default), POST, PUT, DELETE…"),
                        body = Str("Request body for non-GET."),
                        contentType = Str("Body content type (default application/json)."),
                    }, "url")),

                Tool("read_file", "Read a text file from the project volume (shared with your desktop containers at /project).",
                    Obj(new { path = Str("Path relative to the volume root.") }, "path")),

                Tool("write_file", "Write a text file to the project volume. Creates directories as needed.",
                    Obj(new { path = Str("Path relative to the volume root."), content = Str("File content.") }, "path", "content")),

                Tool("list_files", "List files/directories on the project volume.",
                    Obj(new { path = Str("Directory relative to the volume root (default: root).") }, Array.Empty<string>())),

                // ── stimulus hooks: shape what wakes you ──
                Tool("create_stimulus_hook", "Subscribe to a stimulus source so events wake you (or a sub-agent). Sources: timer {intervalSeconds}, webhook {}, file-watch {path}, screen-diff {agentID?, intervalSeconds?, threshold?}, script {script, pollSeconds}, email {to?, from?, subjectContains?}, discord {channelId?, authorId?, contains?}, process-exit {processName?|pid?, pollSeconds?}. Spec filters are optional; the recognition criterion still triages what actually counts.",
                    Obj(new
                    {
                        sourceKind = Str("timer | webhook | file-watch | screen-diff | script | email | discord | process-exit"),
                        sourceSpec = new { type = "object", description = "Source-specific spec object (see tool description)." },
                        criterion = Str("Natural-language recognition criterion: when does a raw event count? Empty = always deliver."),
                        destinationAgentID = Str("Which agent the confirmed stimulus wakes (default: you)."),
                    }, "sourceKind")),

                Tool("list_stimulus_hooks", "List this project's stimulus hooks.", Obj(new { }, Array.Empty<string>())),

                Tool("delete_stimulus_hook", "Delete a stimulus hook by ID.",
                    Obj(new { hookID = Str("The hook's ID.") }, "hookID")),

                Tool("complete_project", "Declare the goal achieved. Opens an approval gate with Klives; on approval the project completes, the Discord channel archives and desktops are released.",
                    Obj(new { summary = Str("Evidence the goal is achieved.") }, "summary")),
            };
        }

        /// <summary>
        /// Computer-use tool definitions for agents whose tier permits desktop perception
        /// (Commander included — it is video-tier). Dispatched to the acting agent's container
        /// via ContainerToolAdapter; every mutating action returns a screenshot via the vision path.
        /// </summary>
        public static List<HFWrapper.HFTool> BuildComputerToolDefinitions()
        {
            HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
            {
                function = new HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
            };
            object Obj(object properties, params string[] required) => new { type = "object", properties, required };
            object Str(string desc) => new { type = "string", description = desc };
            object Num(string desc) => new { type = "number", description = desc };

            return new List<HFWrapper.HFTool>
            {
                Tool("computer_screenshot", "Capture the desktop. Returns the current frame — coordinates are pixels, (0,0) top-left.", Obj(new { }, Array.Empty<string>())),
                Tool("computer_click", "Click at (x, y).", Obj(new { x = Num("X pixel."), y = Num("Y pixel."), button = Str("left (default) | middle | right"), clicks = Num("1 (default) or 2 for double-click.") }, "x", "y")),
                Tool("computer_move", "Move the mouse to (x, y) without clicking.", Obj(new { x = Num("X"), y = Num("Y") }, "x", "y")),
                Tool("computer_drag", "Drag from (fromX, fromY) to (toX, toY).", Obj(new { fromX = Num("From X"), fromY = Num("From Y"), toX = Num("To X"), toY = Num("To Y"), button = Str("left (default)") }, "fromX", "fromY", "toX", "toY")),
                Tool("computer_mouse_down", "Press and HOLD a mouse button at (x, y) — pair with computer_mouse_up for custom drags/hold gestures.", Obj(new { x = Num("X pixel."), y = Num("Y pixel."), button = Str("left (default) | middle | right") }, "x", "y")),
                Tool("computer_mouse_up", "Release a held mouse button at (x, y).", Obj(new { x = Num("X pixel."), y = Num("Y pixel."), button = Str("left (default) | middle | right") }, "x", "y")),
                Tool("computer_scroll", "Scroll at a point.", Obj(new { direction = Str("down (default) | up | left | right"), amount = Num("Notches (default 5)."), x = Num("X (default: centre)"), y = Num("Y (default: centre)") }, Array.Empty<string>())),
                Tool("computer_type", "Type text at the current focus. Reference vault secrets as {name} — they substitute at keystroke time and you never see the value.", Obj(new { text = Str("Text to type.") }, "text")),
                Tool("computer_key", "Press a key or chord, e.g. 'enter', 'ctrl+l', 'alt+f4'.", Obj(new { key = Str("Key name or chord.") }, "key")),
                Tool("computer_key_down", "Press and HOLD a single key (e.g. 'shift') — pair with computer_key_up.", Obj(new { key = Str("Key name.") }, "key")),
                Tool("computer_key_up", "Release a held key.", Obj(new { key = Str("Key name.") }, "key")),
                Tool("computer_wait", "Wait for the screen to settle.", Obj(new { ms = Num("Milliseconds (default 1000, max 30000).") }, Array.Empty<string>())),
                Tool("computer_release_all", "Release all held buttons/keys and the shared-desktop input lock.", Obj(new { }, Array.Empty<string>())),
            };
        }
    }
}
