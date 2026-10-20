using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniBlox.Agents;

namespace Omnipotent.Services.OmniBlox.Assets
{
    public sealed class LabelResult
    {
        public required AssetLabel Label { get; init; }
        public long PromptTokens { get; init; }
        public long CachedTokens { get; init; }
        public long CompletionTokens { get; init; }
    }

    /// <summary>The labeller's reference examples, loaded and ready to send (see <see cref="Core.LabelReferenceSet"/>).</summary>
    public sealed class LabelReference
    {
        public required int Version { get; init; }
        public required IReadOnlyList<(string Text, IReadOnlyList<byte[]> Images, string Answer)> Examples { get; init; }
    }

    /// <summary>
    /// Writes the plain-language half of an asset's label with a vision model: what it is, its style and
    /// category, what it is good for, which side is its front, whether players can stand on it, and how
    /// good it looks. One one-shot call per asset.
    ///
    /// Shaped for AIRouter's prefix cache. A call opens with bytes every other call shares — the system
    /// prompt, then (when the harvester passes them) a few frozen reference examples, earlier models with
    /// their labels — so the provider prefills that head once and serves it from cache afterwards. Only the
    /// new model's description and pictures are new, and they are kept small: the four schematic views
    /// travel as one sheet instead of four pictures.
    /// </summary>
    public sealed class AssetLabeler
    {
        public static readonly string[] Categories =
        {
            "nature", "building", "structure", "furniture", "prop", "decoration", "vehicle", "character", "creature",
            "obstacle", "platform", "terrain", "light", "sign", "tool", "effect", "food", "other",
        };

        /// <summary>
        /// Bump on any change to the system prompt, the request text or the answer format. A reference set
        /// built for another version is rebuilt instead of being shown in a stale shape.
        /// </summary>
        internal const int PromptVersion = 3;

        /// <summary>
        /// The quality scale a label's score is on. 0: the first labels, each judged alone against a one-line
        /// scale, which put nearly everything at 6 or 7. 1: judged against calibrated reference examples.
        /// Labels on an older scale are re-rated when the labeller is idle; bump only when scores stop being
        /// comparable with the ones before.
        /// </summary>
        public const int ScaleVersion = 1;

        /// <summary>
        /// Reference examples shown before each model, picked at four levels of a calibrated scale (see
        /// <see cref="AnchorLevels"/>): the consistent context a score needs, since a model judged with nothing
        /// to compare it to lands in the safe middle. Each costs about one model's worth of tokens, all served
        /// from cache while the labeller is busy; the new model is then roughly the last 15% of the prompt.
        /// </summary>
        public const int ReferenceExamples = 4;

        /// <summary>Fewer than this and the set is not worth showing (one example over-anchors).</summary>
        public const int MinReferenceExamples = 2;

        /// <summary>Models scored side by side to calibrate the scale; at least <see cref="MinCalibrationPool"/>.</summary>
        public const int CalibrationPoolSize = 12;
        public const int MinCalibrationPool = 6;

        /// <summary>The calibrated scores the reference examples are chosen nearest to, in the order shown: the top
        /// and the bottom first, then the two between, so the example just before the new model is a middle one.</summary>
        internal static readonly double[] AnchorLevels = { 9, 2, 7, 4 };

        private const string QualityScale =
@"  9-10  polished, detailed and coherent: as good as free models get
  7-8   good: clean and appealing, usable as it is
  5-6   plain: simple or a little rough, fine as filler
  3-4   crude: low detail, odd proportions, clashing colours or visible flaws
  1-2   broken: floating or missing pieces, untextured blobs, barely recognisable
  0     a real brand or franchise character, or anything unsuitable for children";

        internal const string SystemPrompt =
@"You label free 3D models from the Roblox Creator Store so a game-building AI can find and place them.

For each model you get Roblox's own thumbnail (its true look) and one sheet of four schematic renders made from the model's actual parts (its true size, shape and orientation; mesh parts show as dashed boxes, so judge their detail from the thumbnail). The sheet's panels are: top-left FRONT (looking at the model's −Z side), top-right RIGHT (+X side), bottom-left TOP (the −Z side at the top edge) and bottom-right a 3/4 view captioned with the exact size in studs. A Roblox character is about 5 studs tall.

Any models before the last one in this conversation are reference examples, already scored on a calibrated scale: they show the format and what each quality score looks like. Label only the last model.

How to score quality: compare the model with the reference examples — is it better or worse than each of them, and by how much? — and place it on this scale, using all of it. Do not settle in the middle out of caution; a plain model is a 5, a crude one a 3.
" + QualityScale + @"

Reply with ONE JSON object and nothing else:
{
  ""what"": ""short noun phrase, e.g. 'low-poly pine tree'"",
  ""category"": one of [" + "nature, building, structure, furniture, prop, decoration, vehicle, character, creature, obstacle, platform, terrain, light, sign, tool, effect, food, other" + @"],
  ""subcategory"": ""e.g. tree, house, fence, chair, car"",
  ""style"": ""e.g. low-poly, realistic, cartoon, voxel, stylized, sci-fi, medieval, modern"",
  ""themes"": [""forest"", ""winter"", ...],
  ""description"": ""2-3 sentences: what it looks like, its parts, anything notable"",
  ""goodFor"": [""uses in a game, e.g. 'forest decoration', 'obby obstacle'""],
  ""pairsWith"": [""things it would look right next to""],
  ""front"": ""front"" | ""right"" | ""back"" | ""left""  (the side that is its real front: a door, a face, a sign's text, a vehicle's nose; 'front' if it has none),
  ""walkable"": true if a player could stand or walk on top of it,
  ""quality"": 0-10 on the scale above,
  ""issues"": [""problems: floating pieces, missing textures, offensive or copyrighted content, too detailed for mobile, ...""]
}
Be literal and concrete. If it depicts a real brand, franchise character or anything inappropriate for children, say so in issues and give quality 0.";

        internal const string CalibrationPrompt =
@"You calibrate the quality scale of a library of free 3D models from the Roblox Creator Store, which a game-building AI picks from. You see several models side by side, each as Roblox's own thumbnail, numbered in the order of the pictures.

Compare them with each other and score each model's quality on this scale. Use the range the models really span, and let the scores show the differences between them: two models get the same score only if they really are equally good.
" + QualityScale + @"

Reply with ONE JSON object and nothing else:
{ ""scores"": [ { ""model"": 1, ""quality"": 0-10, ""issues"": [""what costs it points""] }, ... ] }";

        private static readonly string[] SheetPositions = { "top-left", "top-right", "bottom-left", "bottom-right" };

        private readonly IOmniBloxLlm llm;

        public AssetLabeler(IOmniBloxLlm llm) => this.llm = llm;

        public async Task<LabelResult> LabelAsync(AssetRecord record, byte[]? thumbnail, IReadOnlyList<(string View, byte[] Png)> renders,
            CancellationToken ct, LabelReference? reference = null)
        {
            string session = "omniblox-label-" + record.AssetId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
            llm.StartSession(session, SystemPrompt);
            try
            {
                // The shared head: byte-identical in every call that carries this reference set.
                foreach (var (text, images, answer) in reference?.Examples ?? Array.Empty<(string, IReadOnlyList<byte[]>, string)>())
                {
                    llm.AppendStableUser(session, text, images);
                    llm.AppendAssistant(session, answer);
                }
                var (request, pictures) = Request(record, thumbnail, renders);
                llm.AppendStableUser(session, request, pictures);
                var turn = await llm.QueryAsync(session, Array.Empty<ToolDefinition>(),
                    new LlmCallOptions { MaxTokens = 900, Thinking = "low", WorkClass = AIRouterWorkClass.OneShot }, ct);
                if (!turn.Success) throw new InvalidOperationException("Labelling call failed: " + turn.Error);
                var label = Parse(turn.Text);
                label.Model = turn.Model ?? "";
                return new LabelResult { Label = label, PromptTokens = turn.PromptTokens, CachedTokens = turn.CachedTokens, CompletionTokens = turn.CompletionTokens };
            }
            finally
            {
                llm.EndSession(session);
            }
        }

        /// <summary>One model's user turn: its description, then its pictures (the thumbnail, then the render sheet).</summary>
        internal static (string Text, List<byte[]> Images) Request(AssetRecord record, byte[]? thumbnail, IReadOnlyList<(string View, byte[] Png)> renders)
        {
            var images = new List<byte[]>();
            bool hasThumbnail = thumbnail is { Length: > 0 };
            if (hasThumbnail) images.Add(thumbnail!);
            var sheet = SchematicRenderer.ComposeSheet(renders);
            var sheetViews = sheet == null
                ? new List<string>()
                : SchematicRenderer.SheetOrder.Where(v => renders.Any(r => r.View == v && r.Png is { Length: > 0 })).ToList();
            if (sheet != null) images.Add(sheet);
            return (Describe(record, hasThumbnail, sheetViews), images);
        }

        internal static string Describe(AssetRecord r, bool hasThumbnail, IReadOnlyList<string> sheetViews)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Model #{r.AssetId}: \"{r.Name}\" by {r.CreatorName}{(r.CreatorVerified ? " (verified)" : "")}.");
            if (!string.IsNullOrWhiteSpace(r.Description)) sb.AppendLine("Creator's description: " + Clip(r.Description, 300));
            if (!string.IsNullOrWhiteSpace(r.CategoryPath)) sb.AppendLine("Store category: " + r.CategoryPath.Replace("__", " / "));
            if (r.Measurements is { } m)
            {
                sb.AppendLine(FormattableString.Invariant($"Measured size: {m.SizeX:0.##} wide (X) × {m.SizeY:0.##} tall (Y) × {m.SizeZ:0.##} deep (Z) studs; {m.Proportions}; {m.PartCount} visible parts ({m.MeshPartCount} mesh)."));
                if (m.Colors.Count > 0) sb.AppendLine("Main colours: " + string.Join(", ", m.Colors.Select(c => FormattableString.Invariant($"{c.Name} {c.Hex} {c.Share:P0}"))));
                if (m.Materials.Count > 0) sb.AppendLine("Materials: " + string.Join(", ", m.Materials.Select(x => FormattableString.Invariant($"{x.Material} {x.Share:P0}"))));
                var extras = new List<string>();
                if (m.LightCount > 0) extras.Add($"{m.LightCount} light(s)");
                if (m.ParticleCount > 0) extras.Add($"{m.ParticleCount} effect(s)");
                if (m.HasHumanoid) extras.Add("a humanoid rig");
                if (m.HasSeat) extras.Add("a seat");
                if (extras.Count > 0) sb.AppendLine("Also contains: " + string.Join(", ", extras) + ".");
            }
            if (r.ScriptsRemoved > 0) sb.AppendLine($"{r.ScriptsRemoved} script(s) were removed for safety; label the model as the static object that remains.");
            var pictures = new List<string>();
            if (hasThumbnail) pictures.Add("Roblox thumbnail");
            if (sheetViews.Count == 1) pictures.Add($"one schematic render ({ViewName(sheetViews[0])})");
            else if (sheetViews.Count > 1)
                pictures.Add("one sheet of schematic renders (" + string.Join(", ", sheetViews.Select((v, i) => $"{SheetPositions[i]} {ViewName(v)}")) + ")");
            sb.AppendLine("Images, in order: " + (pictures.Count > 0 ? string.Join(", ", pictures) : "none") + ".");
            return sb.ToString();
        }

        private static string ViewName(string view) => view switch
        {
            "front" => "FRONT",
            "right" => "RIGHT",
            "top" => "TOP",
            "iso" => "3/4 view",
            _ => view,
        };

        /// <summary>A stored label written back in the reply format: the answer half of a reference example.</summary>
        internal static string Answer(AssetLabel l) => JsonConvert.SerializeObject(new
        {
            what = l.What,
            category = l.Category,
            subcategory = l.Subcategory,
            style = l.Style,
            themes = l.Themes,
            description = l.Description,
            goodFor = l.GoodFor,
            pairsWith = l.PairsWith,
            front = (int)MathF.Round(l.FrontYaw) switch { 90 => "right", 180 => "back", 270 => "left", _ => "front" },
            walkable = l.Walkable,
            quality = Math.Round(l.Quality, 1),
            issues = l.Issues,
        });

        public sealed class Calibration
        {
            /// <summary>Asset id → its score on the calibrated scale and what cost it points.</summary>
            public Dictionary<long, (double Quality, List<string> Issues)> Scores { get; init; } = new();
            public long PromptTokens { get; init; }
            public long CachedTokens { get; init; }
            public long CompletionTokens { get; init; }
        }

        /// <summary>
        /// Scores a pool of models side by side. Judged one at a time, a model has nothing to be measured
        /// against and lands in the safe middle (the first library sat almost entirely at 6-7); compared with
        /// each other the same models spread out. The reference examples are chosen on this scale, so every
        /// later label is anchored to it. Throws only when the call itself fails; an unreadable reply gives
        /// no scores.
        /// </summary>
        public async Task<Calibration> CalibrateAsync(IReadOnlyList<(AssetRecord Record, byte[] Thumbnail)> pool, CancellationToken ct)
        {
            string session = "omniblox-calibrate-" + Guid.NewGuid().ToString("N")[..8];
            llm.StartSession(session, CalibrationPrompt);
            try
            {
                var sb = new StringBuilder("The models, in the order of the pictures:\n");
                for (int i = 0; i < pool.Count; i++)
                {
                    var r = pool[i].Record;
                    string what = r.Label?.What is { Length: > 0 } w ? w : r.Name;
                    string size = r.Measurements is { } m ? FormattableString.Invariant($", {m.SizeX:0.#} × {m.SizeY:0.#} × {m.SizeZ:0.#} studs") : "";
                    sb.AppendLine($"{i + 1}. \"{r.Name}\": {what}{size}");
                }
                llm.AppendStableUser(session, sb.ToString(), pool.Select(p => p.Thumbnail).ToList());
                var turn = await llm.QueryAsync(session, Array.Empty<ToolDefinition>(),
                    new LlmCallOptions { MaxTokens = 4000, Thinking = "medium", WorkClass = AIRouterWorkClass.OneShot }, ct);
                if (!turn.Success) throw new InvalidOperationException("Calibration call failed: " + turn.Error);
                Dictionary<long, (double, List<string>)> scores;
                try { scores = ParseCalibration(turn.Text, pool.Select(p => p.Record.AssetId).ToList()); }
                catch (Exception ex) when (ex is FormatException or JsonException) { scores = new(); }
                return new Calibration { Scores = scores, PromptTokens = turn.PromptTokens, CachedTokens = turn.CachedTokens, CompletionTokens = turn.CompletionTokens };
            }
            finally
            {
                llm.EndSession(session);
            }
        }

        internal static Dictionary<long, (double Quality, List<string> Issues)> ParseCalibration(string text, IReadOnlyList<long> ids)
        {
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new FormatException("The calibration was not JSON.");
            var j = JObject.Parse(text[start..(end + 1)]);
            var scores = new Dictionary<long, (double, List<string>)>();
            foreach (var s in (j["scores"] as JArray ?? new JArray()).OfType<JObject>())
            {
                int n = (int?)s["model"] ?? 0;
                if (n < 1 || n > ids.Count || s["quality"] is not { Type: JTokenType.Integer or JTokenType.Float or JTokenType.String } q) continue;
                if (!double.TryParse(q.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double quality)) continue;
                var issues = (s["issues"] as JArray)?.Select(x => x.ToString().Trim()).Where(x => x.Length > 0).Take(6).ToList() ?? new();
                scores[ids[n - 1]] = (Math.Clamp(quality, 0, 10), issues);
            }
            return scores;
        }

        /// <summary>
        /// Up to <paramref name="size"/> labelled models to calibrate on, chosen to span the range: alternately
        /// the most and the least promising by what the store says (likes, votes, a verified creator) and by
        /// any stored score, spread over as many categories as possible. Never a quality-0 model (brands and
        /// unsuitable content): it would be shown in every call.
        /// </summary>
        internal static List<AssetRecord> PickCalibrationPool(IEnumerable<AssetRecord> candidates, int size = CalibrationPoolSize)
        {
            static double Promise(AssetRecord r) =>
                r.UpVotePercent / 100.0 * Math.Log10(10 + r.UpVotes) + (r.CreatorVerified ? 0.5 : 0) + r.Label!.Quality / 10.0;
            var ranked = candidates
                .Where(c => c.Label is { What.Length: > 0 } && c.Label.Quality > 0)
                .GroupBy(c => c.AssetId).Select(g => g.First())
                .OrderByDescending(Promise).ThenBy(c => c.AssetId)
                .ToList();
            var alternating = new List<AssetRecord>();
            for (int i = 0, k = ranked.Count - 1; i <= k; i++, k--)
            {
                alternating.Add(ranked[i]);
                if (k != i) alternating.Add(ranked[k]);
            }
            var pool = new List<AssetRecord>();
            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in alternating) if (pool.Count < size && categories.Add(c.Label!.Category)) pool.Add(c);
            foreach (var c in alternating) if (pool.Count < size && !pool.Contains(c)) pool.Add(c);
            return pool;
        }

        /// <summary>
        /// The reference examples: for each of <see cref="AnchorLevels"/> the calibrated model scored nearest to
        /// it, preferring a category not yet shown. When the pool does not span the whole scale the anchors
        /// carry their real scores, so the examples never claim a spread the models do not have.
        /// </summary>
        internal static List<(AssetRecord Record, double Quality, List<string> Issues)> PickReferences(
            IReadOnlyList<AssetRecord> pool, IReadOnlyDictionary<long, (double Quality, List<string> Issues)> scores)
        {
            var scored = pool.Where(r => scores.TryGetValue(r.AssetId, out var s) && s.Quality > 0).ToList();
            var picked = new List<(AssetRecord, double, List<string>)>();
            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (double level in AnchorLevels)
            {
                var nearest = scored
                    .Where(r => picked.All(p => p.Item1.AssetId != r.AssetId))
                    .OrderBy(r => Math.Abs(scores[r.AssetId].Quality - level))
                    .ThenBy(r => categories.Contains(r.Label!.Category) ? 1 : 0)
                    .ThenByDescending(r => r.UpVotes).ThenBy(r => r.AssetId)
                    .FirstOrDefault();
                if (nearest == null) break;
                categories.Add(nearest.Label!.Category);
                picked.Add((nearest, scores[nearest.AssetId].Quality, scores[nearest.AssetId].Issues));
            }
            return picked;
        }

        internal static AssetLabel Parse(string text)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) throw new FormatException("The model did not return a JSON label.");
            var j = JObject.Parse(text[start..(end + 1)]);
            List<string> List(string key) => (j[key] as JArray)?.Select(x => x.ToString().Trim()).Where(x => x.Length > 0).Take(12).ToList() ?? new();
            string category = ((string?)j["category"] ?? "other").Trim().ToLowerInvariant();
            if (!Categories.Contains(category)) category = "other";
            string front = ((string?)j["front"] ?? "front").Trim().ToLowerInvariant();
            return new AssetLabel
            {
                What = Clip(((string?)j["what"] ?? "").Trim(), 120),
                Category = category,
                Subcategory = Clip(((string?)j["subcategory"] ?? "").Trim().ToLowerInvariant(), 60),
                Style = Clip(((string?)j["style"] ?? "").Trim().ToLowerInvariant(), 60),
                Themes = List("themes"),
                Description = Clip(((string?)j["description"] ?? "").Trim(), 600),
                GoodFor = List("goodFor"),
                PairsWith = List("pairsWith"),
                FrontYaw = front switch { "right" => 90, "back" => 180, "left" => 270, _ => 0 },
                Walkable = (bool?)j["walkable"] ?? false,
                Quality = Math.Clamp((double?)j["quality"] ?? 5, 0, 10),
                Issues = List("issues"),
            };
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
