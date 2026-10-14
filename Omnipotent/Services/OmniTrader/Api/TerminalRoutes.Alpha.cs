using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Research.Alpha;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class TerminalRoutes
    {
        private AlphaLab LabOrThrow => parent.AlphaLab ?? throw new InvalidOperationException("The Alpha Lab is not running.");

        private async Task RegisterAlphaAsync()
        {
            await Get("/alpha/overview", OmniTraderPerms.AlphaRead, async req =>
            {
                // Settings, AgentBusy and the schedule stamps live in memory, where no store write
                // can invalidate a cached copy: a cached overview would undo a settings save.
                Live();
                await Json(req, await AlphaOverviewAsync(LabOrThrow));
            });

            await Get("/alpha/papers", OmniTraderPerms.AlphaRead, async req =>
            {
                var lab = LabOrThrow;
                string? status = req.userParameters.Get("status");
                string? text = req.userParameters.Get("q");
                double? minRel = ParseDouble(req.userParameters.Get("minRelevance"));
                string orderBy = req.userParameters.Get("orderBy") switch { "published" or "t1" => "t1", "updated" => "updated", _ => "n1" };
                int limit = ParseInt(req.userParameters.Get("limit"), 100, 1, 500);
                int offset = ParseInt(req.userParameters.Get("offset"), 0, 0, 1_000_000);
                var rows = await lab.ListPapersAsync(status, text, minRel, orderBy, limit, offset);
                await Json(req, new
                {
                    Total = await lab.CountPapersAsync(status, text, minRel),
                    Rows = rows.Select(PaperRow)
                });
            });

            await Get("/alpha/paper", OmniTraderPerms.AlphaRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var lab = LabOrThrow;
                var paper = await lab.GetPaperAsync(id);
                if (paper == null) { await NotFound(req); return; }
                await Json(req, await PaperDetailAsync(lab, paper));
            });

            await Post("/alpha/paper/verdict", OmniTraderPerms.AlphaManage, async req =>
            {
                var dto = Body<PaperVerdictDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Verdict)) { await Bad(req, "id and verdict required"); return; }
                await JsonOrNotFound(req, await LabOrThrow.SetVerdictAsync(dto.Id, dto.Verdict, dto.Notes, Actor(req)));
            });

            await Post("/alpha/paper/star", OmniTraderPerms.AlphaManage, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await JsonOrNotFound(req, await LabOrThrow.StarAsync(id, ParseBool(req.userParameters.Get("starred"), true)));
            });

            await Post("/alpha/paper/add", OmniTraderPerms.AlphaManage, async req =>
            {
                var dto = Body<PaperAddDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Title)) { await Bad(req, "title required"); return; }
                await Json(req, await LabOrThrow.AddPaperAsync(dto.Title, dto.Abstract, dto.Url, Actor(req)));
            });

            await Post("/alpha/analyse", OmniTraderPerms.AlphaManage, async req =>
            {
                var dto = Body<PaperIdsDto>(req);
                var run = await LabOrThrow.DispatchAsync("manual", CancellationToken.None, dto?.Ids is { Count: > 0 } ids ? ids : null);
                await Json(req, run == null ? new { Started = false, Reason = LabOrThrow.AgentBusy ? "KliveAgent is already analysing" : "Nothing queued" } : new { Started = true, Reason = (string?)null, Run = RunRow(run) });
            });

            await Post("/alpha/synthesise", OmniTraderPerms.AlphaManage, async req =>
            {
                var dto = Body<SynthesiseDto>(req) ?? new SynthesiseDto();
                var lab = LabOrThrow;
                var run = await lab.SynthesiseAsync("manual", CancellationToken.None, dto.PaperIds, dto.IdeaIds, dto.Note);
                string? reason = run != null ? null
                    : lab.AgentBusy ? "KliveAgent is already working on a run"
                    : "A synthesis needs ideas from at least two papers: pick two or more papers, or let analysis record more ideas first";
                await Json(req, new { Started = run != null, Reason = reason, Run = run == null ? null : RunRow(run) });
            });

            await Post("/alpha/ingest", OmniTraderPerms.AlphaManage, async req =>
            {
                var run = await LabOrThrow.IngestAsync("manual");
                await Json(req, RunRow(run));
            });

            await Get("/alpha/ideas", OmniTraderPerms.AlphaRead, async req =>
            {
                var lab = LabOrThrow;
                string? role = req.userParameters.Get("role");
                string? text = req.userParameters.Get("q");
                string? paperId = req.userParameters.Get("paperId");
                int limit = ParseInt(req.userParameters.Get("limit"), 200, 1, 1000);
                int offset = ParseInt(req.userParameters.Get("offset"), 0, 0, 1_000_000);
                var rows = await lab.ListIdeasAsync(role, text, paperId, limit, offset);
                await Json(req, new { Total = await lab.CountIdeasAsync(role, text, paperId), Rows = rows.Select(IdeaRow) });
            });

            await Post("/alpha/idea/delete", OmniTraderPerms.AlphaManage, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Deleted = await LabOrThrow.DeleteIdeaAsync(id) });
            });

            await Get("/alpha/candidates", OmniTraderPerms.AlphaRead, async req =>
            {
                var list = await LabOrThrow.ListCandidatesAsync(req.userParameters.Get("status"), req.userParameters.Get("paperId"),
                    ParseInt(req.userParameters.Get("limit"), 200, 1, 1000));
                await Json(req, list.Select(CandidateRow));
            });

            await Get("/alpha/candidate", OmniTraderPerms.AlphaRead, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await JsonOrNotFound(req, await LabOrThrow.GetCandidateAsync(id));
            });

            await Post("/alpha/candidate/reevaluate", OmniTraderPerms.AlphaManage, async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Json(req, new { Queued = await LabOrThrow.ReevaluateAsync(id) });
            });

            await Post("/alpha/candidate/status", OmniTraderPerms.AlphaManage, async req =>
            {
                var dto = Body<CandidateStatusDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id) || !Enum.TryParse<CandidateStatus>(dto.Status, true, out var status))
                { await Bad(req, "id and a valid status required"); return; }
                await JsonOrNotFound(req, await LabOrThrow.SetCandidateStatusAsync(dto.Id, status, dto.Notes));
            });

            await Post("/alpha/submit", OmniTraderPerms.AlphaManage, async req =>
            {
                var dto = Body<CandidateSubmitDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.StrategyId)) { await Bad(req, "strategyId required"); return; }
                var c = await LabOrThrow.SubmitAsync(new CandidateSubmission
                {
                    StrategyId = dto.StrategyId, Hypothesis = dto.Hypothesis ?? "",
                    PaperIds = (dto.PaperId == null ? Array.Empty<string>() : new[] { dto.PaperId }).Concat(dto.PaperIds ?? new()).ToList(),
                    IdeaIds = dto.IdeaIds ?? new(), TestSymbols = dto.TestSymbols, Interval = CandleClock.Parse(dto.Interval), By = Actor(req), Notes = dto.Notes
                });
                await Json(req, CandidateRow(c));
            });

            await Get("/alpha/runs", OmniTraderPerms.AlphaRead, async req =>
                await Json(req, (await LabOrThrow.ListRunsAsync(req.userParameters.Get("kind"), ParseInt(req.userParameters.Get("limit"), 50, 1, 500))).Select(RunRow)));

            await Get("/alpha/settings", OmniTraderPerms.AlphaRead, async req => await Json(req, LabOrThrow.Settings));

            await Post("/alpha/settings", OmniTraderPerms.AlphaManage, async req =>
            {
                var settings = Body<AlphaSettings>(req);
                if (settings == null) { await Bad(req, "Body must be settings"); return; }
                await LabOrThrow.SaveSettingsAsync(settings);
                await Json(req, LabOrThrow.Settings);
            });
        }

        /// <summary>The Alpha Lab header: settings, schedule stamps, counts, recent runs and the leaderboard.</summary>
        internal static async Task<object> AlphaOverviewAsync(AlphaLab lab)
        {
            var top = await lab.ListCandidatesAsync(null, null, 200);
            static DateTime? Stamp(DateTime t) => t == default || t == DateTime.MinValue ? null : t;
            return new
            {
                lab.Settings,
                lab.AgentBusy,
                LastIngestUtc = Stamp(lab.LastIngestUtc),
                LastDispatchUtc = Stamp(lab.LastDispatchUtc),
                LastSynthesisUtc = Stamp(lab.LastSynthesisUtc),
                Papers = await lab.PaperStatusCountsAsync(),
                Candidates = await lab.CandidateStatusCountsAsync(),
                Ideas = await lab.IdeaRoleCountsAsync(),
                Syntheses = top.Count(c => c.IsSynthesis()),
                Runs = (await lab.ListRunsAsync(null, 15)).Select(RunRow),
                Leaders = top.Where(c => c.Score.HasValue).OrderByDescending(c => c.Score).Take(10).Select(CandidateRow)
            };
        }

        /// <summary>A paper with the ideas recorded from it and every candidate that draws on it.</summary>
        internal static async Task<object> PaperDetailAsync(AlphaLab lab, ResearchPaper paper) => new
        {
            Paper = paper,
            Ideas = (await lab.ListIdeasAsync(paperId: paper.Id, limit: 200)).Select(IdeaRow),
            Candidates = (await lab.ListCandidatesAsync(null, paper.Id, 50)).Select(CandidateRow)
        };

        internal static object PaperRow(ResearchPaper p) => new
        {
            p.Id, p.Source, p.Title, Authors = p.Authors.Take(6), p.PublishedUtc, p.DiscoveredUtc, p.Venue, p.Url, p.PdfUrl, p.Citations,
            p.Relevance, p.Feasibility, p.Families, p.AssetClasses, p.DataNeeds, Status = p.Status.ToString(), p.Verdict, p.Starred,
            Candidates = p.CandidateIds.Count, Ideas = p.IdeaIds.Count, p.Categories,
            Snippet = p.Abstract.Length > 360 ? p.Abstract[..360] + "…" : p.Abstract
        };

        internal static object IdeaRow(AlphaIdea i) => new
        {
            i.Id, i.PaperId, i.PaperTitle, i.Role, i.Name, i.Summary, i.Formula, i.Parameters, i.Evidence, i.AssetClasses, i.Families,
            i.CandidateIds, Used = i.CandidateIds.Count, i.BestScore, i.CreatedBy, i.CreatedUtc, i.UpdatedUtc
        };

        internal static object RunRow(AlphaRun r) => new
        {
            r.Id, r.Kind, r.Status, r.Trigger, r.StartedUtc, r.FinishedUtc, r.PaperIds, r.IdeaIds, r.ConversationId, r.Summary, r.Found, r.Added, r.Error,
            DurationSec = r.FinishedUtc.HasValue ? Math.Round((r.FinishedUtc.Value - r.StartedUtc).TotalSeconds) : (double?)null
        };

        internal static object CandidateRow(AlphaCandidate c) => new
        {
            c.Id, c.PaperId, c.PaperTitle, PaperIds = c.SourcePaperIds(), c.Sources, c.IdeaIds, Synthesis = c.IsSynthesis(),
            c.StrategyId, c.StrategyName, c.StrategyVersion, c.StrategyKind, c.Hypothesis, c.TestSymbols,
            Interval = c.Interval.ToString(), Status = c.Status.ToString(), c.SubmittedBy, c.CreatedUtc, c.UpdatedUtc, c.Score,
            Sharpe = c.Evaluation?.Primary?.Sharpe, ReturnPct = c.Evaluation?.Primary?.ReturnPct, MaxDrawdownPct = c.Evaluation?.Primary?.MaxDrawdownPct,
            Trades = c.Evaluation?.Primary?.Trades, StressedSharpe = c.Evaluation?.PrimaryStressed?.Sharpe, OosSharpe = c.Evaluation?.OutOfSample?.Sharpe,
            c.Evaluation?.WalkForwardEfficiency, c.Evaluation?.DeflatedSharpe, Profitable = c.Evaluation?.Batch?.Profitable, Symbols = c.Evaluation?.Batch?.Symbols,
            // Synthesis minus its strongest single-paper part, in Sharpe.
            VsParts = c.Evaluation?.Constituents is { Count: > 0 } parts ? parts[0].Delta : (double?)null,
            Error = c.Evaluation?.Error
        };
    }

    public sealed class SynthesiseDto
    {
        public List<string>? PaperIds { get; set; }
        public List<string>? IdeaIds { get; set; }
        public string? Note { get; set; }
    }

    public sealed class PaperVerdictDto
    {
        public string Id { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string? Notes { get; set; }
    }

    public sealed class PaperAddDto
    {
        public string Title { get; set; } = "";
        public string? Abstract { get; set; }
        public string? Url { get; set; }
    }

    public sealed class PaperIdsDto { public List<string>? Ids { get; set; } }

    public sealed class CandidateStatusDto
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Notes { get; set; }
    }

    public sealed class CandidateSubmitDto
    {
        public string? PaperId { get; set; }
        /// <summary>Further papers for a synthesis (PaperId, when set, stays primary).</summary>
        public List<string>? PaperIds { get; set; }
        public List<string>? IdeaIds { get; set; }
        public string StrategyId { get; set; } = "";
        public string? Hypothesis { get; set; }
        public List<string>? TestSymbols { get; set; }
        public string? Interval { get; set; }
        public string? Notes { get; set; }
    }
}
