using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>
    /// Works out whose a roster handle is, away from the prompt it was written for, and puts the person's name in its
    /// place: in the text of a stored claim or fact, and in the result of a tactic (an answer, a report, a list of plans),
    /// which is built from those facts and from what the model wrote about them.
    /// <para>A handle is cut from the hash of a person id (<see cref="Roster.HandleFor"/>), so the people it could be are
    /// known. Four hex digits are shared by several of an archive's people, and by ordinary words ("paced", "p2000"), so
    /// a handle is only replaced when exactly one of the people it could be belongs there: one of the people the text is
    /// said to concern (Klives is always one), or, failing those, someone who is in a conversation the text comes from
    /// or is mentioned in a message it cites. Anything else is left exactly as it stands.</para>
    /// </summary>
    public sealed class HandleNamer
    {
        private readonly CorpusReader corpus;
        private readonly OmniscienceStores stores;
        private readonly Dictionary<string, string?> names = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Person, string Conversation), bool> members = new();
        private Dictionary<string, List<(string Hash, string Person)>>? byHandle;
        private Dictionary<string, string>? canonical;
        private HashSet<string>? self;

        public HandleNamer(CorpusReader corpus, OmniscienceStores stores)
        {
            this.corpus = corpus;
            this.stores = stores;
        }

        /// <summary>How many handles were given a name, and how many could not be given to one person and stay.</summary>
        public sealed class Tally
        {
            public int Named;
            public int Left;
        }

        /// <summary>The text with each handle that is exactly one person's here replaced by that person's name.</summary>
        /// <param name="concerned">The people the text is about or by (person ids; nulls are skipped).</param>
        /// <param name="conversations">The conversations it comes from.</param>
        /// <param name="messages">The messages it cites.</param>
        public string Text(string text, IEnumerable<string?> concerned, IReadOnlyCollection<string>? conversations = null, IReadOnlyCollection<string>? messages = null, Tally? tally = null)
        {
            if (!HandleText.MayHold(text)) return text;
            Load();
            var close = concerned.OfType<string>().Select(Canonical).Concat(self!).ToHashSet(StringComparer.Ordinal);
            return HandleText.Named(text, handle =>
            {
                string hex = handle[1..];
                if (!byHandle!.TryGetValue("p" + hex[..4], out var bucket)) return null; // nobody's handle: a word that looks like one
                var could = bucket.Where(b => b.Hash.StartsWith(hex, StringComparison.Ordinal)).Select(b => b.Person).Distinct(StringComparer.Ordinal).ToList();
                if (could.Count == 0) return null;
                var near = could.Where(close.Contains).ToList();
                string? person = near.Count == 1 ? near[0] : null;
                if (person == null && near.Count == 0)
                {
                    var there = could.Where(p => IsThere(p, conversations, messages)).ToList();
                    if (there.Count == 1) person = there[0];
                }
                string? name = person == null ? null : NameOf(person);
                if (tally != null) { if (name == null) tally.Left++; else tally.Named++; }
                return name;
            });
        }

        /// <summary>A reference to something in the corpus as results carry them: a message, a conversation ("discord:123…").</summary>
        private static readonly Regex Reference = new(@"^[a-z][a-z0-9_]{1,20}:[^\s]{3,120}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>How many of a result's references are looked up to learn where it comes from.</summary>
        private const int ReferenceLimit = 400;

        /// <summary>
        /// A result as JSON with the handles in its text replaced by names: every string value is looked at, no property name
        /// is touched. Who the result concerns and where it comes from is read from the result itself: the person ids it
        /// carries, the conversations it names and the messages it cites (and their conversations).
        /// Returns the same string when nothing changed, and when it is not JSON.
        /// </summary>
        public string Json(string json, IEnumerable<string?>? concerned = null, Tally? tally = null)
        {
            if (!HandleText.MayHold(json)) return json;
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (JsonException) { return json; }
            if (root == null) return json;
            Load();
            var texts = new List<string>();
            Walk(root, texts.Add, null);
            var people = (concerned ?? Array.Empty<string?>()).OfType<string>().Concat(texts.Where(canonical!.ContainsKey)).Distinct(StringComparer.Ordinal).ToList();
            var (conversations, messages) = Places(texts);
            bool changed = false;
            Walk(root, _ => { }, text =>
            {
                string named = Text(text, people, conversations, messages, tally);
                changed |= !ReferenceEquals(named, text) && !string.Equals(named, text, StringComparison.Ordinal);
                return named;
            });
            return changed ? root.ToJsonString(Kernel.Jobs.KernelJson.Options) : json;
        }

        /// <summary>The person ids a piece of JSON carries (an input, a list of subjects): for <c>concerned</c>.</summary>
        public IReadOnlyList<string> PeopleIn(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (JsonException) { return Array.Empty<string>(); }
            if (root == null) return Array.Empty<string>();
            Load();
            var texts = new List<string>();
            Walk(root, texts.Add, null);
            return texts.Where(canonical!.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>Every string value of a document, in place: seen by <paramref name="see"/>, and replaced by what
        /// <paramref name="rewrite"/> returns when one is given.</summary>
        private static void Walk(JsonNode node, Action<string> see, Func<string, string>? rewrite)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj.ToList())
                    {
                        if (child is JsonValue value && value.TryGetValue(out string? text))
                        {
                            see(text);
                            if (rewrite != null && rewrite(text) is { } named && !string.Equals(named, text, StringComparison.Ordinal)) obj[key] = named;
                        }
                        else if (child != null) Walk(child, see, rewrite);
                    }
                    break;
                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue value && value.TryGetValue(out string? text))
                        {
                            see(text);
                            if (rewrite != null && rewrite(text) is { } named && !string.Equals(named, text, StringComparison.Ordinal)) array[i] = named;
                        }
                        else if (array[i] is { } child) Walk(child, see, rewrite);
                    }
                    break;
            }
        }

        /// <summary>Of a result's strings, the conversations it names and the messages it cites, with those messages' conversations.</summary>
        private (IReadOnlyCollection<string> Conversations, IReadOnlyCollection<string> Messages) Places(IEnumerable<string> texts)
        {
            var references = texts.Where(t => Reference.IsMatch(t)).Distinct(StringComparer.Ordinal).Take(ReferenceLimit).ToList();
            if (references.Count == 0) return (Array.Empty<string>(), Array.Empty<string>());
            using var c = stores.Corpus.OpenRead();
            var conversations = c.QueryIn(null, "SELECT conversation_id FROM conversations WHERE conversation_id IN ({keys})", references, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            var messages = new List<string>();
            foreach (var (message, conversation) in c.QueryIn(null, "SELECT message_id, conversation_id FROM messages WHERE message_id IN ({keys})",
                         references.Where(r => !conversations.Contains(r)).ToList(), r => (r.GetString(0), r.GetString(1))))
            {
                messages.Add(message);
                conversations.Add(conversation);
            }
            return (conversations, messages);
        }

        /// <summary>Every person id of the archive under the handle it would be given, with the person it stands for today.</summary>
        private void Load()
        {
            if (byHandle != null) return;
            var index = new Dictionary<string, List<(string Hash, string Person)>>(StringComparer.Ordinal);
            var stands = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var c = stores.Corpus.OpenRead())
            {
                foreach (var (id, into) in c.Query(null, "SELECT person_id, canonical_id FROM person_canonical", r => (r.GetString(0), r.GetString(1)))) stands[id] = into;
                foreach (string id in c.Query(null, "SELECT person_id FROM persons", r => r.GetString(0))) stands.TryAdd(id, id);
            }
            foreach (var (id, into) in stands)
            {
                var (handle, hash) = HandleText.Of(id);
                (index.TryGetValue(handle, out var list) ? list : index[handle] = new List<(string, string)>()).Add((hash, into));
            }
            self = corpus.SelfPersonIds().Select(p => stands.GetValueOrDefault(p, p)).ToHashSet(StringComparer.Ordinal);
            canonical = stands;
            byHandle = index;
        }

        private string Canonical(string person) => canonical!.GetValueOrDefault(person, person);

        private string? NameOf(string person)
        {
            if (self!.Contains(person)) return "Klives";
            if (names.TryGetValue(person, out var known)) return known;
            string? name = corpus.DisplayName(person);
            return names[person] = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        /// <summary>Whether the person is in one of the conversations, or is mentioned in one of the messages.</summary>
        private bool IsThere(string person, IReadOnlyCollection<string>? conversations, IReadOnlyCollection<string>? messages)
        {
            bool anywhere = conversations is { Count: > 0 }, cited = messages is { Count: > 0 };
            if (!anywhere && !cited) return false;
            var identities = corpus.IdentitiesOf(corpus.PersonIdsOf(person)).Select(i => i.IdentityId).ToList();
            if (identities.Count == 0) return false;
            using var c = stores.Corpus.OpenRead();
            if (anywhere)
                foreach (string conversation in conversations!)
                {
                    if (!members.TryGetValue((person, conversation), out bool member))
                        members[(person, conversation)] = member = c.QueryIn(null, "SELECT 1 FROM conversation_members WHERE conversation_id=$c AND identity_id IN ({keys}) LIMIT 1",
                            identities, r => r.GetInt32(0), 400, ("$c", conversation)).Count > 0;
                    if (member) return true;
                }
            if (!cited) return false;
            var mentioned = c.QueryIn(null, "SELECT target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", messages!.Distinct(StringComparer.Ordinal).ToList(), r => r.GetString(0));
            return mentioned.Any(identities.Contains);
        }
    }
}
