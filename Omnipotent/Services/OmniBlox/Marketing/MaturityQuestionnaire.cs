using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Marketing
{
    /// <summary>The model's pick for one question.</summary>
    public sealed class ChosenAnswer
    {
        public List<string> Options { get; } = new();
        public string? Text { get; set; }
        public string Fact { get; set; } = "";
    }

    /// <summary>The answers to submit for one questionnaire, or what is still wrong with them.</summary>
    public sealed class QuestionnaireResolution
    {
        public List<QuestionnaireAnswer> Answers { get; } = new();
        public List<string> Problems { get; } = new();
        /// <summary>"Question → answer (because …)", in the questionnaire's order.</summary>
        public List<string> Readable { get; } = new();
        public bool Ok => Problems.Count == 0 && Answers.Count > 0;
    }

    /// <summary>
    /// Roblox's Maturity &amp; Compliance questionnaire, answered from what the studio knows about a game: the content
    /// notes the compliance check prepares (the ones the owner approves with the launch), the game's design and the
    /// Game Kit's fixed behaviour. The questions are Roblox's and change without notice, so a model maps the facts
    /// onto whatever is asked; everything else here is deterministic: every shown question must be answered with
    /// its own options, follow-ups only when their option is chosen, and the rating Roblox previews must not exceed
    /// what the facts allow (<see cref="AgeCeiling"/>) before anything is published.
    /// </summary>
    public static class MaturityQuestionnaire
    {
        /// <summary>True of every OmniBlox game, whatever its theme: what the Game Kit does and never does.</summary>
        public static readonly string[] KitFacts =
        {
            "Players share multiplayer servers and see each other's Roblox avatars and display names (also on leaderboards).",
            "Communication: only Roblox's standard text chat, which Roblox filters. The game adds no chat of its own, does not turn on voice chat, and has no other way to message players.",
            "No player-to-player trading, gifting or marketplace.",
            "No free-form creation: players cannot build, draw, write or upload anything other players see. Any building is placing items from the game's own fixed set.",
            "No links to or mentions of anything outside Roblox, no ads, no sponsored content, no real-world money or prizes.",
            "Purchases are developer products and game passes for named, fixed in-game items or boosts. Nothing paid is random (no loot boxes, gacha or paid spins) and there is no gambling of any kind.",
            "No real-world brands, people, politics, religion or other sensitive real-world topics; no alcohol, drugs or tobacco; no romance; no crude humour; no strong language.",
            "The game asks for and stores no personal information; its own analytics are anonymous gameplay counts.",
        };

        /// <summary>The facts the answers rest on, as the model reads them.</summary>
        public static string Facts(GameDossier game, ComplianceReport compliance)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Game: \"{game.Store.Title}\"" + (game.Store.Genre.Length > 0 ? $" ({game.Store.Genre})" : "") + ".");
            if (game.Brief is { } brief)
            {
                if (!string.IsNullOrWhiteSpace(brief.Pitch)) sb.AppendLine("Pitch: " + brief.Pitch.Trim());
                if (!string.IsNullOrWhiteSpace(brief.CoreLoop)) sb.AppendLine("Core loop: " + brief.CoreLoop.Trim());
            }
            sb.AppendLine("Store description: " + game.Store.Description.Trim());
            sb.AppendLine();
            sb.AppendLine("Content (prepared from the game's design; the owner approved these with the launch):");
            foreach (var note in compliance.QuestionnaireNotes) sb.AppendLine("- " + note);
            sb.AppendLine();
            sb.AppendLine("The studio's Game Kit (true of this game):");
            foreach (var fact in KitFacts) sb.AppendLine("- " + fact);
            return sb.ToString().TrimEnd();
        }

        public static string FactsHash(string facts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)))[..16];

        /// <summary>
        /// The highest minimum age the facts allow Roblox to recommend. No violence or fear at all fits Roblox's
        /// all-ages rating (any recommendation below 9); mild, unrealistic violence or fear may reach 9+ but never 13+.
        /// A higher preview means the answers say more than the facts do, so nothing is published.
        /// </summary>
        public static int AgeCeiling(IEnumerable<string> notes) =>
            notes.Any(n => n.Contains(": mild", StringComparison.OrdinalIgnoreCase)) ? 12 : 8;

        /// <summary>A questionnaire as the model reads it: sections, then questions with their ids, types and options, follow-ups indented under the option that reveals them.</summary>
        public static string Render(Questionnaire q)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"QUESTIONNAIRE {q.Id}" + (q.Name.Length > 0 ? $" — {q.Name}" : ""));
            foreach (var section in q.Sections) RenderSection(sb, section, 0, null);
            return sb.ToString().TrimEnd();
        }

        private static void RenderSection(StringBuilder sb, QuestionnaireSection section, int depth, string? revealedBy)
        {
            string pad = new(' ', depth * 4);
            sb.AppendLine($"{pad}## Section: {section.Name}" + (revealedBy != null ? $" (shown when {revealedBy} is chosen)" : "") + (section.Description.Length > 0 ? $" — {Clip(section.Description, 400)}" : ""));
            foreach (var question in section.Questions) RenderQuestion(sb, question, depth);
        }

        private static void RenderQuestion(StringBuilder sb, QuestionnaireQuestion q, int depth)
        {
            string pad = new(' ', depth * 4);
            string type = KindOf(q) switch { QuestionnaireQuestion.Radio => "radio", QuestionnaireQuestion.CheckBox => "checkbox", _ => "text" };
            sb.AppendLine($"{pad}- [{type}] {q.Id}: {q.Text}" + (q.Help.Length > 0 ? $"  (help: {Clip(q.Help, 500)})" : "") + (type == "text" && q.MaxInputLength is int max ? $" (at most {max} characters)" : ""));
            foreach (var option in q.Options)
            {
                sb.AppendLine($"{pad}    · {option.Id}: {option.Text}" + (option.Help.Length > 0 ? $"  (help: {Clip(option.Help, 300)})" : ""));
                foreach (var child in option.ChildQuestions) RenderQuestion(sb, child, depth + 2);
                foreach (var child in option.ChildSections) RenderSection(sb, child, depth + 2, option.Id);
            }
        }

        /// <summary>A question's kind; an unknown type with options is answered like a radio question.</summary>
        private static string KindOf(QuestionnaireQuestion q) => q.Type switch
        {
            QuestionnaireQuestion.Radio or QuestionnaireQuestion.CheckBox or QuestionnaireQuestion.TextBox => q.Type,
            _ => q.Options.Count > 0 ? QuestionnaireQuestion.Radio : QuestionnaireQuestion.TextBox,
        };

        /// <summary>The model's reply as question id → its pick (unreadable entries are skipped; Resolve says what is missing).</summary>
        public static Dictionary<string, ChosenAnswer> ParseReply(string text)
        {
            var result = new Dictionary<string, ChosenAnswer>(StringComparer.Ordinal);
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return result;
            JObject j;
            try { j = JObject.Parse(text[start..(end + 1)]); }
            catch (JsonException) { return result; }
            foreach (var a in j["answers"] as JArray ?? new JArray())
            {
                if (a is not JObject o) continue;
                string? id = (string?)(o["question"] ?? o["questionId"] ?? o["id"]);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var pick = new ChosenAnswer { Text = (string?)o["text"], Fact = (string?)o["fact"] ?? "" };
                var choose = o["choose"] ?? o["options"] ?? o["option"];
                if (choose is JArray list) pick.Options.AddRange(list.Select(x => (string?)x).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
                else if (choose?.Type == JTokenType.String && (string?)choose is { Length: > 0 } one) pick.Options.Add(one.Trim());
                result[id.Trim()] = pick;
            }
            return result;
        }

        /// <summary>
        /// Walks the questionnaire as the dashboard shows it given these picks: every question shown must be answered
        /// with its own options (or text), follow-ups are shown only under a chosen option, and answers to hidden
        /// questions are dropped (the dashboard drops them too).
        /// </summary>
        public static QuestionnaireResolution Resolve(Questionnaire q, IReadOnlyDictionary<string, ChosenAnswer> picks)
        {
            var r = new QuestionnaireResolution();
            foreach (var section in q.Sections) WalkSection(section, picks, r);
            return r;
        }

        private static void WalkSection(QuestionnaireSection section, IReadOnlyDictionary<string, ChosenAnswer> picks, QuestionnaireResolution r)
        {
            foreach (var question in section.Questions) WalkQuestion(question, picks, r);
        }

        private static void WalkQuestion(QuestionnaireQuestion q, IReadOnlyDictionary<string, ChosenAnswer> picks, QuestionnaireResolution r)
        {
            string label = $"{q.Id} (\"{Clip(q.Text, 90)}\")";
            if (!picks.TryGetValue(q.Id, out var pick))
            {
                r.Problems.Add(label + " has no answer");
                return;
            }
            string because = pick.Fact.Length > 0 ? $" (because {Clip(pick.Fact, 160)})" : "";
            string kind = KindOf(q);
            if (kind == QuestionnaireQuestion.TextBox)
            {
                string text = (pick.Text ?? string.Join(", ", pick.Options)).Trim();
                if (text.Length == 0) { r.Problems.Add(label + " needs a written answer"); return; }
                if (q.MaxInputLength is int max && max > 0 && text.Length > max) text = text[..max];
                r.Answers.Add(new QuestionnaireAnswer(q.Id, JsonConvert.SerializeObject(text)));
                r.Readable.Add($"{Clip(q.Text, 140)} → \"{Clip(text, 120)}\"{because}");
                return;
            }

            var byId = q.Options.Where(o => o.Id.Length > 0).GroupBy(o => o.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var unknown = pick.Options.Where(id => !byId.ContainsKey(id)).ToList();
            if (unknown.Count > 0)
            {
                r.Problems.Add($"{label}: {string.Join(", ", unknown)} {(unknown.Count == 1 ? "is not one of its options" : "are not among its options")} ({string.Join(", ", byId.Keys)})");
                return;
            }
            var chosen = pick.Options.Distinct(StringComparer.Ordinal).Select(id => byId[id]).ToList();
            if (chosen.Count == 0) { r.Problems.Add(label + " has no option chosen"); return; }
            if (kind == QuestionnaireQuestion.Radio && chosen.Count != 1) { r.Problems.Add(label + " takes exactly one option"); return; }

            r.Answers.Add(new QuestionnaireAnswer(q.Id, kind == QuestionnaireQuestion.Radio
                ? JsonConvert.SerializeObject(chosen[0].Id)
                : JsonConvert.SerializeObject(chosen.Select(o => o.Id).ToList())));
            r.Readable.Add($"{Clip(q.Text, 140)} → {string.Join(" + ", chosen.Select(o => Clip(o.Text, 80)))}{because}");
            foreach (var option in chosen)
            {
                foreach (var child in option.ChildQuestions) WalkQuestion(child, picks, r);
                foreach (var child in option.ChildSections) WalkSection(child, picks, r);
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
