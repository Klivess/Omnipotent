using System.Globalization;
using System.Text;
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

    /// <summary>
    /// Writes the plain-language half of an asset's label with a vision model: what it is, its style and
    /// category, what it is good for, which side is its front, whether players can stand on it, and how
    /// good it looks. One cheap one-shot call per asset; the system prompt never changes, so its prefix
    /// stays cached across the whole library.
    /// </summary>
    public sealed class AssetLabeler
    {
        public static readonly string[] Categories =
        {
            "nature", "building", "structure", "furniture", "prop", "decoration", "vehicle", "character", "creature",
            "obstacle", "platform", "terrain", "light", "sign", "tool", "effect", "food", "other",
        };

        internal const string SystemPrompt =
@"You label free 3D models from the Roblox Creator Store so a game-building AI can find and place them.

You get Roblox's own thumbnail of the model (its true look) and four schematic renders made from the model's actual parts (its true size, shape and orientation; mesh parts show as dashed boxes, so judge their detail from the thumbnail). The renders are labelled FRONT (looking at the model's −Z side), RIGHT (+X side), TOP (the −Z side at the top edge) and a 3/4 view with the exact size in studs. A Roblox character is about 5 studs tall.

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
  ""quality"": 0-10 (10 = polished and usable in a real game; 0-2 = broken, empty or unrecognisable),
  ""issues"": [""problems: floating pieces, missing textures, offensive or copyrighted content, too detailed for mobile, ...""]
}
Be literal and concrete. If it depicts a real brand, franchise character or anything inappropriate for children, say so in issues and give quality 0.";

        private readonly IOmniBloxLlm llm;

        public AssetLabeler(IOmniBloxLlm llm) => this.llm = llm;

        public async Task<LabelResult> LabelAsync(AssetRecord record, byte[]? thumbnail, IReadOnlyList<(string View, byte[] Png)> renders, CancellationToken ct)
        {
            string session = "omniblox-label-" + record.AssetId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
            llm.StartSession(session, SystemPrompt);
            try
            {
                var images = new List<byte[]>();
                if (thumbnail is { Length: > 0 }) images.Add(thumbnail);
                images.AddRange(renders.Select(r => r.Png));
                llm.AppendUser(session, Describe(record, thumbnail != null, renders.Select(r => r.View)), images);
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

        internal static string Describe(AssetRecord r, bool hasThumbnail, IEnumerable<string> views)
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
            sb.AppendLine($"Images, in order: {(hasThumbnail ? "Roblox thumbnail, " : "")}{string.Join(", ", views.Select(v => v + " render"))}.");
            return sb.ToString();
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
