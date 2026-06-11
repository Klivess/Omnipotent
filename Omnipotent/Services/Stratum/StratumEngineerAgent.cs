using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Prompting for the unified Stratum Engineer: the engineering doctrine system prompt and
    /// the per-turn seed (fresh project state + rolling summary + recent messages). The actual
    /// tool loop lives in <see cref="StratumEngineerTurnRunner"/>.
    /// </summary>
    public static class StratumEngineerAgent
    {
        /// <summary>How many recent timeline user/agent messages are replayed verbatim into each turn.</summary>
        public const int RecentMessagesVerbatim = 10;

        public static string BuildSystemPrompt() =>
@"You are the Stratum Engineer — a single, persistent mechatronics engineer responsible for an entire hardware project: mechanical CAD, electronics, firmware, and simulation. You converse with the user about their device and you BUILD it through your tools. You are the only engineer on this project; there is no other agent.

ENGINEERING DOCTRINE (follow this order; do not skip steps):
1. PLAN first: capture the device concept and subtasks with update_device_plan. Ask the user concise clarifying questions in plain prose when requirements are genuinely ambiguous — otherwise make a sensible assumption and record it in the plan's Assumptions.
2. DIMENSIONS before geometry: put shared values (wall thicknesses, clearances) in the registry with set_dimensions and reference the injected Python constants in scripts. Never hardcode a value twice.
3. BLUEPRINT before parts: lay out every part's slot AND declare a typed CONTRACT for every pair of parts that touch or fasten (update_assembly_blueprint). The host derives both sides' exact mating geometry from each contract — you never invent hole positions or diameters in a script. Parts without a contract must keep clearance.
4. GENERATE one part at a time with generate_part. The host measures everything: validity, watertightness, bbox vs slot (3%), per-feature probes, exact collisions against neighbours. Read the report — the numbers are ground truth, your intent is not. When a render follows, LOOK at it: confirm the shape, orientation (principalAxis), and features match the part's purpose before moving on.
5. COMPOSE the assembly (compose_assembly) after parts change; fix every collision/clearance failure before proceeding.
6. ELECTRONICS: pick modules only from the catalog (search_module_library), wire them with update_electronics_design, place them with update_electronics_layout, then re-generate hosting parts so they grow the derived bosses/cutouts, then enrich_bom.
7. FIRMWARE: write_firmware_files against the design's actual wiring (exact pin names), then compile_firmware and fix errors.
8. SIMULATE load-bearing parts with run_fea when structural integrity matters.
9. APPROVAL GATES: request_user_approval at milestones — the plan, the blueprint, each verified part, the electronics design, the layout, the firmware, the final assembly. A rejection comment is a requirement: fold it in and redo the work.

CONVERSATION RULES:
- Your plain-prose replies go to the user. Be concise and concrete; report measured numbers, not adjectives.
- When the user asks a question, answer it from project state (get_project_state) — do not regenerate things just to answer.
- When the user requests a change, identify the smallest set of artifacts affected (use contracts to see what else a change touches), update those, and re-verify.
- Never claim something is built/verified unless a tool result in THIS conversation proves it.
- All dimensions in millimetres, angles in degrees.";

        /// <summary>
        /// Builds the seed user message for a new turn: fresh PROJECT STATE + rolling summary +
        /// recent conversation verbatim + the user's new message. The within-turn tool history
        /// lives in the structured session; across turns only this seed (and the summary) survive.
        /// </summary>
        public static string BuildTurnSeed(
            StratumEngineerTurnContext tc,
            StratumConversationMeta meta,
            List<StratumTimelineEvent> recentMessages,
            string userText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("── PROJECT STATE (fresh from storage — trust this over memory) ──");
            sb.AppendLine(StratumEngineerTools.BuildProjectStateBlock(tc));
            if (!string.IsNullOrWhiteSpace(meta.RollingSummary))
            {
                sb.AppendLine("── CONVERSATION SUMMARY (older turns, compacted) ──");
                sb.AppendLine(meta.RollingSummary.Trim());
            }
            if (recentMessages.Count > 0)
            {
                sb.AppendLine("── RECENT CONVERSATION ──");
                foreach (var m in recentMessages)
                    sb.AppendLine($"[{(m.Author == "user" ? "USER" : "YOU")}] {Truncate(m.Text, 600)}");
            }
            sb.AppendLine("── NEW USER MESSAGE ──");
            sb.AppendLine(userText);
            return sb.ToString();
        }

        /// <summary>Prompt for the utility-model rolling-summary compaction.</summary>
        public static string BuildSummaryPrompt(string existingSummary, List<StratumTimelineEvent> turnEvents)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You maintain the rolling memory of an engineering conversation. Merge the existing summary with the new turn below into ONE compact summary (≤ 300 words). Keep: decisions made, requirements/constraints the user stated, what was built and its verification status, open questions. Drop: tool mechanics, pleasantries, superseded states. Output ONLY the summary text.");
            sb.AppendLine();
            sb.AppendLine("EXISTING SUMMARY:");
            sb.AppendLine(string.IsNullOrWhiteSpace(existingSummary) ? "(none)" : existingSummary);
            sb.AppendLine();
            sb.AppendLine("NEW TURN:");
            foreach (var e in turnEvents)
            {
                switch (e.Type)
                {
                    case StratumTimelineEventTypes.UserMessage:
                        sb.AppendLine($"[USER] {Truncate(e.Text, 500)}");
                        break;
                    case StratumTimelineEventTypes.AgentMessage:
                    case StratumTimelineEventTypes.Thought:
                        sb.AppendLine($"[ENGINEER] {Truncate(e.Text, 500)}");
                        break;
                    case StratumTimelineEventTypes.ToolCall:
                        sb.AppendLine($"[tool] {e.ToolName}");
                        break;
                    case StratumTimelineEventTypes.ToolResult:
                        sb.AppendLine($"[tool result] {e.ToolName}: {Truncate(e.Text, 240)}");
                        break;
                    case StratumTimelineEventTypes.GateResolved:
                        sb.AppendLine($"[gate] {Truncate(e.Text, 200)}");
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
