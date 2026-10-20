using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>Tools every role shares: the Playbook and the studio's own track record.</summary>
    public static class CommonTools
    {
        public static void AddPlaybookSearch(AgentToolbox box, StudioContext ctx) =>
            box.Add("playbook_search", "Search the studio's Playbook — lessons learned from earlier games (what worked, what failed, with evidence).",
                Object(Req("query", Str("What you want to know, e.g. 'obby difficulty' or 'game pass prices'")), Opt("topic", Str("Optional topic filter", Playbook.Topics))),
                args =>
                {
                    string? topic = Args.Str(args, "topic") is { Length: > 0 } t ? t : null;
                    var hits = ctx.Playbook.Search(Args.Required(args, "query"), topic, 8);
                    return ToolResult.Ok(hits.Count == 0 ? "No lessons match yet." : Playbook.Format(hits));
                });

        public static void AddPortfolio(AgentToolbox box, StudioContext ctx) =>
            box.Add("portfolio", "The studio's games so far: stage, template, launch date and headline numbers.",
                Object(),
                _ =>
                {
                    var games = ctx.Store.Read(s => s.Games.Values.OrderBy(g => g.CreatedUtc).Select(OmniBloxStore.Clone).ToList());
                    if (games.Count == 0) return ToolResult.Ok("No games yet — this is the first.");
                    var sb = new StringBuilder();
                    foreach (var g in games)
                    {
                        var days = g.Stats.Daily.TakeLast(14).ToList();
                        string numbers = days.Count == 0 ? "no analytics yet"
                            : $"avg DAU {days.Average(d => d.DailyActiveUsers ?? 0):0.#}, revenue {days.Sum(d => d.RevenueRobux ?? 0):0} R$ (14 d), D1 {days.Where(d => d.D1Retention != null).Select(d => d.D1Retention!.Value).DefaultIfEmpty().Average():0.##}";
                        sb.AppendLine($"- {g.GameId} \"{g.DisplayTitle}\" — {g.Stage}, {g.Brief?.Template ?? "?"} ({g.Brief?.Genre ?? "?"})" +
                                      (g.Publish.WentPublicUtc is DateTime live ? $", live since {live:yyyy-MM-dd}" : "") + $"; {numbers}");
                    }
                    return ToolResult.Ok(sb.ToString());
                });

        public static void AddLesson(AgentToolbox box, StudioContext ctx, string gameId) =>
            box.Add("add_lesson", "Record a lesson in the Playbook for future games. Be specific and cite the evidence (numbers).",
                Object(Req("topic", Str("Lesson topic", Playbook.Topics)), Req("text", Str("The lesson, one or two sentences")),
                    Req("evidence", Str("What shows it (numbers, observations)")), Opt("outcome", Str("worked, failed or neutral", "worked", "failed", "neutral")),
                    Opt("confidence", Num("0.05–0.95")), Opt("tags", StrList("Tags such as the genre or template"))),
                args =>
                {
                    string id = ctx.Playbook.Add(Args.Required(args, "topic"), Args.Required(args, "text"), Args.Str(args, "evidence"),
                        Args.Str(args, "outcome", "neutral"), Args.Num(args, "confidence") ?? 0.5, Args.List(args, "tags"), gameId);
                    return ToolResult.Ok($"Lesson saved ({id}).");
                });

        public static string Join(IEnumerable<string> lines) => string.Join("\n", lines);

        public static JObject Brief(GameBrief b) => JObject.FromObject(b);
    }
}
