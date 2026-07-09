namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Maps a capability tier to (a) the model that serves it and (b) the tools it may use
    /// (§6.1). Model routing now reads from PER-PROJECT settings (ProjectSettingsStore), not
    /// OmniSettings — every project owns its own price/model list. Tool gating is a static table:
    /// computer-use requires video-tier perception, the image tier gets screenshots, the text
    /// tier gets scripts/HTTP/files only.
    /// </summary>
    public class ProjectTierRouter
    {
        private readonly ProjectSettingsStore settings;

        public ProjectTierRouter(ProjectSettingsStore settings)
        {
            this.settings = settings;
        }

        public string GetModelForTier(string projectID, ProjectAgentTier tier) => settings.Get(projectID).ModelForTier(tier);
        public string GetCommanderModel(string projectID) => settings.Get(projectID).CommanderModel;
        public string GetUtilityModel(string projectID) => settings.Get(projectID).UtilityModel;

        // ── Tool gating ──

        /// <summary>Tools available to every tier (scripts/HTTP/files/messaging/agent lifecycle).</summary>
        private static readonly HashSet<string> TextTierTools = new(StringComparer.Ordinal)
        {
            "run_script", "run_powershell", "run_bash", "http_request", "read_file", "write_file", "list_files",
            "send_agent_message", "spawn_sub_agent", "retire_sub_agent",
            "create_stimulus_hook", "list_stimulus_hooks", "delete_stimulus_hook",
            "request_user_approval", "request_budget_increase", "record_money_spend",
            "vault_save", "vault_list", "request_human",
            "update_plan", "report_progress",
            "update_observable", "list_observables",
            "recall_memories", "save_memory",
            "search_knowledge", "read_knowledge_doc", "web_search", "web_fetch",
        };

        /// <summary>Tools reserved to the Commander (strategy/lifecycle-level), not sub-agents.</summary>
        private static readonly HashSet<string> CommanderOnlyTools = new(StringComparer.Ordinal)
        {
            "complete_project", "request_budget_increase",
        };

        /// <summary>The full set of computer-use tools; require a container desktop (video tier).</summary>
        private static readonly HashSet<string> ComputerTools = new(StringComparer.Ordinal)
        {
            "computer_screenshot", "computer_find_text", "computer_click_text", "computer_window_state", "computer_read_screen",
            "computer_move", "computer_click", "computer_drag",
            "computer_mouse_down", "computer_mouse_up", "computer_scroll", "computer_type",
            "computer_key", "computer_key_down", "computer_key_up", "computer_release_all",
            "computer_wait", "computer_open_browser", "computer_navigate", "computer_focus_window", "computer_launch_app",
            "computer_clipboard_get", "computer_clipboard_set",
        };

        /// <summary>
        /// Whether a tier is allowed to call a tool. Computer-use requires the video tier (it
        /// needs to perceive a live desktop); the image tier gets one-shot screenshots; the text
        /// tier gets none. Everything non-computer is available to all tiers.
        /// </summary>
        public bool IsToolAllowed(ProjectAgentTier tier, string toolName)
        {
            if (TextTierTools.Contains(toolName)) return true;
            if (ComputerTools.Contains(toolName))
            {
                return tier switch
                {
                    ProjectAgentTier.TextImageVideo or ProjectAgentTier.TextImageVideoAudio => true,
                    ProjectAgentTier.TextImage => toolName == "computer_screenshot",
                    _ => false,
                };
            }
            return false;
        }

        /// <summary>Commander-only tools (complete_project, budget increase) are gated out of sub-agent loops.</summary>
        public static bool IsCommanderOnly(string toolName) => CommanderOnlyTools.Contains(toolName);

        /// <summary>Does this tier get a desktop container at all? Text tier does not (§4).</summary>
        public static bool TierGetsDesktop(ProjectAgentTier tier) =>
            tier is ProjectAgentTier.TextImageVideo or ProjectAgentTier.TextImageVideoAudio;
    }
}
