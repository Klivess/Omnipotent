using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Transforms;

namespace Omnipotent.Services.OmniscienceV3.Modules.Links
{
    /// <summary>X23 "Link tracker": an address or a site → who shared it, where, when, and how it was received.</summary>
    public sealed class LinkTrackerTactic : TacticBase
    {
        public const string Key = "x23.link_tracker";
        private readonly LinksModule module;

        public LinkTrackerTactic(LinksModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X23",
            Title = "Link tracker",
            Category = TacticCategory.Transform,
            Description = "Who shared a link (or anything from a site), where and when, who was first, and the reactions it got. Give an address or a site, or select a message that holds a link. " +
                          "Also select servers, conversations or a time range to look only there.",
            Accepts = new[] { SelectionKind.Message, SelectionKind.Guild, SelectionKind.Conversation, SelectionKind.TimeRange },
            MinSelections = 0,
            MaxSelections = 4,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "url":{"type":"string","maxLength":2000,"description":"The address to track."},
                  "site":{"type":"string","maxLength":200,"description":"A site (for example youtube.com), to track every link to it."},
                  "limit":{"type":"integer","minimum":1,"maximum":1000,"default":200}}}
                """,
            Outputs = new[] { TacticOutputKind.Timeline, TacticOutputKind.MessageSet },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.messages.read",
            TypicalDurationMs = 800,
        };

        public override string? Validate(TacticInput input)
        {
            bool url = !string.IsNullOrWhiteSpace(input.Param<string>("url")), site = !string.IsNullOrWhiteSpace(input.Param<string>("site"));
            bool message = input.Selections.Any(s => s.Kind == SelectionKind.Message && s.Count > 0);
            if (url && site) return "Give either 'url' or 'site', not both.";
            if (!url && !site && !message) return "Give the address to track in 'url', a site in 'site', or select a message that holds a link.";
            if (url && LinkParser.Parse(input.Param<string>("url")!.Trim()) == null) return "'url' must be a web address (http or https).";
            if (input.Selections.GroupBy(s => s.Kind).Any(g => g.Count() > 1)) return "Give at most one selection of each kind.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            string? url = context.Input.Param<string>("url")?.Trim(), site = context.Input.Param<string>("site")?.Trim();
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(site))
            {
                var messages = context.Input.Selections.Where(s => s.Kind == SelectionKind.Message).SelectMany(s => s.Items.Select(i => i.Id)).ToList();
                url = module.UrlsOfMessages(messages).FirstOrDefault() ?? throw new TacticRefusedException("The selected message holds no link.");
            }
            var scope = TransformsModule.ScopeOf(context);
            var track = module.Track(string.IsNullOrEmpty(url) ? null : url, string.IsNullOrEmpty(url) ? site : null, scope.Guilds, scope.Conversations, scope.FromMs, scope.ToMs,
                context.Input.Param<int?>("limit") ?? 200);
            string what = track.Link?.Title ?? track.Target;
            string summary = track.Shares == 0 ? $"Nobody has shared {what} there."
                : $"{what}: shared {track.Shares} times by {track.Sharers} people; first by {track.First?.PersonName ?? "someone"}.";
            IReadOnlyList<TacticOutput> outputs = new TacticOutput[]
            {
                new(TacticOutputKind.Timeline, Data: JsonSerializer.SerializeToElement(track, KernelJson.Options), Summary: summary),
                new(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, track.Items.Select(i => i.MessageId).Distinct(StringComparer.Ordinal), "links:shares"), Summary: $"The messages that shared it ({track.Items.Count})"),
            };
            return Task.FromResult(outputs);
        }
    }
}
