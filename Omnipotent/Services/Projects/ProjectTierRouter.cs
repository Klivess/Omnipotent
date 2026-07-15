namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Maps a capability tier to (a) the model that serves it and (b) the tools it may use
    /// (§6.1). Model routing now reads from PER-PROJECT settings (ProjectSettingsStore), not
    /// OmniSettings — every project owns its own price/model list. Tool gating is a static table:
    /// every tier owns a desktop; text workers operate it through structured OCR/DOM tools while
    /// image-capable workers also receive the complete pixel/coordinate surface.
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
        public IReadOnlyList<string> GetRoutesForTier(string projectID, ProjectAgentTier tier) => settings.Get(projectID).RoutesForTier(tier);
        public IReadOnlyList<string> GetCommanderRoutes(string projectID) => settings.Get(projectID).CommanderRoutes;
        public IReadOnlyList<string> GetUtilityRoutes(string projectID) => settings.Get(projectID).UtilityRoutes;

        // ── Tool gating ──

        /// <summary>Tools available to every tier (scripts/HTTP/files/messaging/agent lifecycle).</summary>
        private static readonly HashSet<string> TextTierTools = new(StringComparer.Ordinal)
        {
            "run_script", "execute_csharp", "run_powershell", "run_bash", "http_request",
            "grep", "search_code", "read_code_file", "list_code_directory", "get_global_path",
            "read_file", "write_file", "list_files", "stat_file", "resolve_project_path", "make_directory", "move_file", "copy_file", "delete_file", "mark_file_important",
            "send_agent_message", "spawn_sub_agent",
            "create_stimulus_hook", "list_stimulus_hooks", "delete_stimulus_hook",
            "request_user_approval", "request_budget_increase", "record_money_spend",
            "vault_save", "vault_list",
            "update_plan", "report_progress",
            "list_project_directives", "acknowledge_project_directive", "complete_project_directive",
            "update_observable", "list_observables",
            "update_checkpoint", "get_checkpoint",
            "account_register", "account_list", "account_update",
            "klivemail_create_mailbox", "klivemail_list_messages", "klivemail_get_message", "klivemail_wait_for_code",
            "recall_memories", "recall_memories_by_tag", "save_memory", "save_shortcut", "get_shortcuts", "delete_memory",
            "search_knowledge", "read_knowledge_doc", "web_search", "web_fetch",
            "query_events",
        };

        /// <summary>Tools reserved to the Commander (strategy/lifecycle-level), not sub-agents.</summary>
        private static readonly HashSet<string> CommanderOnlyTools = new(StringComparer.Ordinal)
        {
            "complete_project", "request_user_approval", "request_budget_increase", "request_human", "retire_sub_agent", "assign_plan_work", "record_money_spend",
            // Councils and the Grand Plan are the Commander's strategic instruments — sub-agents
            // execute under the plan, they don't set or revise it. (These are also absent from
            // TextTierTools, so IsToolAllowed already blocks them; this makes the intent explicit.)
            "convene_council", "submit_grand_plan", "amend_grand_plan", "get_grand_plan",
        };

        /// <summary>The full set of computer-use tools backed by an agent-owned container.</summary>
        private static readonly HashSet<string> ComputerTools = new(StringComparer.Ordinal)
        {
            "computer_screenshot", "computer_find_text", "computer_click_text", "computer_window_state", "computer_read_screen",
            "computer_move", "computer_mouse_move_relative", "computer_click", "computer_drag",
            "computer_mouse_down", "computer_mouse_up", "computer_scroll", "computer_type",
            "computer_key", "computer_key_down", "computer_key_up", "computer_release_all",
            "computer_wait", "computer_open_browser", "computer_navigate", "computer_browser_inspect", "computer_click_browser_control", "computer_focus_window", "computer_launch_app",
            "computer_terminal",
            "computer_clipboard_get", "computer_clipboard_set",
            "computer_confirm_action", "computer_confirm_and_click",
            // Not a computer_* perception tool, but a desktop-preflight — gate it to the tiers that
            // actually get a desktop so text/image sub-agents aren't offered a no-op.
            "ensure_desktop_ready",
        };

        /// <summary>Desktop operations whose observations are useful without raw image
        /// perception. Cheap text workers can therefore own a real computer through OCR,
        /// accessibility/DOM inspection and bounded terminal output.</summary>
        private static readonly HashSet<string> StructuredDesktopTools = new(StringComparer.Ordinal)
        {
            "computer_find_text", "computer_click_text", "computer_window_state",
            "computer_type", "computer_key", "computer_key_down", "computer_key_up", "computer_release_all",
            "computer_wait", "computer_open_browser", "computer_navigate", "computer_browser_inspect", "computer_click_browser_control",
            "computer_focus_window", "computer_launch_app", "computer_terminal",
            "computer_clipboard_get", "computer_clipboard_set", "computer_confirm_action",
            "ensure_desktop_ready",
        };

        /// <summary>
        /// Image-capable tiers receive the complete visual surface. Text-only tiers receive the
        /// structured subset whose OCR/DOM/terminal observations do not require raw pixels.
        /// </summary>
        public bool IsToolAllowed(ProjectAgentTier tier, string toolName)
        {
            if (TextTierTools.Contains(toolName)) return true;
            if (ComputerTools.Contains(toolName))
            {
                return tier switch
                {
                    ProjectAgentTier.TextImage or ProjectAgentTier.TextImageVideo or ProjectAgentTier.TextImageVideoAudio => true,
                    ProjectAgentTier.Text => StructuredDesktopTools.Contains(toolName),
                    _ => false,
                };
            }
            return false;
        }

        /// <summary>Commander-only tools (complete_project, budget increase) are gated out of sub-agent loops.</summary>
        public static bool IsCommanderOnly(string toolName) => CommanderOnlyTools.Contains(toolName);

        /// <summary>Every project agent owns a lazily provisioned desktop container.</summary>
        public static bool TierGetsDesktop(ProjectAgentTier tier) => true;
    }
}
