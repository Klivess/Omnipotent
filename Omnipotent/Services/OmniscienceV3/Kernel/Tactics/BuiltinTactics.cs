using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Tactics
{
    /// <summary>The tactics the kernel itself provides; modules add the rest through <c>IIntelModule.Tactics</c>.</summary>
    public static class BuiltinTactics
    {
        public static IReadOnlyList<ITactic> All { get; } = new ITactic[] { new SetAlgebraTactic(), new CohortBuilderTactic() };

        public static TacticOutputKind OutputKindFor(SelectionKind kind) => kind switch
        {
            SelectionKind.Person => TacticOutputKind.Cohort,
            SelectionKind.Message => TacticOutputKind.MessageSet,
            SelectionKind.Attachment or SelectionKind.Face or SelectionKind.FaceCluster => TacticOutputKind.ImageSet,
            SelectionKind.Assertion => TacticOutputKind.FactSet,
            SelectionKind.TimeRange => TacticOutputKind.Timeline,
            _ => TacticOutputKind.Dataset,
        };
    }

    /// <summary>X27: union / intersect / difference / sort / top-k over any sets ("late-night posters ∩ mention exams").</summary>
    public sealed class SetAlgebraTactic : TacticBase
    {
        public const string Key = "x27.set_algebra";

        private static readonly SelectionKind[] Combinable =
        {
            SelectionKind.Person, SelectionKind.Message, SelectionKind.Conversation, SelectionKind.Guild, SelectionKind.Attachment,
            SelectionKind.Face, SelectionKind.FaceCluster, SelectionKind.Assertion, SelectionKind.TimeRange,
        };

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X27",
            Title = "Set algebra",
            Category = TacticCategory.Transform,
            Description = "Combine two sets of the same kind (union, intersection, difference), or sort a set and keep the top k. People are matched through merges.",
            Accepts = Combinable,
            MinSelections = 1,
            MaxSelections = 2,
            ParameterSchemaJson = """
                {"type":"object","required":["op"],"properties":{
                  "op":{"type":"string","enum":["union","intersect","difference","sort","topk"]},
                  "k":{"type":"integer","minimum":1,"maximum":100000},
                  "order":{"type":"string","enum":["ScoreDescending","ScoreAscending","Label","Id"]}}}
                """,
            Outputs = new[] { TacticOutputKind.Cohort, TacticOutputKind.MessageSet, TacticOutputKind.ImageSet, TacticOutputKind.FactSet, TacticOutputKind.Timeline, TacticOutputKind.Dataset },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.overview.view",
            TypicalDurationMs = 200,
        };

        public override string? Validate(TacticInput input)
        {
            string op = (input.Param<string>("op") ?? "").ToLowerInvariant();
            switch (op)
            {
                case "union" or "intersect" or "difference":
                    if (input.Selections.Count != 2) return $"'{op}' needs two selections.";
                    if (input.Selections[0].Kind != input.Selections[1].Kind) return $"Both selections must be the same kind (got {input.Selections[0].Kind} and {input.Selections[1].Kind}).";
                    return null;
                case "sort":
                    return input.Selections.Count == 1 ? null : "'sort' takes one selection.";
                case "topk":
                    if (input.Selections.Count != 1) return "'topk' takes one selection.";
                    return input.Param<int?>("k") is > 0 ? null : "'topk' needs k ≥ 1.";
                default:
                    return "'op' must be one of union, intersect, difference, sort, topk.";
            }
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var input = context.Input;
            string op = input.Param<string>("op")!.ToLowerInvariant();
            Selection result = op switch
            {
                "union" => SetAlgebra.Combine(input.Selections[0], input.Selections[1], SetOperation.Union, context.CanonicalPerson),
                "intersect" => SetAlgebra.Combine(input.Selections[0], input.Selections[1], SetOperation.Intersect, context.CanonicalPerson),
                "difference" => SetAlgebra.Combine(input.Selections[0], input.Selections[1], SetOperation.Difference, context.CanonicalPerson),
                "sort" => SetAlgebra.Sort(input.Selections[0], Enum.TryParse<SortOrder>(input.Param<string>("order"), true, out var o) ? o : SortOrder.ScoreDescending),
                _ => SetAlgebra.TopK(input.Selections[0], input.Param<int>("k")),
            };
            IReadOnlyList<TacticOutput> outputs = new[]
            {
                new TacticOutput(BuiltinTactics.OutputKindFor(result.Kind), result, Summary: $"{op}: {result.Count} item(s)"),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X26: saves any set of people as a named cohort usable everywhere (operator.db: Klives-authored).</summary>
    public sealed class CohortBuilderTactic : TacticBase
    {
        public const string Key = "x26.cohort";

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X26",
            Title = "Save as cohort",
            Category = TacticCategory.Transform,
            Description = "Save a set of people as a named cohort (people are stored by their canonical id, so merges are respected).",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","required":["name"],"properties":{
                  "name":{"type":"string","minLength":1,"maxLength":120},
                  "cohortId":{"type":"string","description":"Update this cohort instead of creating a new one."}}}
                """,
            Outputs = new[] { TacticOutputKind.Cohort },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            TypicalDurationMs = 200,
        };

        public override string? Validate(TacticInput input) =>
            string.IsNullOrWhiteSpace(input.Param<string>("name")) ? "A cohort needs a name." : null;

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var source = context.Input.Selections[0];
            var members = source.Items.Select(i => context.CanonicalPerson(i.Id)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string name = context.Input.Param<string>("name")!.Trim();
            string cohortId = context.Input.Param<string>("cohortId") is { Length: > 0 } existing ? existing : "cohort_" + Guid.NewGuid().ToString("N")[..12];
            long now = context.Clock.NowMs;
            string definition = JsonSerializer.Serialize(new { origin = source.Origin, runId = context.RunId, sampled = source.Sampled }, KernelJson.Options);
            string membersJson = JsonSerializer.Serialize(members, KernelJson.Options);
            await context.Module.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO cohorts(cohort_id, name, definition_json, members_json, created_at_ms, updated_at_ms) VALUES($id,$n,$d,$m,$t,$t)
                    ON CONFLICT(cohort_id) DO UPDATE SET name=excluded.name, definition_json=excluded.definition_json, members_json=excluded.members_json, updated_at_ms=excluded.updated_at_ms
                    """, ("$id", cohortId), ("$n", name), ("$d", definition), ("$m", membersJson), ("$t", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'cohort.save',$id,$d)",
                    ("$t", now), ("$a", context.Actor), ("$id", cohortId), ("$d", JsonSerializer.Serialize(new { name, members = members.Count }, KernelJson.Options)));
            }, context.Cancellation).ConfigureAwait(false);
            var cohort = Selection.Of(SelectionKind.Person, members, $"cohort:{cohortId}");
            return new[] { new TacticOutput(TacticOutputKind.Cohort, cohort, Json(new { cohortId, name }), $"Saved cohort '{name}' with {members.Count} people.") };
        }
    }

    /// <summary>Saved cohorts (operator.db), readable as selections.</summary>
    public static class Cohorts
    {
        public sealed record Cohort(string CohortId, string Name, IReadOnlyList<string> Members, long CreatedAtMs, long UpdatedAtMs);

        public static IReadOnlyList<Cohort> List(Storage.OmniscienceStores stores)
        {
            using var c = stores.Operator.OpenRead();
            return c.Query(null, "SELECT cohort_id, name, members_json, created_at_ms, updated_at_ms FROM cohorts ORDER BY name", Map);
        }

        public static Cohort? Get(Storage.OmniscienceStores stores, string cohortId)
        {
            using var c = stores.Operator.OpenRead();
            return c.Query(null, "SELECT cohort_id, name, members_json, created_at_ms, updated_at_ms FROM cohorts WHERE cohort_id=$id", Map, ("$id", cohortId)).FirstOrDefault();
        }

        public static Selection ToSelection(Cohort cohort) => Selection.Of(SelectionKind.Person, cohort.Members, $"cohort:{cohort.CohortId}");

        private static Cohort Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new(r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<List<string>>(r.GetString(2), KernelJson.Options) ?? new List<string>(), r.GetInt64(3), r.GetInt64(4));
    }
}
